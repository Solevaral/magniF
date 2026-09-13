using System.Globalization;

namespace magniF.Core;

public enum AppLanguage
{
    /// <summary>Язык интерфейса Windows. / Windows UI language.</summary>
    Auto,
    English,
    Russian,
}

/// <summary>Строки интерфейса парами «английский / русский».</summary>
public static class Strings
{
    private static bool _ru;

    public static void Use(AppLanguage language)
    {
        _ru = language switch
        {
            AppLanguage.Russian => true,
            AppLanguage.English => false,
            _ => CultureInfo.CurrentUICulture.TwoLetterISOLanguageName
                     .Equals("ru", StringComparison.OrdinalIgnoreCase),
        };
    }

    private static string S(string en, string ru) => _ru ? ru : en;

    // ---- трей ----

    public static string MenuSettings => S("Settings…", "Настройки…");
    public static string MenuEnabled => S("Magnifier enabled", "Лупа включена");
    public static string MenuAutoStart => S("Start with Windows", "Запускать с Windows");
    public static string MenuExit => S("Exit", "Выход");
    public static string Tooltip(string bind) => S($"magniF — hold {bind}", $"magniF — удерживайте {bind}");
    public static string TooltipOff => S("magniF — off", "magniF — выключена");

    // ---- окно настроек ----

    public static string Subtitle => S("Hold a key — zoom follows the cursor", "Держите клавишу — лупа под курсором");
    public static string On => S("On", "Вкл");

    public static string SectionActivation => S("Activation", "Активация");
    public static string HoldKey => S("Hold to zoom", "Удерживать для зума");
    public static string HoldKeyHint => S("Click, then press a key, a combination or a side mouse button. Esc cancels.",
                                          "Нажмите, затем клавишу, сочетание или боковую кнопку мыши. Esc — отмена.");
    public static string Recording => S("Press keys…", "Нажмите клавиши…");
    public static string Delay => S("Activation delay", "Задержка появления");
    public static string DelayHint => S("The key passes through to other apps; the lens appears only if nothing else is pressed meanwhile.",
                                        "Клавиша работает как обычно; лупа появится, только если за это время ничего больше не нажато.");
    public static string WheelZoom => S("Mouse wheel changes zoom", "Колесо мыши меняет кратность");
    public static string WheelStep => S("Wheel step", "Шаг колеса");

    public static string SectionLens => S("Lens", "Лупа");
    public static string Zoom => S("Zoom", "Кратность");
    public static string SmoothScaling => S("Smooth scaling", "Сглаживание при увеличении");
    public static string Width => S("Width", "Ширина");
    public static string Height => S("Height", "Высота");
    public static string Radius => S("Corner radius", "Скругление");
    public static string Indicator => S("Center mark", "Метка центра");
    public static string IndicatorNone => S("None", "Нет");
    public static string IndicatorDot => S("Dot", "Точка");
    public static string IndicatorCross => S("Cross", "Крест");
    public static string IndicatorColor => S("Mark color", "Цвет метки");

    public static string SectionStyle => S("Style", "Стиль");
    public static string BorderColor => S("Border color", "Цвет рамки");
    public static string BorderThickness => S("Border", "Рамка");
    public static string Shadow => S("Shadow", "Тень");
    public static string ShadowSize => S("Shadow size", "Размер тени");
    public static string ShadowOpacity => S("Shadow opacity", "Плотность тени");


    public static string SectionGeneral => S("General", "Общее");
    public static string Language => S("Language", "Язык");
    public static string LanguageAuto => S("System", "Системный");
    public static string AutoStart => S("Start with Windows", "Запускать с Windows");

    public static string Preview => S("Preview", "Превью");
    public static string PreviewHint(string bind) =>
        S($"Hold {bind} anywhere to try it for real.", $"Удерживайте {bind} где угодно, чтобы попробовать вживую.");
    public static string ResetDefaults => S("Reset to defaults", "Сбросить настройки");
    public static string Ms => S("ms", "мс");
    public static string Px => S("px", "пкс");

    public static string MouseButton(int n) => S($"Mouse {n}", $"Мышь {n}");

    // ---- сообщения ----

    public static string AutoStartFailed(string? error) =>
        S($"Could not change autostart: {error}", $"Не удалось изменить автозапуск: {error}");

    public static string MagInitFailed(int code) =>
        S($"MagInitialize failed (error {code}). The screen magnifier is disabled by policy or unavailable.",
          $"MagInitialize не удался (код {code}). Экранный увеличитель отключён политикой или недоступен.");

    public static string UnexpectedError(object details) =>
        S($"Unexpected error:\n\n{details}", $"Непредвиденная ошибка:\n\n{details}");
}
