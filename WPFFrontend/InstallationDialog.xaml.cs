using System;
using System.ComponentModel;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Threading;
using BGOverlay;
using WinApiBindings;

namespace WPFFrontend;

internal partial class InstallationDialog : Window
{
    private const string Checkmark = "\u2713";
    private bool installing;

    private InstallationDialog(Window owner, string title, string accept, string decline = null)
    {
        InitializeComponent();
        string close = RadarLocalization.Get("Str_InstallationClose");
        CloseButton.ToolTip = close;
        AutomationProperties.SetName(CloseButton, close);
        if (owner.IsVisible) Owner = owner;
        else WindowStartupLocation = WindowStartupLocation.CenterScreen;
        Question.Text = title;
        Accept.Content = accept;
        Decline.Content = decline;
        if (decline == null) Decline.Visibility = Visibility.Collapsed;
    }

    internal static bool Ask(Window owner, string question, string details, string accept, string decline = null)
    {
        var dialog = new InstallationDialog(owner, question, accept, decline);
        dialog.Details.Text = details;
        return dialog.ShowDialog() == true;
    }

    internal static void ShowProgress(Window owner, GameInstallation game, string radarExe, int gameProcessId,
        Action<bool> saveInstallation)
    {
        var dialog = new InstallationDialog(owner, RadarLocalization.Get("Str_NativeSetup"),
            RadarLocalization.Get("Str_InstallationOK"));
        dialog.Details.Visibility = Visibility.Collapsed;
        dialog.InstallationProgress.Visibility = Visibility.Visible;
        bool hasEEex = game.HasEEex;
        if (hasEEex) dialog.EEexPatchStep.Visibility = Visibility.Visible;
        dialog.DetectedGame.Text = string.Format(RadarLocalization.Get("Str_NativeProgressDetected"), game.Name);
        dialog.GamePath.Text = game.Folder;
        dialog.RadarPath.Text = radarExe;
        dialog.InstallationStatus.Text = RadarLocalization.Get("Str_NativeProgressInstalling");
        dialog.installing = true;
        dialog.CloseButton.IsEnabled = dialog.Accept.IsEnabled = false;

        async void InstallAfterRender(object sender, EventArgs e)
        {
            dialog.ContentRendered -= InstallAfterRender;
            void MarkInjectorInstalled() => dialog.Dispatcher.Invoke(
                () =>
                {
                    dialog.InjectorMark.Text = Checkmark;
                    if (hasEEex) dialog.EEexPatchMark.Text = Checkmark;
                }, DispatcherPriority.Background);

            try
            {
                await Task.Run(() => NativeInstallation.Install(game, radarExe, MarkInjectorInstalled, hasEEex));
                dialog.AutostartMark.Text = Checkmark;
                saveInstallation(hasEEex);
                dialog.InstallationStatus.Text = RadarLocalization.Get("Str_NativeProgressRestart");
            }
            catch (Exception ex)
            {
                if (dialog.AutostartMark.Text != Checkmark)
                {
                    dialog.InjectorMark.Text = string.Empty;
                    dialog.EEexPatchMark.Text = string.Empty;
                }
                dialog.InstallationStatus.Text = RadarLocalization.Get("Str_NativeProgressFailed")
                    + "\n" + DescribeError(ex);
            }
            finally
            {
                dialog.installing = false;
                dialog.CloseButton.IsEnabled = dialog.Accept.IsEnabled = true;
            }
        }

        dialog.ContentRendered += InstallAfterRender;
        var focusTimer = dialog.KeepAboveLaunchingGame(gameProcessId);
        try { dialog.ShowDialog(); }
        finally { focusTimer.Stop(); }
    }

    internal static string DescribeError(Exception ex)
    {
        Logger.Error("Native radar installation", ex);
        return ex.Message;
    }

    private DispatcherTimer KeepAboveLaunchingGame(int gameProcessId)
    {
        var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(250) };
        var window = new WindowInteropHelper(this);
        timer.Tick += (_, _) =>
        {
            if (!IsVisible) return;
            var foreground = WinAPIBindings.GetForegroundWindow();
            if (foreground == IntPtr.Zero ||
                WinAPIBindings.GetWindowThreadProcessId(foreground, out uint foregroundProcessId) == 0 ||
                foregroundProcessId != gameProcessId) return;

            // The game can raise its window after ShowDialog has already activated this window.
            var topmost = new IntPtr(-1); // HWND_TOPMOST
            const uint positionFlags = 0x0001 | 0x0002 | 0x0010 | 0x0200;
            // SWP_NOSIZE | SWP_NOMOVE | SWP_NOACTIVATE | SWP_NOOWNERZORDER
            WinAPIBindings.SetWindowPos(window.Handle, topmost, 0, 0, 0, 0, positionFlags);
            Activate();
        };
        timer.Start();
        return timer;
    }

    private void Accept_Click(object sender, RoutedEventArgs e) => DialogResult = true;
    private void Decline_Click(object sender, RoutedEventArgs e) => DialogResult = false;

    private void Close_Click(object sender, RoutedEventArgs e) => Close();

    protected override void OnClosing(CancelEventArgs e)
    {
        base.OnClosing(e);
        if (installing) e.Cancel = true;
    }

    private void Dialog_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Escape) return;
        e.Handled = true;
        Close();
    }
}
