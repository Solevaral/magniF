using System.ComponentModel;
using System.IO;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace magniF.Core;

public enum CenterIndicator
{
    None,
    Dot,
    Crosshair,
}

public sealed class Settings : INotifyPropertyChanged
{
    public const double MinZoom = 1.1;
    public const double MaxZoom = 10;

    private bool _enabled = true;
    private List<int> _binding = [KeyBinding.VK_CONTROL];
    private int _activationDelay = 200;
    private bool _wheelZoom = true;
    private double _zoomStep = 1.2;
    private double _zoom = 2.0;
    private bool _smoothScaling = true;
    private int _lensWidth = 360;
    private int _lensHeight = 220;
    private int _cornerRadius = 18;
    private CenterIndicator _indicator = CenterIndicator.Dot;
    private string _indicatorColor = "#FF7C9CFF";
    private string _borderColor = "#FF2C3040";
    private int _borderThickness = 3;
    private bool _shadowEnabled = true;
    private int _shadowSize = 18;
    private int _shadowOpacity = 45;
    private AppLanguage _language = AppLanguage.Auto;

    public event PropertyChangedEventHandler? PropertyChanged;

    /// <summary>Лупа отзывается на бинд.</summary>
    public bool Enabled { get => _enabled; set => Set(ref _enabled, value); }

    /// <summary>Коды клавиш и кнопок мыши, которые нужно держать одновременно.</summary>
    public List<int> Binding { get => _binding; set => Set(ref _binding, value); }

    /// <summary>Сколько держать бинд без других нажатий, прежде чем появится лупа, мс.</summary>
    public int ActivationDelay { get => _activationDelay; set => Set(ref _activationDelay, Math.Clamp(value, 0, 1000)); }

    public bool WheelZoom { get => _wheelZoom; set => Set(ref _wheelZoom, value); }

    /// <summary>Множитель кратности на одно деление колеса.</summary>
    public double ZoomStep { get => _zoomStep; set => Set(ref _zoomStep, Math.Round(Math.Clamp(value, 1.05, 2.0), 2)); }

    public double Zoom { get => _zoom; set => Set(ref _zoom, Math.Round(Math.Clamp(value, MinZoom, MaxZoom), 2)); }

    /// <summary>Бикубическое сглаживание увеличенного изображения; выключено — чёткие пиксели.</summary>
    public bool SmoothScaling { get => _smoothScaling; set => Set(ref _smoothScaling, value); }

    /// <summary>Размер лупы в DIP: на мониторе масштабируется под его DPI.</summary>
    public int LensWidth { get => _lensWidth; set => Set(ref _lensWidth, Math.Clamp(value, 80, 1600)); }

    public int LensHeight { get => _lensHeight; set => Set(ref _lensHeight, Math.Clamp(value, 60, 1200)); }

    public int CornerRadius { get => _cornerRadius; set => Set(ref _cornerRadius, Math.Clamp(value, 0, 300)); }

    public CenterIndicator Indicator { get => _indicator; set => Set(ref _indicator, value); }

    public string IndicatorColor { get => _indicatorColor; set => Set(ref _indicatorColor, NormalizeColor(value, _indicatorColor)); }

    public string BorderColor { get => _borderColor; set => Set(ref _borderColor, NormalizeColor(value, _borderColor)); }

    public int BorderThickness { get => _borderThickness; set => Set(ref _borderThickness, Math.Clamp(value, 0, 24)); }

    public bool ShadowEnabled { get => _shadowEnabled; set => Set(ref _shadowEnabled, value); }

    public int ShadowSize { get => _shadowSize; set => Set(ref _shadowSize, Math.Clamp(value, 2, 60)); }

    /// <summary>Непрозрачность тени у края лупы, %.</summary>
    public int ShadowOpacity { get => _shadowOpacity; set => Set(ref _shadowOpacity, Math.Clamp(value, 0, 100)); }

    public AppLanguage Language { get => _language; set => Set(ref _language, value); }

    private void Set<T>(ref T field, T value, [CallerMemberName] string? name = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value)) return;
        field = value;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    }

    /// <summary>Приводит строку к виду #AARRGGBB; мусор заменяется прежним значением.</summary>
    public static string NormalizeColor(string? value, string fallback)
    {
        var s = value?.Trim().TrimStart('#') ?? "";
        if (s.Length == 6) s = "FF" + s;
        return s.Length == 8 && uint.TryParse(s, System.Globalization.NumberStyles.HexNumber, null, out _)
            ? "#" + s.ToUpperInvariant()
            : fallback;
    }

    public static uint ParseArgb(string color) =>
        uint.Parse(color.AsSpan(1), System.Globalization.NumberStyles.HexNumber);

    /// <summary>Независимая копия для потоков лупы и хуков.</summary>
    public Settings Clone() =>
        JsonSerializer.Deserialize(JsonSerializer.Serialize(this, SettingsJsonContext.Default.Settings),
            SettingsJsonContext.Default.Settings)!;

    // ---- загрузка и сохранение ----

    public static string FilePath { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "magniF", "settings.json");

    public static bool FileExists => File.Exists(FilePath);

    public static Settings Load()
    {
        try
        {
            if (File.Exists(FilePath))
            {
                var json = File.ReadAllText(FilePath);
                var loaded = JsonSerializer.Deserialize(json, SettingsJsonContext.Default.Settings);
                if (loaded is not null)
                {
                    loaded.Normalize();
                    return loaded;
                }
            }
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
        {
            // Битый или недоступный файл — стартуем со значений по умолчанию.
        }

        return new Settings();
    }

    public void Save()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
            File.WriteAllText(FilePath, JsonSerializer.Serialize(this, SettingsJsonContext.Default.Settings));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Настройки не критичны: продолжаем работать с текущими значениями в памяти.
        }
    }

    /// <summary>
    /// Числа и цвета из файла уже прошли через сеттеры; бинд, правленный руками, приводим отдельно.
    /// </summary>
    private void Normalize()
    {
        Binding = KeyBinding.Normalize(Binding);
        if (Binding.Count == 0) Binding = [KeyBinding.VK_CONTROL];
    }
}

[JsonSourceGenerationOptions(WriteIndented = true, UseStringEnumConverter = true, IgnoreReadOnlyProperties = true)]
[JsonSerializable(typeof(Settings))]
internal partial class SettingsJsonContext : JsonSerializerContext;
