using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using BGOverlay;
using static BGOverlay.RadarLocalization;

namespace WPFFrontend;

internal sealed record GameInstallation(string Name, string Executable, bool Native = false)
{
    // Older installations.json entries have no EEex history.
    public bool? LastKnownEEex { get; init; }
    internal bool HasEEex => File.Exists(Path.Combine(Folder, "EEex.dll"));
    [JsonIgnore]
    public string Folder => Path.GetDirectoryName(Executable);
    internal string GameExe => Path.GetFileName(Executable);
    internal string Link => Path.Combine(Folder, "BGRadar", "BG Radar Overlay.exe");

    [JsonIgnore]
    public bool HasNativeModule => NativeInstallation.ModulePaths(this).Any(File.Exists);

    [JsonIgnore]
    public string IconLabel => Name switch
    {
        "BGII: EE" => "BG2",
        "IWD: EE" => "IWD",
        _ => "BG"
    };

    internal static GameInstallation FromExecutable(string executable)
    {
        // The display label is independent of the registry key (the full executable path).
        var name = "IWD: EE";
        if (!NativeInstallation.SamePath(Path.GetFileName(executable), "icewind.exe"))
        {
            var product = FileVersionInfo.GetVersionInfo(executable).ProductName ?? "";
            name = product.Contains("Gate II", StringComparison.OrdinalIgnoreCase) ? "BGII: EE" : "BG: EE";
        }
        return new GameInstallation(name, Path.GetFullPath(executable));
    }
}

// Manual and game-launched companions share the file beside the resolved Radar executable.
internal sealed class InstallationRegistry
{
    private const string FileName = "installations.json";
    private readonly string file = Path.Combine(Path.GetDirectoryName(NativeInstallation.RadarExe), FileName);
    internal ObservableCollection<GameInstallation> Games { get; } = new();

    internal void Load()
    {
        if (!File.Exists(file)) return;

        var games = JsonSerializer.Deserialize<List<GameInstallation>>(File.ReadAllText(file));
        var loaded = new List<GameInstallation>();
        foreach (var game in games ?? throw new InvalidDataException(Get("Str_NativeInvalidList")))
        {
            if (game == null || string.IsNullOrWhiteSpace(game.Executable) || !Path.IsPathFullyQualified(game.Executable)
                || !(NativeInstallation.SamePath(game.GameExe, "Baldur.exe") || NativeInstallation.SamePath(game.GameExe, "icewind.exe")))
                throw new InvalidDataException(Get("Str_NativeInvalidEntry"));
            if (!loaded.Any(entry => NativeInstallation.SamePath(entry.Executable, game.Executable))) loaded.Add(game);
        }
        foreach (var game in loaded) Games.Add(game);
    }

    internal GameInstallation Find(string executable) => Games.FirstOrDefault(g => NativeInstallation.SamePath(g.Executable, executable));

    internal void Save(GameInstallation game)
    {
        var index = Games.IndexOf(Find(game.Executable));
        var snapshot = Games.ToList();
        if (index < 0) snapshot.Add(game); else snapshot[index] = game;
        Write(snapshot);
        if (index < 0) Games.Add(game); else Games[index] = game;
    }

    private void Write(List<GameInstallation> games)
    {
        File.WriteAllText(file + ".tmp", JsonSerializer.Serialize(games, new JsonSerializerOptions { WriteIndented = true }));
        File.Move(file + ".tmp", file, true);
    }
}

internal static class NativeInstallation
{
    private const string CriticalScript = "M_RDRCRT.lua";
    internal static bool SamePath(string a, string b) => string.Equals(a, b, StringComparison.OrdinalIgnoreCase);
    internal static string RadarExe => new FileInfo(Environment.ProcessPath).ResolveLinkTarget(true)?.FullName ?? Environment.ProcessPath;

    internal static IEnumerable<string> ModulePaths(GameInstallation game)
    {
        yield return Path.Combine(game.Folder, "BG2Radar.asi");
        yield return Path.Combine(game.Folder, "scripts", "BG2Radar.asi");
    }

    internal static void Remove(GameInstallation game)
    {
        CleanModuleBackups(game);
        foreach (var module in ModulePaths(game).Where(File.Exists))
            File.Delete(module);
        var script = Path.Combine(game.Folder, "override", CriticalScript);
        if (File.Exists(script)) File.Delete(script);

        // winmm.dll is a shared ASI loader and may still be needed by other mods.
        if (new FileInfo(game.Link).LinkTarget != null)
            File.Delete(game.Link);
    }

    internal static string LinkDestination(GameInstallation game)
    {
        var link = new FileInfo(game.Link);
        return link.LinkTarget == null ? null : link.ResolveLinkTarget(true)?.FullName;
    }

    internal static bool Refresh(GameInstallation game, bool hasEEex)
    {
        // For a pre-upgrade entry, an existing patch is the best evidence that
        // this installation already used EEex. A routine repair isn't a new install.
        var previouslyHadEEex = game.LastKnownEEex ?? File.Exists(Path.Combine(game.Folder, "override", CriticalScript));
        if (hasEEex)
            Install(game, RadarExe, null, installLoader: false, hasEEex: true);
        else
        {
            CleanModuleBackups(game);
            SetLink(game, RadarExe);
        }
        return hasEEex && !previouslyHadEEex;
    }

    private static void SetLink(GameInstallation game, string radarExe)
    {
        var link = new FileInfo(game.Link);
        var destination = LinkDestination(game);
        if (SamePath(destination, radarExe)) return;
        if (destination == null && link.Exists)
            throw new IOException(string.Format(Get("Str_NativeLinkOccupied"), link.FullName));
        Directory.CreateDirectory(link.DirectoryName);
        var temp = game.Link + "." + Guid.NewGuid().ToString("N");
        try
        {
            File.CreateSymbolicLink(temp, radarExe);
            File.Move(temp, game.Link, destination != null);
        }
        finally { File.Delete(temp); }
    }

    private static IEnumerable<string> PayloadTargets(GameInstallation game, bool installLoader, bool hasEEex)
    {
        if (installLoader) yield return Path.Combine(game.Folder, "winmm.dll");
        yield return ModulePaths(game).FirstOrDefault(File.Exists) ?? Path.Combine(game.Folder, "BG2Radar.asi");
        if (hasEEex)
            yield return Path.Combine(game.Folder, "override", CriticalScript);
    }

    internal static void Install(GameInstallation game, string radarExe, Action injectorInstalled, bool hasEEex,
        bool installLoader = true)
    {
        if (!File.Exists(game.Executable))
            throw new IOException(string.Format(Get("Str_NativeGameMissing"), game.Folder));
        CleanModuleBackups(game);
        var source = Path.Combine(Path.GetDirectoryName(radarExe), "GameFiles");
        var staged = new List<(string Temp, string Target, string Backup)>();
        var committed = 0;
        try
        {
            foreach (var target in PayloadTargets(game, installLoader, hasEEex))
            {
                var name = Path.GetFileName(target);
                var payload = Path.Combine(source, name);
                if (File.Exists(target))
                {
                    using var existing = File.OpenRead(target);
                    using var bundled = File.OpenRead(payload);
                    if (SHA256.HashData(existing).SequenceEqual(SHA256.HashData(bundled))) continue;
                    if (name == "winmm.dll")
                        throw new IOException(string.Format(Get("Str_NativeLoaderConflict"), game.Folder));
                }
                string temp = target + "." + Guid.NewGuid().ToString("N");
                Directory.CreateDirectory(Path.GetDirectoryName(target));
                staged.Add((temp, target, File.Exists(target) ? temp + ".bak" : null));
                File.Copy(payload, temp);
            }
            foreach (var item in staged)
            {
                if (item.Backup == null) File.Move(item.Temp, item.Target);
                else File.Replace(item.Temp, item.Target, item.Backup);
                committed++;
            }
            injectorInstalled?.Invoke();
            SetLink(game, radarExe);
        }
        catch
        {
            for (int i = committed - 1; i >= 0; i--)
            {
                var item = staged[i];
                if (item.Backup == null) File.Delete(item.Target);
                else File.Move(item.Backup, item.Target, true);
            }
            throw;
        }
        finally { foreach (var item in staged) File.Delete(item.Temp); }
        foreach (var item in staged)
        {
            if (item.Backup != null) TryDeleteBackup(item.Backup);
        }
    }

    // A mapped ASI can stay locked until the game exits. Retry before the next
    // transaction, when no rollback files for that transaction exist yet.
    private static void CleanModuleBackups(GameInstallation game)
    {
        foreach (var module in ModulePaths(game).Where(File.Exists))
        {
            string prefix = Path.GetFileName(module) + ".";
            try
            {
                foreach (var file in Directory.EnumerateFiles(Path.GetDirectoryName(module), prefix + "*.bak"))
                {
                    string name = Path.GetFileNameWithoutExtension(file);
                    if (!name.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) continue;
                    string id = name.Substring(prefix.Length);
                    if (Guid.TryParseExact(id, "N", out _) || Guid.TryParseExact(id, "D", out _))
                        TryDeleteBackup(file);
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                Logger.Error($"Could not check for old backups of '{module}'", ex);
            }
        }
    }

    private static void TryDeleteBackup(string path)
    {
        try { File.Delete(path); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Logger.Error($"Could not remove the previous installation file at '{path}'", ex);
        }
    }
}
