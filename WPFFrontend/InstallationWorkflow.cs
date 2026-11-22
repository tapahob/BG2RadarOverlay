using System;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using static BGOverlay.RadarLocalization;

namespace WPFFrontend;

public partial class MainWindow
{
    private readonly InstallationRegistry installations = new();
    private readonly SemaphoreSlim installationAction = new(1);
    private bool registryLoaded;

    private async Task RunInstallationAction(Func<Task> action)
    {
        await installationAction.WaitAsync();
        try { await action(); }
        catch (Exception ex)
        {
            InstallationDialog.Ask(this, Get("Str_NativeSetup"), InstallationDialog.DescribeError(ex),
                Get("Str_InstallationOK"));
        }
        finally { installationAction.Release(); }
    }

    private async Task CheckSavedInstallations()
    {
        await RunInstallationAction(() =>
        {
            installations.Load();
            registryLoaded = true;
            return Task.CompletedTask;
        });
        if (!registryLoaded) return;
        foreach (var game in installations.Games.ToArray())
        {
            if (Dispatcher.HasShutdownStarted) return;
            await RunInstallationAction(() =>
            {
                RefreshSavedInstallation(installations.Find(game.Executable));
                return Task.CompletedTask;
            });
        }
    }

    private void RefreshSavedInstallation(GameInstallation game)
    {
        if (!File.Exists(game.Executable) || !game.HasNativeModule) return;
        if (!game.Native && !File.Exists(game.CompanionConfig) && NativeInstallation.LinkDestination(game) == null) return;
        var hasEEex = game.HasEEex;
        var newlyDetected = NativeInstallation.Refresh(game, hasEEex);
        if (game.LastKnownEEex != hasEEex)
            installations.Save(game with { LastKnownEEex = hasEEex });
        if (newlyDetected)
            InstallationDialog.Ask(this, Get("Str_EEexPatchDetectedTitle"),
                Get("Str_EEexPatchDetectedDetails") + "\n\n" + game.Folder, Get("Str_InstallationOK"));
    }

    private Task DetectInstallation(string executable, int gameProcessId)
    {
        if (!registryLoaded) return Task.CompletedTask;
        var saved = installations.Find(executable);
        if (saved != null)
        {
            if (saved.LastKnownEEex != saved.HasEEex) RefreshSavedInstallation(saved);
            return Task.CompletedTask;
        }
        var game = GameInstallation.FromExecutable(executable);
        InstallationDialog.ShowProgress(this, game, NativeInstallation.RadarExe, gameProcessId,
            hasEEex => installations.Save(game with { Native = true, LastKnownEEex = hasEEex }));
        return Task.CompletedTask;
    }

    private async Task ToggleNativeInstallation(GameInstallation game)
    {
        if (!game.HasNativeModule)
        {
            await InstallNative(game);
            return;
        }

        if (!InstallationDialog.Ask(this, string.Format(Get("Str_NativeRemoveTitle"), game.Name), game.Folder,
                Get("Str_NativeRemove"), Get("Str_InstallationCancel"))) return;

        try
        {
            NativeInstallation.Remove(game);
            installations.Save(game with { Native = false });
        }
        finally { _options.InstallationList.Items.Refresh(); }

        using var process = FindRunningGame(game);
        if (process != null)
            InstallationDialog.Ask(this, Get("Str_NativeRemovedTitle"), Get("Str_NativeRemovedDetails"),
                Get("Str_InstallationOK"));
    }

    private async Task InstallNative(GameInstallation game)
    {
        var hasEEex = game.HasEEex;
        NativeInstallation.Install(game, NativeInstallation.RadarExe, null, hasEEex);
        installations.Save(game with { Native = true, LastKnownEEex = hasEEex });
        using var process = FindRunningGame(game);
        if (process == null)
        {
            InstallationDialog.Ask(this, string.Format(Get("Str_NativeReadyTitle"), game.Name),
                Get("Str_NativeReadyDetails"), Get("Str_InstallationOK"));
            return;
        }
        if (InstallationDialog.Ask(this, string.Format(Get("Str_NativeRestartTitle"), game.Name),
                string.Format(Get("Str_NativeRestartDetails"), game.Folder),
                Get("Str_NativeRestartNow"), Get("Str_InstallationLater")))
            await ((App)Application.Current).Restart(game, process);
    }

    private static Process FindRunningGame(GameInstallation game)
    {
        Process match = null;
        foreach (var process in Process.GetProcessesByName(Path.GetFileNameWithoutExtension(game.GameExe)))
        {
            try
            {
                if (match == null && NativeInstallation.SamePath(process.MainModule?.FileName, game.Executable))
                    match = process;
            }
            catch (InvalidOperationException) { }
            catch (Win32Exception) { }
            finally { if (process != match) process.Dispose(); }
        }
        return match;
    }
}
