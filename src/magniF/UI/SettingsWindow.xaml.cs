using System.ComponentModel;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Effects;
using System.Windows.Shapes;
using magniF.Core;
using magniF.UI.Controls;
using static magniF.Interop.NativeMethods;

namespace magniF.UI;

public partial class SettingsWindow : Window
{
    private readonly Settings _settings;
    private bool _recording;

    internal SettingsWindow(Settings settings)
    {
        _settings = settings;
        InitializeComponent();
        DataContext = settings;

        // Суффиксы единиц зависят от языка, поэтому задаются здесь, а не в XAML.
        foreach (var slider in new[] { WidthSlider, HeightSlider, RadiusSlider, BorderSlider, ShadowSizeSlider })
            slider.Format = "{0:0} " + Strings.Px;
        foreach (var slider in new[] { DelaySlider })
            slider.Format = "{0:0} " + Strings.Ms;

        AutoStartToggle.IsChecked = AutoStart.IsEnabled;
        // В режиме модуля автозапуском управляет каркас All-in-one.
        if (Hosting.IsHosted) AutoStartToggle.Visibility = Visibility.Collapsed;
        _settings.PropertyChanged += OnSettingsChanged;
        SourceInitialized += (_, _) => ApplyDarkTitleBar();
        Deactivated += (_, _) => StopRecording();
        Closed += (_, _) =>
        {
            StopRecording();
            _settings.PropertyChanged -= OnSettingsChanged;
        };
        Loaded += (_, _) => UpdateAll();
    }

    internal double ScrollOffset
    {
        get => Scroll.VerticalOffset;
        set => Dispatcher.BeginInvoke(() => Scroll.ScrollToVerticalOffset(value), System.Windows.Threading.DispatcherPriority.Loaded);
    }

    private void OnSettingsChanged(object? sender, PropertyChangedEventArgs e) => UpdateAll();

    private void UpdateAll()
    {
        var bind = KeyBinding.Format(_settings.Binding);
        if (!_recording) BindButton.Content = bind;
        PreviewHint.Text = Strings.PreviewHint(bind);
        UpdatePreview();
    }

    // ---- бинд ----

    private void OnBindClick(object sender, RoutedEventArgs e)
    {
        if (_recording)
        {
            StopRecording();
            return;
        }

        _recording = true;
        BindButton.Content = Strings.Recording;
        BindButton.BorderBrush = (Brush)FindResource("Accent");
        App.Current.Input.BeginRecording(codes => Dispatcher.BeginInvoke(() =>
        {
            if (!_recording) return;
            _recording = false;
            BindButton.ClearValue(BorderBrushProperty);
            if (codes is { Count: > 0 }) _settings.Binding = codes;
            UpdateAll();
        }));
    }

    private void StopRecording()
    {
        if (!_recording) return;
        _recording = false;
        App.Current.Input.CancelRecording();
        BindButton.ClearValue(BorderBrushProperty);
        UpdateAll();
    }

    // ---- общее ----

    private void OnAutoStartClick(object sender, RoutedEventArgs e)
    {
        App.SetAutoStart(AutoStartToggle.IsChecked == true);
        AutoStartToggle.IsChecked = AutoStart.IsEnabled;
    }

    private void OnResetClick(object sender, RoutedEventArgs e) => App.Current.ResetSettings();

    // ---- превью ----

    private void UpdatePreview()
    {
        var s = _settings;
        double w = s.LensWidth, h = s.LensHeight;
        var radius = Math.Min(s.CornerRadius, Math.Min(w, h) / 2);
        var shadow = s.ShadowEnabled ? s.ShadowSize : 0;

        // «Стол» всегда чуть больше лупы; Viewbox ужмёт его под размер карточки.
        Desk.Width = Math.Max(480, w + 2 * shadow + 120);
        Desk.Height = Math.Max(520, h + 2 * shadow + 160);
        SampleWindow.Width = Math.Max(340, w * 0.9);

        foreach (var lens in new[] { LensBody, LensShadow })
        {
            lens.Width = w;
            lens.Height = h;
            lens.CornerRadius = new CornerRadius(radius);
        }

        LensBody.BorderThickness = new Thickness(s.BorderThickness);
        LensBody.BorderBrush = ColorField.Brush(s.BorderColor);

        LensShadow.Visibility = shadow > 0 && s.ShadowOpacity > 0 ? Visibility.Visible : Visibility.Hidden;
        LensShadow.Effect = new DropShadowEffect
        {
            Color = Colors.Black,
            BlurRadius = shadow * 1.6,
            ShadowDepth = shadow / 4.0,
            Direction = 270,
            Opacity = s.ShadowOpacity / 100.0,
        };

        // Лупа показывает центр «стола» с заданной кратностью.
        var cx = Desk.Width / 2;
        var cy = Desk.Height / 2;
        var vw = w / s.Zoom;
        var vh = h / s.Zoom;
        LensBrush.Viewbox = new Rect(cx - vw / 2, cy - vh / 2, vw, vh);
        RenderOptions.SetBitmapScalingMode(LensBody, s.SmoothScaling ? BitmapScalingMode.HighQuality : BitmapScalingMode.NearestNeighbor);

        DrawMark(s);
    }

    private void DrawMark(Settings s)
    {
        Mark.Children.Clear();
        if (s.Indicator == CenterIndicator.None) return;

        var color = ColorField.Brush(s.IndicatorColor);
        var outline = new SolidColorBrush(Color.FromArgb(170, 10, 10, 14));

        if (s.Indicator == CenterIndicator.Dot)
        {
            Mark.Children.Add(Dot(4.2, outline));
            Mark.Children.Add(Dot(3, color));
            return;
        }

        const double gap = 3.5, arm = 8;
        foreach (var (brush, thickness) in new[] { (outline, 3.5), (color, 1.6) })
        {
            foreach (var (x1, y1, x2, y2) in new[]
                     { (-gap - arm, 0.0, -gap, 0.0), (gap, 0, gap + arm, 0), (0, -gap - arm, 0, -gap), (0, gap, 0, gap + arm) })
            {
                Mark.Children.Add(new Line
                {
                    X1 = x1, Y1 = y1, X2 = x2, Y2 = y2, Stroke = brush, StrokeThickness = thickness,
                    StrokeStartLineCap = PenLineCap.Round, StrokeEndLineCap = PenLineCap.Round,
                });
            }
        }
    }

    private static Ellipse Dot(double r, Brush fill)
    {
        var dot = new Ellipse { Width = 2 * r, Height = 2 * r, Fill = fill };
        System.Windows.Controls.Canvas.SetLeft(dot, -r);
        System.Windows.Controls.Canvas.SetTop(dot, -r);
        return dot;
    }

    private void ApplyDarkTitleBar()
    {
        var hwnd = new WindowInteropHelper(this).Handle;
        var dark = 1;
        DwmSetWindowAttribute(hwnd, DWMWA_USE_IMMERSIVE_DARK_MODE, ref dark, sizeof(int));
        var caption = 0x00171312; // COLORREF (BGR) цвета фона #121317
        DwmSetWindowAttribute(hwnd, DWMWA_CAPTION_COLOR, ref caption, sizeof(int));
    }
}
