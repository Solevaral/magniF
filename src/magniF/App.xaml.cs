using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Interop;
using System.Windows.Threading;
using magniF.Core;
using magniF.Interop;
using magniF.UI;

namespace magniF;

public partial class App : Application
{
    private const string SingleInstanceName = @"Local\magniF.SingleInstance";
    private const string ShowSettingsSignal = @"Local\magniF.ShowSettings";

    private Mutex? _mutex;
    private EventWaitHandle? _showSignal;
    private Settings _settings = null!;
    private volatile Settings _snapshot = null!;
    private Lens? _lens;
    private TrayIcon? _tray;
    private ContextMenu? _menu;
    private SettingsWindow? _settingsWindow;
    private readonly DispatcherTimer _saveTimer = new() { Interval = TimeSpan.FromMilliseconds(500) };

    internal new static App Current => (App)Application.Current;

    internal Settings Settings => _settings;

    internal InputHook Input { get; private set; } = null!;

    protected override void OnStartup(StartupEventArgs e)
    {
        _mutex = new Mutex(initiallyOwned: true, SingleInstanceName, out var isFirstInstance);
        if (!isFirstInstance)
        {
            // Уже запущено — просим первый экземпляр показать настройки.
            if (EventWaitHandle.TryOpenExisting(ShowSettingsSignal, out var signal)) signal.Set();
            Shutdown();
            return;
        }

        // Курсор не должен остаться скрытым, что бы ни случилось.
        AppDomain.CurrentDomain.ProcessExit += (_, _) => Magnification.TryShowCursor();
        AppDomain.CurrentDomain.UnhandledException += (_, _) => Magnification.TryShowCursor();
        DispatcherUnhandledException += (_, args) =>
        {
            Magnification.TryShowCursor();
            MessageBox.Show(Strings.UnexpectedError(args.Exception), "magniF", MessageBoxButton.OK, MessageBoxImage.Error);
            args.Handled = true;
            ExitApp();
        };

        base.OnStartup(e);

        var firstRun = !Settings.FileExists;
        _settings = Settings.Load();
        _snapshot = _settings.Clone();
        Strings.Use(_settings.Language);
        _settings.PropertyChanged += OnSettingsChanged;
        _saveTimer.Tick += (_, _) => { _saveTimer.Stop(); _settings.Save(); };

        _lens = new Lens(() => _snapshot);
        if (_lens.InitError is { } error)
        {
            MessageBox.Show(error, "magniF", MessageBoxButton.OK, MessageBoxImage.Error);
            ExitApp();
            return;
        }

        Input = new InputHook(() => _snapshot);
        Input.Activated += _lens.Show;
        Input.Deactivated += _lens.Hide;
        Input.Wheel += _lens.AdjustZoom;
        _lens.HoldLost += Input.Resync;
        _lens.ZoomCommitted += zoom => Dispatcher.BeginInvoke(() => _settings.Zoom = zoom);

        _tray = new TrayIcon();
        _tray.LeftClick += (_, _) => ShowSettings();
        _tray.RightClick += (_, _) => ShowMenu();
        UpdateTray();

        _showSignal = new EventWaitHandle(false, EventResetMode.AutoReset, ShowSettingsSignal);
        new Thread(() =>
        {
            while (_showSignal.WaitOne()) Dispatcher.BeginInvoke(ShowSettings);
        }) { IsBackground = true, Name = "magniF.Signal" }.Start();

        if (firstRun)
        {
            _settings.Save();
            ShowSettings();
        }
    }

    private void OnSettingsChanged(object? sender, PropertyChangedEventArgs e)
    {
        _snapshot = _settings.Clone();

        if (e.PropertyName == nameof(Settings.Language))
        {
            Strings.Use(_settings.Language);
            ReopenSettings();
        }

        if (e.PropertyName is nameof(Settings.Enabled) or nameof(Settings.Binding) or nameof(Settings.Language))
        {
            UpdateTray();
            if (!_settings.Enabled) _lens?.Hide();
        }

        _saveTimer.Stop();
        _saveTimer.Start();
    }

    private void UpdateTray() =>
        _tray?.Update(_settings.Enabled,
            _settings.Enabled ? Strings.Tooltip(KeyBinding.Format(_settings.Binding)) : Strings.TooltipOff);

    internal void ShowSettings()
    {
        if (_settingsWindow is null)
        {
            _settingsWindow = new SettingsWindow(_settings);
            _settingsWindow.Closed += (_, _) => _settingsWindow = null;
            _settingsWindow.Show();
        }

        if (_settingsWindow.WindowState == WindowState.Minimized) _settingsWindow.WindowState = WindowState.Normal;
        _settingsWindow.Activate();
    }

    internal void ResetSettings()
    {
        var fresh = new Settings();
        foreach (var p in typeof(Settings).GetProperties().Where(p => p.CanWrite))
        {
            if (p.Name == nameof(Settings.Language)) continue;
            p.SetValue(_settings, p.GetValue(fresh));
        }
    }

    private void ReopenSettings()
    {
        if (_settingsWindow is not { } old) return;
        var (left, top) = (old.Left, old.Top);
        var scroll = old.ScrollOffset;
        old.Close();
        ShowSettings();
        _settingsWindow!.Left = left;
        _settingsWindow.Top = top;
        _settingsWindow.ScrollOffset = scroll;
    }

    private void ShowMenu()
    {
        _menu = new ContextMenu { Placement = PlacementMode.MousePoint };

        var settings = new MenuItem { Header = Strings.MenuSettings, FontWeight = FontWeights.SemiBold };
        settings.Click += (_, _) => ShowSettings();

        var enabled = new MenuItem { Header = Strings.MenuEnabled, IsChecked = _settings.Enabled };
        enabled.Click += (_, _) => _settings.Enabled = !_settings.Enabled;

        var autostart = new MenuItem { Header = Strings.MenuAutoStart, IsChecked = AutoStart.IsEnabled };
        autostart.Click += (_, _) => SetAutoStart(!AutoStart.IsEnabled);

        var exit = new MenuItem { Header = Strings.MenuExit };
        exit.Click += (_, _) => ExitApp();

        _menu.Items.Add(settings);
        _menu.Items.Add(new Separator());
        _menu.Items.Add(enabled);
        _menu.Items.Add(autostart);
        _menu.Items.Add(new Separator());
        _menu.Items.Add(exit);

        _menu.Opened += (_, _) =>
        {
            // Без активации меню не закрывается по клику мимо него.
            if (PresentationSource.FromVisual(_menu) is HwndSource source)
                NativeMethods.SetForegroundWindow(source.Handle);
        };
        _menu.IsOpen = true;
    }

    internal static bool SetAutoStart(bool enabled)
    {
        if (AutoStart.TrySet(enabled, out var error)) return true;
        MessageBox.Show(Strings.AutoStartFailed(error), "magniF", MessageBoxButton.OK, MessageBoxImage.Warning);
        return false;
    }

    internal void ExitApp()
    {
        if (_saveTimer.IsEnabled)
        {
            _saveTimer.Stop();
            _settings.Save();
        }

        _settingsWindow?.Close();
        _tray?.Dispose();
        (Input as IDisposable)?.Dispose();
        _lens?.Dispose();
        Magnification.TryShowCursor();
        Shutdown();
    }
}
