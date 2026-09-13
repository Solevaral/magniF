using static magniF.Interop.NativeMethods;

namespace magniF.Core;

/// <summary>
/// Бинд — набор виртуальных кодов. Модификаторы хранятся обобщёнными (Ctrl, а не левый/правый
/// Ctrl), кнопки мыши — стандартными VK_MBUTTON / VK_XBUTTON1 / VK_XBUTTON2.
/// </summary>
public static class KeyBinding
{
    public const int VK_MBUTTON = 0x04;
    public const int VK_XBUTTON1 = 0x05;
    public const int VK_XBUTTON2 = 0x06;
    public const int VK_SHIFT = 0x10;
    public const int VK_CONTROL = 0x11;
    public const int VK_MENU = 0x12;
    public const int VK_ESCAPE = 0x1B;
    public const int VK_LWIN = 0x5B;
    public const int VK_RWIN = 0x5C;
    public const int VK_LSHIFT = 0xA0;
    public const int VK_RSHIFT = 0xA1;
    public const int VK_LCONTROL = 0xA2;
    public const int VK_RCONTROL = 0xA3;
    public const int VK_LMENU = 0xA4;
    public const int VK_RMENU = 0xA5;

    /// <summary>Неназначенный код: им «разбавляем» удержание Alt/Win, чтобы отпускание не открыло меню.</summary>
    public const int VK_MASK = 0xE8;

    /// <summary>Приводит код от хука к виду, в котором он хранится в бинде.</summary>
    public static int Normalize(int vk) => vk switch
    {
        VK_LCONTROL or VK_RCONTROL => VK_CONTROL,
        VK_LSHIFT or VK_RSHIFT => VK_SHIFT,
        VK_LMENU or VK_RMENU => VK_MENU,
        VK_RWIN => VK_LWIN,
        _ => vk,
    };

    public static List<int> Normalize(IEnumerable<int>? codes) =>
        codes is null ? [] : [.. codes.Select(Normalize).Where(c => c is > 0 and < 0xFF).Distinct().OrderBy(Order)];

    public static bool IsModifier(int vk) => vk is VK_CONTROL or VK_SHIFT or VK_MENU or VK_LWIN;

    public static bool IsMouse(int vk) => vk is VK_MBUTTON or VK_XBUTTON1 or VK_XBUTTON2;

    /// <summary>Модификаторы первыми, в привычном порядке Ctrl+Shift+Alt+Win.</summary>
    private static int Order(int vk) => vk switch
    {
        VK_CONTROL => 0,
        VK_SHIFT => 1,
        VK_MENU => 2,
        VK_LWIN => 3,
        _ => 10 + vk,
    };

    public static string Format(IEnumerable<int> codes) => string.Join(" + ", codes.Select(Name));

    public static string Name(int vk)
    {
        switch (vk)
        {
            case VK_CONTROL: return "Ctrl";
            case VK_SHIFT: return "Shift";
            case VK_MENU: return "Alt";
            case VK_LWIN: return "Win";
            case VK_MBUTTON: return Strings.MouseButton(3);
            case VK_XBUTTON1: return Strings.MouseButton(4);
            case VK_XBUTTON2: return Strings.MouseButton(5);
        }

        var scan = MapVirtualKey((uint)vk, MAPVK_VK_TO_VSC);
        if (scan != 0)
        {
            var lParam = (int)(scan << 16);
            if (IsExtended(vk)) lParam |= 1 << 24;
            var buffer = new char[64];
            var length = GetKeyNameText(lParam, buffer, buffer.Length);
            if (length > 0) return new string(buffer, 0, length);
        }

        return $"0x{vk:X2}";
    }

    /// <summary>Клавиши, у которых скан-код совпадает с цифровым блоком, но это другая клавиша.</summary>
    private static bool IsExtended(int vk) => vk is
        >= 0x21 and <= 0x28 // PgUp, PgDn, End, Home, стрелки
        or 0x2D or 0x2E     // Insert, Delete
        or 0x6F             // Numpad /
        or 0x90             // NumLock
        or 0x2C;            // PrintScreen
}
