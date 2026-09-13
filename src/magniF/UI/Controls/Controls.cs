using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Media;
using System.Windows.Shapes;
using magniF.Core;

namespace magniF.UI.Controls;

/// <summary>Строка «подпись — слайдер — значение».</summary>
public sealed class LabeledSlider : Grid
{
    public static readonly DependencyProperty ValueProperty = DependencyProperty.Register(
        nameof(Value), typeof(double), typeof(LabeledSlider),
        new FrameworkPropertyMetadata(0.0, FrameworkPropertyMetadataOptions.BindsTwoWayByDefault, (d, _) => ((LabeledSlider)d).UpdateText()));

    private readonly TextBlock _label = new() { Style = (Style)Application.Current.Resources["Label"] };
    private readonly Slider _slider = new() { IsSnapToTickEnabled = true };
    private readonly TextBlock _value = new() { Style = (Style)Application.Current.Resources["Value"] };

    public LabeledSlider()
    {
        Margin = new Thickness(0, 5, 0, 5);
        ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(150) });
        ColumnDefinitions.Add(new ColumnDefinition());
        ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(70) });

        SetColumn(_slider, 1);
        SetColumn(_value, 2);
        Children.Add(_label);
        Children.Add(_slider);
        Children.Add(_value);

        _slider.SetBinding(Slider.ValueProperty, new Binding(nameof(Value)) { Source = this, Mode = BindingMode.TwoWay });
        Loaded += (_, _) => UpdateText();
    }

    public double Value { get => (double)GetValue(ValueProperty); set => SetValue(ValueProperty, value); }

    public string Label { get => _label.Text; set => _label.Text = value; }

    public double Minimum { get => _slider.Minimum; set => _slider.Minimum = value; }

    public double Maximum { get => _slider.Maximum; set => _slider.Maximum = value; }

    public double Step
    {
        get => _slider.TickFrequency;
        set { _slider.TickFrequency = value; _slider.SmallChange = value; _slider.LargeChange = value * 5; }
    }

    /// <summary>Формат значения, например <c>{0:0} мс</c>.</summary>
    public string Format { get; set; } = "{0:0}";

    private void UpdateText() => _value.Text = string.Format(CultureInfo.CurrentCulture, Format, Value);
}

/// <summary>Выбор цвета: палитра готовых оттенков и поле #AARRGGBB.</summary>
public sealed class ColorField : StackPanel
{
    private static readonly string[] Palette =
        ["#FF7C9CFF", "#FF5EE0B5", "#FFFFC857", "#FFFF6B81", "#FFFFFFFF", "#FF2C3040", "#FF000000"];

    public static readonly DependencyProperty ColorProperty = DependencyProperty.Register(
        nameof(Color), typeof(string), typeof(ColorField),
        new FrameworkPropertyMetadata("#FFFFFFFF", FrameworkPropertyMetadataOptions.BindsTwoWayByDefault, (d, _) => ((ColorField)d).Sync()));

    private readonly TextBox _hex = new() { Width = 100, Margin = new Thickness(8, 0, 0, 0) };
    private readonly List<(string Color, Border Ring)> _swatches = [];

    public ColorField()
    {
        Orientation = Orientation.Horizontal;
        HorizontalAlignment = HorizontalAlignment.Right;

        foreach (var c in Palette)
        {
            var ring = new Border
            {
                Width = 24, Height = 24, CornerRadius = new CornerRadius(12), Margin = new Thickness(2, 0, 2, 0),
                BorderThickness = new Thickness(2), BorderBrush = Brushes.Transparent, Cursor = System.Windows.Input.Cursors.Hand,
                Background = Brushes.Transparent,
                Child = new Ellipse
                {
                    Margin = new Thickness(2), Fill = Brush(c),
                    Stroke = new SolidColorBrush(System.Windows.Media.Color.FromArgb(60, 255, 255, 255)), StrokeThickness = 1,
                },
            };
            var color = c;
            ring.MouseLeftButtonUp += (_, _) => Color = color;
            _swatches.Add((c, ring));
            Children.Add(ring);
        }

        _hex.LostKeyboardFocus += (_, _) => Commit();
        _hex.KeyDown += (_, e) => { if (e.Key == System.Windows.Input.Key.Enter) Commit(); };
        Children.Add(_hex);
        Sync();
    }

    public string Color { get => (string)GetValue(ColorProperty); set => SetValue(ColorProperty, value); }

    private void Commit() => Color = Settings.NormalizeColor(_hex.Text, Color);

    private void Sync()
    {
        _hex.Text = Color;
        var accent = (Brush)Application.Current.Resources["Accent"];
        foreach (var (c, ring) in _swatches)
            ring.BorderBrush = string.Equals(c, Color, StringComparison.OrdinalIgnoreCase) ? accent : Brushes.Transparent;
    }

    public static SolidColorBrush Brush(string argb)
    {
        var v = Settings.ParseArgb(argb);
        var brush = new SolidColorBrush(System.Windows.Media.Color.FromArgb((byte)(v >> 24), (byte)(v >> 16), (byte)(v >> 8), (byte)v));
        brush.Freeze();
        return brush;
    }
}

/// <summary>RadioButton.IsChecked ↔ значение перечисления из ConverterParameter.</summary>
public sealed class EnumIsConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture) =>
        value?.ToString() == parameter?.ToString();

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) =>
        value is true ? Enum.Parse(targetType, parameter.ToString()!) : Binding.DoNothing;
}
