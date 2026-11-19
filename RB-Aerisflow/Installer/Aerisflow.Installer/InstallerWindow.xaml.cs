using System;
using System.Diagnostics;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;

namespace Aerisflow.Installer;

public partial class InstallerWindow : Window
{
    private readonly InstallEngine _engine = new InstallEngine();
    private readonly bool _uninstallMode;
    private bool _busy;
    private bool _installed;
    private bool _done;
    private bool _loadingNotes;
    private string _releaseUrl = "";

    public InstallerWindow(bool uninstallMode)
    {
        InitializeComponent();
        _uninstallMode = uninstallMode;

        InstallPathText.Text = _engine.InstallDir;
        InstallPathText.ToolTip = _engine.InstallDir;
        VersionText.Text = string.IsNullOrWhiteSpace(_engine.PayloadVersion) ? "" : "v" + _engine.PayloadVersion;

        SourceInitialized += OnSourceInitialized;
        Loaded += OnLoaded;
        RefreshState();
    }

    private void OnSourceInitialized(object? sender, EventArgs e)
    {
        IntPtr hwnd = new WindowInteropHelper(this).Handle;
        WindowEffects.ApplyModernChrome(hwnd);
        WindowEffects.ApplyAcrylic(hwnd);
    }

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        var fade = new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(220))
        {
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut },
        };
        RootBorder.BeginAnimation(OpacityProperty, fade);

        var scale = new DoubleAnimation(0.97, 1.0, TimeSpan.FromMilliseconds(220))
        {
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut },
        };
        RootScale.BeginAnimation(ScaleTransform.ScaleXProperty, scale);
        RootScale.BeginAnimation(ScaleTransform.ScaleYProperty, scale);

        if (_uninstallMode)
        {
            NotesSection.Visibility = Visibility.Collapsed;
            return;
        }
        await LoadNotesAsync();
    }

    private async Task LoadNotesAsync()
    {
        if (_loadingNotes) return;
        _loadingNotes = true;
        NotesStatus.Text = "Loading…";
        NotesRetryButton.Visibility = Visibility.Collapsed;
        NotesText.Text = "Fetching release notes from GitHub…";

        try
        {
            string version = _engine.PayloadVersion;
            var release = await ReleaseFeed.ForVersionAsync(version);
            if (release == null)
            {
                NotesStatus.Text = "Offline";
                NotesText.Text = "Release notes are unavailable right now. " +
                "They are published on GitHub with every release.";
                NotesRetryButton.Visibility = Visibility.Visible;
                return;
            }

            _releaseUrl = release.Url;
            if (!string.IsNullOrWhiteSpace(release.Version)
            && !string.Equals(release.Version, version, StringComparison.OrdinalIgnoreCase))
            {
                VersionText.Text = "v" + release.Version;
            }

            NotesStatus.Text = string.Equals(release.Version, version, StringComparison.OrdinalIgnoreCase)
            ? "From GitHub"
            : "Latest on GitHub";
            NotesText.Text = string.IsNullOrWhiteSpace(release.Notes)
            ? "This release has no notes."
            : release.Notes;
        }
        catch
        {
            NotesStatus.Text = "Offline";
            NotesText.Text = "Release notes are unavailable right now.";
            NotesRetryButton.Visibility = Visibility.Visible;
        }
        finally
        {
            _loadingNotes = false;
        }
    }

    private async void NotesRetryButton_Click(object sender, RoutedEventArgs e) => await LoadNotesAsync();

    private void NotesOpenButton_Click(object sender, RoutedEventArgs e)
    {
        string url = string.IsNullOrWhiteSpace(_releaseUrl)
        ? "https://github.com/RealBibus/Aerisflow/releases"
        : _releaseUrl;
        try
        {
            Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
        }
        catch { }
    }

    private void RefreshState()
    {
        _installed = _engine.IsInstalled;
        string version = _engine.InstalledVersion;

        if (_uninstallMode)
        {
            StateText.Text = _installed ? "Installed" : "Not installed";
            StateHint.Text = _installed
            ? (string.IsNullOrWhiteSpace(version) ? "Ready to remove" : "v" + version)
            : "Nothing to remove";
            StatusText.Text = _installed ? "Ready to remove Aerisflow." : "Aerisflow is not installed.";
            InstallButton.Content = "Uninstall";
            InstallButton.IsEnabled = _installed;
            UninstallButton.Visibility = Visibility.Collapsed;
            FootnoteText.Text = "Your snow settings will be removed with the app.";
            if (!string.IsNullOrWhiteSpace(version)) VersionText.Text = "v" + version;
            return;
        }

        if (_installed)
        {
            StateText.Text = "Installed";
            StateHint.Text = string.IsNullOrWhiteSpace(version) ? "Ready to install again" : "v" + version;
            StatusText.Text = "Aerisflow is already installed. Installing again replaces the files and keeps your settings.";
            InstallButton.Content = "Install";
            UninstallButton.Visibility = Visibility.Visible;
        }
        else
        {
            StateText.Text = "Not installed";
            StateHint.Text = "Per-user install · no admin needed";
            StatusText.Text = "Ready to install.";
            InstallButton.Content = "Install";
            UninstallButton.Visibility = Visibility.Collapsed;
        }
    }

    private async void InstallButton_Click(object sender, RoutedEventArgs e)
    {
        if (_busy) return;

        if (_done)
        {
            try { _engine.Launch(); } catch { }
            Close();
            return;
        }

        if (_uninstallMode)
        {
            await UninstallAsync();
            return;
        }

        var options = new InstallOptions(
        StartWithWindows: AutoStartToggle.IsChecked == true,
        DesktopShortcut: DesktopToggle.IsChecked == true,
        StartMenuShortcut: StartMenuToggle.IsChecked == true,
        LaunchAfterInstall: LaunchToggle.IsChecked == true);

        SetBusy(true);
        FootnoteText.Text = "Installing. Your existing snow settings are preserved.";
        try
        {
            var progress = new Progress<InstallProgress>(OnProgress);
            await Task.Run(() => _engine.Install(options, progress));

            _installed = true;
            _done = true;
            InstallProgress.Value = 1;
            PercentText.Text = "100%";
            StatusText.Text = "Aerisflow is installed.";
            InstallButton.Content = "Launch";
            CloseButton.Content = "Close";
            StateText.Text = "Installed";
            StateHint.Text = string.IsNullOrWhiteSpace(_engine.InstalledVersion)
            ? "Per-user install · no admin needed"
            : "v" + _engine.InstalledVersion;
            UninstallButton.Visibility = Visibility.Visible;
            FootnoteText.Text = "Open the tray icon to change intensity, frost, haze, bass reaction and more.";

            string version = _engine.InstalledVersion;
            if (!string.IsNullOrWhiteSpace(version)) VersionText.Text = "v" + version;
        }
        catch (Exception ex)
        {
            StatusText.Text = "Install failed";
            FootnoteText.Text = ex.Message;
        }
        finally
        {
            SetBusy(false);
        }
    }

    private async void UninstallButton_Click(object sender, RoutedEventArgs e)
    {
        if (_busy) return;
        await UninstallAsync();
    }

    private async Task UninstallAsync()
    {
        SetBusy(true);
        FootnoteText.Text = "Removing Aerisflow from this computer.";
        try
        {
            var progress = new Progress<InstallProgress>(OnProgress);
            await Task.Run(() => _engine.Uninstall(progress));
            StatusText.Text = "Aerisflow was removed.";
            PercentText.Text = "";
            InstallProgress.Value = 0;
            FootnoteText.Text = "You can close this window now.";
            InstallButton.IsEnabled = false;
            await Task.Delay(900);
            Close();
        }
        catch (Exception ex)
        {
            StatusText.Text = "Uninstall failed";
            FootnoteText.Text = ex.Message;
            SetBusy(false);
        }
    }

    private void OnProgress(InstallProgress progress)
    {
        InstallProgress.Value = Math.Clamp(progress.Fraction, 0, 1);
        StatusText.Text = progress.Message;
        PercentText.Text = progress.Fraction > 0 ? $"{Math.Round(progress.Fraction * 100)}%" : "";
    }

    private void SetBusy(bool busy)
    {
        _busy = busy;
        InstallButton.IsEnabled = !busy;
        UninstallButton.IsEnabled = !busy;
        CloseButton.IsEnabled = !busy;
        HeaderCloseButton.IsEnabled = !busy;
        AutoStartToggle.IsEnabled = !busy;
        DesktopToggle.IsEnabled = !busy;
        StartMenuToggle.IsEnabled = !busy;
        LaunchToggle.IsEnabled = !busy;
    }

    private void CloseButton_Click(object sender, RoutedEventArgs e)
    {
        if (_busy) return;
        Close();
    }

    private void Window_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ButtonState != MouseButtonState.Pressed) return;
        if (FindButton(e.OriginalSource as DependencyObject) != null) return;
        try { DragMove(); } catch { }
    }

    private static ButtonBase? FindButton(DependencyObject? source)
    {
        while (source != null)
        {
            if (source is ButtonBase button) return button;
            source = VisualTreeHelper.GetParent(source);
        }
        return null;
    }
}