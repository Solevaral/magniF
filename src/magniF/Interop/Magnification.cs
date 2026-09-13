using System.Runtime.InteropServices;

namespace magniF.Interop;

/// <summary>
/// P/Invoke к magnification.dll. Сама лупа рисуется через Direct3D; отсюда нужен только
/// <see cref="MagShowSystemCursor"/> — единственный документированный способ спрятать курсор во всём сеансе.
/// </summary>
internal static class Magnification
{
    private const string Dll = "magnification.dll";

    [DllImport(Dll, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool MagInitialize();

    [DllImport(Dll, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool MagUninitialize();

    /// <summary>Прячет или возвращает системный курсор во всём сеансе.</summary>
    [DllImport(Dll, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool MagShowSystemCursor([MarshalAs(UnmanagedType.Bool)] bool fShowCursor);

    /// <summary>Возвращает курсор, не падая, если DLL недоступна.</summary>
    internal static void TryShowCursor()
    {
        try { MagShowSystemCursor(true); }
        catch (DllNotFoundException) { }
        catch (EntryPointNotFoundException) { }
    }
}
