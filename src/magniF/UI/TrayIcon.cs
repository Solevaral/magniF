using System.Drawing;
using System.Drawing.Drawing2D;
using System.Windows.Forms;
using static magniF.Interop.NativeMethods;

namespace magniF.UI;

/// <summary>Иконка в области уведомлений поверх Shell_NotifyIcon с восстановлением после перезапуска Explorer.</summary>
internal sealed class TrayIcon : IDisposable
{
    private const int WM_TRAYCALLBACK = 0x8000 + 1;
    private const uint IconId = 1;

    private const int WM_LBUTTONUP = 0x0202;
    private const int WM_RBUTTONUP = 0x0205;

    private readonly MessageWindow _window;
    private readonly uint _taskbarCreated;
    private IntPtr _hIcon;
    private string _tip = "magniF";
    private bool _added;
    private bool _disposed;

    public TrayIcon()
    {
        _taskbarCreated = RegisterWindowMessage("TaskbarCreated");
        _window = new MessageWindow(OnMessage);
        _hIcon = TrayIconArt.CreateHIcon(enabled: true);
        Add();
    }

    public event EventHandler? LeftClick;
    public event EventHandler? RightClick;

    public IntPtr Handle => _window.Handle;

    public void Update(bool enabled, string tip)
    {
        if (_disposed) return;

        var old = _hIcon;
        _hIcon = TrayIconArt.CreateHIcon(enabled);
        _tip = tip.Length > 127 ? tip[..127] : tip;

        var data = BuildData(NIF_ICON | NIF_TIP);
        Shell_NotifyIcon(NIM_MODIFY, ref data);

        if (old != IntPtr.Zero) DestroyIcon(old);
    }

    private void Add()
    {
        var data = BuildData(NIF_MESSAGE | NIF_ICON | NIF_TIP);
        _added = Shell_NotifyIcon(NIM_ADD, ref data);
    }

    private NOTIFYICONDATA BuildData(uint flags) => new()
    {
        cbSize = (uint)System.Runtime.InteropServices.Marshal.SizeOf<NOTIFYICONDATA>(),
        hWnd = _window.Handle,
        uID = IconId,
        uFlags = flags,
        uCallbackMessage = WM_TRAYCALLBACK,
        hIcon = _hIcon,
        szTip = _tip,
        szInfo = string.Empty,
        szInfoTitle = string.Empty,
    };

    private void OnMessage(ref Message m)
    {
        if (m.Msg == WM_TRAYCALLBACK)
        {
            switch ((int)m.LParam)
            {
                case WM_LBUTTONUP: LeftClick?.Invoke(this, EventArgs.Empty); break;
                case WM_RBUTTONUP: RightClick?.Invoke(this, EventArgs.Empty); break;
            }
        }
        else if (_taskbarCreated != 0 && m.Msg == (int)_taskbarCreated)
        {
            // Explorer перезапустился — иконку нужно добавить заново.
            Add();
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        if (_added)
        {
            var data = BuildData(0);
            Shell_NotifyIcon(NIM_DELETE, ref data);
        }

        if (_hIcon != IntPtr.Zero)
        {
            DestroyIcon(_hIcon);
            _hIcon = IntPtr.Zero;
        }

        _window.DestroyHandle();
    }

    /// <summary>Скрытое окно-приёмник сообщений оболочки.</summary>
    private sealed class MessageWindow : NativeWindow
    {
        public delegate void MessageHandler(ref Message m);

        private readonly MessageHandler _handler;

        public MessageWindow(MessageHandler handler)
        {
            _handler = handler;
            CreateHandle(new CreateParams
            {
                Caption = "magniF.TrayWindow",
                ExStyle = WS_EX_TOOLWINDOW,
            });
        }

        protected override void WndProc(ref Message m)
        {
            _handler(ref m);
            base.WndProc(ref m);
        }
    }
}

/// <summary>Рисует иконку трея: скруглённая линза с ручкой; выключенная — серая.</summary>
internal static class TrayIconArt
{
    public static IntPtr CreateHIcon(bool enabled)
    {
        var size = Math.Max(16, GetSystemMetrics(SM_CXSMICON));
        using var bmp = new Bitmap(size, size);
        using (var g = Graphics.FromImage(bmp))
        {
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.Clear(Color.Transparent);

            var k = size / 16f;
            var accent = enabled ? Color.FromArgb(255, 124, 156, 255) : Color.FromArgb(255, 150, 152, 160);

            using var handle = new Pen(accent, 2.6f * k) { StartCap = LineCap.Round, EndCap = LineCap.Round };
            g.DrawLine(handle, 10.6f * k, 10.6f * k, 14.2f * k, 14.2f * k);

            var lens = new RectangleF(1.5f * k, 1.5f * k, 10.5f * k, 10.5f * k);
            using var path = RoundRect(lens, 3f * k);
            using var fill = new SolidBrush(Color.FromArgb(235, 28, 30, 38));
            g.FillPath(fill, path);
            using var pen = new Pen(accent, 1.9f * k);
            g.DrawPath(pen, path);
        }

        return bmp.GetHicon();
    }

    private static GraphicsPath RoundRect(RectangleF r, float radius)
    {
        var path = new GraphicsPath();
        var d = radius * 2;
        path.AddArc(r.X, r.Y, d, d, 180, 90);
        path.AddArc(r.Right - d, r.Y, d, d, 270, 90);
        path.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
        path.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
        path.CloseFigure();
        return path;
    }
}
