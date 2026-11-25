using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using BGOverlay;

namespace WPFFrontend;

internal static class ReleaseUpdater
{
    private const string Repo = "tapahob/BG2RadarOverlay";
    private const string ExeName = "BG Radar Overlay.exe";
    private const string LauncherVariable = "BG2RADAR_UPDATE_LAUNCHER";
    private const string LocalesVariable = "BG2RADAR_CUSTOM_LOCALES";
    private static readonly Version Current = typeof(ReleaseUpdater).Assembly.GetName().Version;
    private static readonly HttpClient Client = new() { Timeout = TimeSpan.FromMinutes(15) };
    internal static string LauncherPath { get; private set; }

    // This app runs elevated. Only launch cached programs from the administrator-controlled
    // Program Files tree. Independent portable installations have separate update histories.
    private static string CacheFor(string launcher)
    {
        var key = SHA256.HashData(Encoding.UTF8.GetBytes(launcher.ToUpperInvariant()));
        return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
            "BG2RadarOverlay", "Updates", Convert.ToHexString(key));
    }

    internal static bool LaunchPreparedVersion(string[] args)
    {
        try
        {
            var running = NativeInstallation.RunningExe;
            var origin = Environment.GetEnvironmentVariable(LauncherVariable);
            if (!string.IsNullOrEmpty(origin) && Path.IsPathFullyQualified(origin) &&
                NativeInstallation.SamePath(running, Path.Combine(CacheFor(origin), Current.ToString(), ExeName)))
            {
                LauncherPath = origin;
                RadarLocalization.UseCustomLocales(Path.GetDirectoryName(origin),
                    Environment.GetEnvironmentVariable(LocalesVariable));
                return false;
            }

            LauncherPath = running;
            var cache = CacheFor(running);
            if (!Directory.Exists(cache)) return false;
            foreach (var version in CachedVersions(cache).Where(v => v > Current).OrderByDescending(v => v))
            {
                var exe = Path.Combine(cache, version.ToString(), ExeName);
                if (!IsCompleteRelease(Path.GetDirectoryName(exe), version)) continue;
                var start = new ProcessStartInfo(exe) { WorkingDirectory = Environment.CurrentDirectory };
                foreach (var arg in args) start.ArgumentList.Add(arg);
                start.Environment[LauncherVariable] = running;
                start.Environment[LocalesVariable] = RadarLocalization.FindCustomLocales(Path.GetDirectoryName(running));
                using var child = Process.Start(start);
                if (child != null) return true;
            }
        }
        catch (Exception ex)
        {
            Logger.Init();
            Logger.Error("Could not start the prepared radar update", ex);
        }
        return false;
    }

    private static Version[] CachedVersions(string cache) => Directory.GetDirectories(cache)
        .Select(Path.GetFileName)
        .Select(name => Version.TryParse(name, out var v) && v.ToString() == name ? v : null)
        .Where(v => v != null).ToArray();

    private static bool IsCompleteRelease(string folder, Version version)
    {
        var exe = Path.Combine(folder, ExeName);
        return File.Exists(exe) && Version.TryParse(FileVersionInfo.GetVersionInfo(exe).FileVersion, out var actual)
            && actual == version && File.Exists(Path.Combine(folder, "Locales", "en_US.txt"))
            && File.Exists(Path.Combine(folder, "GameFiles", "BG2Radar.asi"))
            && File.Exists(Path.Combine(folder, "GameFiles", "winmm.dll"))
            && File.Exists(Path.Combine(folder, "GameFiles", NativeInstallation.CriticalScript));
    }

    internal static async Task CheckPeriodically(CancellationToken stop)
    {
        while (!stop.IsCancellationRequested)
        {
            try
            {
                await CheckForUpdate(Client, CacheFor(NativeInstallation.RadarExe), stop);
            }
            catch (OperationCanceledException) when (stop.IsCancellationRequested) { return; }
            catch (Exception ex) { Logger.Error("Radar update check failed", ex); }
            await Task.Delay(TimeSpan.FromHours(6), stop);
        }
    }

    internal static async Task CheckForUpdate(HttpClient client, string cache, CancellationToken stop)
    {
        Directory.CreateDirectory(cache);
        // Also serializes downloads by different Windows sessions sharing one installation.
        using var gate = new FileStream(Path.Combine(cache, "download.lock"), FileMode.OpenOrCreate,
            FileAccess.Write, FileShare.None);
        foreach (var partial in Directory.GetDirectories(cache, "partial-*"))
            TryCleanUp(partial);
        foreach (var old in CachedVersions(cache).Where(v => v < Current))
            TryCleanUp(Path.Combine(cache, old.ToString()));

        using var request = new HttpRequestMessage(HttpMethod.Get, $"https://api.github.com/repos/{Repo}/releases/latest");
        request.Headers.UserAgent.ParseAdd("BG2RadarOverlay/" + Current);
        using var reply = await client.SendAsync(request, stop);
        reply.EnsureSuccessStatusCode();
        using var release = JsonDocument.Parse(await reply.Content.ReadAsStreamAsync(stop));
        var data = release.RootElement;
        var tag = data.GetProperty("tag_name").GetString();
        if (data.GetProperty("draft").GetBoolean() || data.GetProperty("prerelease").GetBoolean() ||
            !Version.TryParse(tag?.TrimStart('v', 'V'), out var version) || version <= Current) return;

        var destination = Path.Combine(cache, version.ToString());
        if (IsCompleteRelease(destination, version)) return;
        var archiveName = $"BG.Radar.Overlay.{version}.7z";
        var asset = data.GetProperty("assets").EnumerateArray().FirstOrDefault(a =>
            string.Equals(a.GetProperty("name").GetString()?.Replace(' ', '.'), archiveName, StringComparison.OrdinalIgnoreCase));
        if (asset.ValueKind == JsonValueKind.Undefined)
            throw new InvalidDataException($"Release {tag} has no {archiveName}.");
        var digest = asset.GetProperty("digest").GetString();
        if (digest == null || !digest.StartsWith("sha256:", StringComparison.Ordinal) || digest.Length != 71)
            throw new InvalidDataException($"Release {tag} is missing its SHA-256 digest.");

        var work = Directory.CreateDirectory(Path.Combine(cache, "partial-" + Guid.NewGuid().ToString("N"))).FullName;
        try
        {
            var archive = Path.Combine(work, "release.7z");
            var name = Uri.EscapeDataString(asset.GetProperty("name").GetString());
            var url = $"https://github.com/{Repo}/releases/download/{Uri.EscapeDataString(tag)}/{name}";
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(stop);
            deadline.CancelAfter(TimeSpan.FromMinutes(15));
            using var download = await client.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, deadline.Token);
            download.EnsureSuccessStatusCode();
            using (var file = new FileStream(archive, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None))
            {
                await download.Content.CopyToAsync(file, deadline.Token);
                file.Position = 0;
                if (file.Length != asset.GetProperty("size").GetInt64() ||
                    !Convert.ToHexString(await SHA256.HashDataAsync(file, deadline.Token))
                        .Equals(digest.Substring(7), StringComparison.OrdinalIgnoreCase))
                    throw new InvalidDataException("Radar archive failed its integrity check.");
            }

            var payload = Directory.CreateDirectory(Path.Combine(work, "payload")).FullName;
            var extract = new ProcessStartInfo(Path.Combine(Environment.SystemDirectory, "tar.exe"))
            {
                CreateNoWindow = true, WorkingDirectory = payload, ArgumentList = { "-xf", archive }
            };
            using var tar = Process.Start(extract);
            await tar.WaitForExitAsync();
            stop.ThrowIfCancellationRequested();
            if (tar.ExitCode != 0 || !IsCompleteRelease(payload, version))
                throw new InvalidDataException($"Incomplete or mismatched radar package for {tag}.");

            // A directory rename on the same volume makes the complete release visible at once.
            if (Directory.Exists(destination)) Directory.Delete(destination, true);
            Directory.Move(payload, destination);
            Logger.Info($"Radar {version} is ready for the next launch.");
        }
        finally { Directory.Delete(work, true); }
    }

    private static void TryCleanUp(string folder)
    {
        try
        {
            // Windows refuses to delete a running executable. Leave the rest of that
            // release intact if another session still uses it.
            File.Delete(Path.Combine(folder, ExeName));
            Directory.Delete(folder, true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Logger.Error($"Could not remove old radar update folder {folder}", ex);
        }
    }
}
