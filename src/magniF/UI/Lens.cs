using System.IO;
using System.Numerics;
using System.Runtime.InteropServices;
using magniF.Core;
using magniF.Interop;
using Vortice.Direct3D;
using Vortice.Direct3D11;
using Vortice.DirectComposition;
using Vortice.DXGI;
using static magniF.Interop.NativeMethods;

namespace magniF.UI;

/// <summary>
/// Лупа под курсором. Работает на собственном потоке и рисует каждый кадр на GPU:
/// <list type="bullet">
/// <item>изображение рабочего стола — Desktop Duplication API;</item>
/// <item>вывод — прозрачное для мыши окно на весь монитор с цепочкой обмена DirectComposition.</item>
/// </list>
/// Окно не двигается вслед за мышью — двигается картинка внутри, поэтому положение лупы и
/// увеличиваемая область всегда из одного кадра. Окно исключено из захвата
/// (<c>WDA_EXCLUDEFROMCAPTURE</c>), так что лупа не видит сама себя.
/// </summary>
internal sealed class Lens : IDisposable
{
    private const uint WM_SHOW = WM_APP + 10;
    private const uint WM_HIDE = WM_APP + 11;
    private const uint WM_WHEEL = WM_APP + 12;
    private const uint WM_TIMER = 0x0113;

    private const string WindowClass = "magniF.Lens";

    /// <summary>Сколько держать захват экрана после скрытия, чтобы повторный показ был мгновенным.</summary>
    private const uint IdleReleaseMs = 15000;

    private readonly Func<Settings> _settings;
    private readonly Thread _thread;
    private readonly ManualResetEventSlim _ready = new();
    private readonly WndProc _wndProc = (h, m, w, l) => DefWindowProc(h, m, w, l);

    private uint _threadId;
    private IntPtr _window;
    private bool _cursorApi;
    private GpuLens? _gpu;
    private nuint _idleTimer;

    // ---- состояние кадра (только поток лупы) ----
    private bool _wanted;
    private bool _visible;
    private double _zoom;
    private double _settingsZoom;
    private int _missedHold;
    private int _frame;

    public Lens(Func<Settings> settings)
    {
        _settings = settings;
        _settingsZoom = _zoom = settings().Zoom;
        _thread = new Thread(Run) { IsBackground = true, Name = "magniF.Lens" };
        _thread.Start();
        _ready.Wait();
    }

    /// <summary>Текст ошибки, если лупу не удалось запустить.</summary>
    public string? InitError { get; private set; }

    /// <summary>Кратность изменена колесом; вызывается на потоке лупы после скрытия.</summary>
    public event Action<double>? ZoomCommitted;

    /// <summary>Лупа сама заметила, что бинд отпущен, а хук это пропустил.</summary>
    public event Action? HoldLost;

    public void Show() => PostThreadMessage(_threadId, WM_SHOW, 0, 0);

    public void Hide() => PostThreadMessage(_threadId, WM_HIDE, 0, 0);

    public void AdjustZoom(double steps) =>
        PostThreadMessage(_threadId, WM_WHEEL, (IntPtr)(int)Math.Round(steps * 1000), 0);

    // ---------------------------------------------------------------- поток

    private void Run()
    {
        _threadId = GetCurrentThreadId();
        PeekMessage(out _, IntPtr.Zero, 0, 0, PM_NOREMOVE);

        try
        {
            // Magnification API здесь только ради скрытия системного курсора.
            _cursorApi = Magnification.MagInitialize();

            CreateOverlayWindow();
            _gpu = new GpuLens(_window);
        }
        catch (Exception ex)
        {
            InitError = ex.Message;
            _ready.Set();
            return;
        }
        _ready.Set();

        var running = true;
        while (running)
        {
            if (!_visible && !_wanted)
            {
                if (GetMessage(out var msg, IntPtr.Zero, 0, 0) <= 0) break;
                Handle(ref msg);
            }

            while (PeekMessage(out var msg, IntPtr.Zero, 0, 0, PM_REMOVE))
            {
                if (msg.message == WM_QUIT) { running = false; break; }
                Handle(ref msg);
            }

            if (running && (_visible || _wanted)) SafeFrame();
        }

        ShowCursor(true);
        _gpu?.Dispose();
        DestroyWindow(_window);
        if (_cursorApi) Magnification.MagUninitialize();
    }

    /// <summary>
    /// Сбой видеодрайвера или захвата не должен ронять программу со спрятанным курсором:
    /// лупа закрывается, устройство пересоздаётся при следующем показе.
    /// </summary>
    private void SafeFrame()
    {
        try
        {
            Frame();
        }
        catch (Exception)
        {
            BeginHide();
            HoldLost?.Invoke();
            ShowWindow(_window, SW_HIDE);
            _visible = false;
            try
            {
                _gpu?.Dispose();
                _gpu = new GpuLens(_window);
            }
            catch
            {
                _gpu = null;
            }
        }
    }

    private void Handle(ref MSG msg)
    {
        switch (msg.message)
        {
            case WM_SHOW:
                if (_wanted) break;
                _wanted = true;
                _missedHold = 0;
                SyncZoomFromSettings();
                ShowCursor(false);
                if (_idleTimer != 0) { KillTimer(IntPtr.Zero, _idleTimer); _idleTimer = 0; }
                break;

            case WM_HIDE:
                BeginHide();
                break;

            case WM_WHEEL:
                if (_wanted)
                {
                    var steps = (int)msg.wParam / 1000.0;
                    _zoom = Math.Clamp(_zoom * Math.Pow(_settings().ZoomStep, steps), Settings.MinZoom, Settings.MaxZoom);
                }
                break;

            case WM_TIMER when msg.hwnd == IntPtr.Zero:
                KillTimer(IntPtr.Zero, _idleTimer);
                _idleTimer = 0;
                if (!_wanted && !_visible) _gpu?.ReleaseCapture();
                break;

            default:
                TranslateMessage(ref msg);
                DispatchMessage(ref msg);
                break;
        }
    }

    private void ShowCursor(bool show)
    {
        if (_cursorApi) Magnification.MagShowSystemCursor(show);
    }

    private void BeginHide()
    {
        if (!_wanted) return;
        _wanted = false;
        // Курсор возвращаем сразу: пользователь уже отпустил клавишу и может целиться.
        ShowCursor(true);

        if (Math.Abs(_zoom - _settingsZoom) > 0.001)
        {
            _settingsZoom = _zoom;
            ZoomCommitted?.Invoke(Math.Round(_zoom, 2));
        }
    }

    private void SyncZoomFromSettings()
    {
        var z = _settings().Zoom;
        if (Math.Abs(z - _settingsZoom) > 0.001) _zoom = _settingsZoom = z;
    }

    // ---------------------------------------------------------------- кадр

    private void Frame()
    {
        var gpu = _gpu ??= new GpuLens(_window);
        var s = _settings();

        if (!_wanted)
        {
            HideOverlay();
            return;
        }

        SyncZoomFromSettings();
        if (_frame % 3 == 0) CheckHold(s);

        GetCursorPos(out var cursor);
        var monitor = MonitorFromPoint(cursor, MONITOR_DEFAULTTONEAREST);
        var info = new MONITORINFO { cbSize = (uint)Marshal.SizeOf<MONITORINFO>() };
        GetMonitorInfo(monitor, ref info);
        var scale = GetDpiForMonitor(monitor, 0, out var dpi, out _) == 0 ? dpi / 96.0 : 1.0;

        if (!gpu.Prepare(monitor, info.rcMonitor))
        {
            // Захват недоступен (защищённый рабочий стол, смена режима) — попробуем в следующем кадре.
            Thread.Sleep(16);
            return;
        }

        // Курсор читаем ещё раз прямо перед отрисовкой: захват кадра мог занять время.
        GetCursorPos(out var latest);
        if (MonitorFromPoint(latest, MONITOR_DEFAULTTONEAREST) == monitor) cursor = latest;

        var p = LensParams.Compute(s, cursor, info.rcMonitor, gpu.TextureSize, scale, _zoom);
        gpu.Render(p);

        if (!_visible)
        {
            ShowWindow(_window, SW_SHOWNOACTIVATE);
            _visible = true;
        }
        if (_frame % 120 == 0)
            SetWindowPos(_window, HWND_TOPMOST, 0, 0, 0, 0, SWP_NOMOVE | SWP_NOSIZE | SWP_NOACTIVATE);
        _frame++;
    }

    /// <summary>Страховка от пропущенного хуком отпускания.</summary>
    private void CheckHold(Settings s)
    {
        var held = s.Binding.Count > 0 && s.Binding.All(vk => (GetAsyncKeyState(vk) & 0x8000) != 0);
        _missedHold = held ? 0 : _missedHold + 1;
        if (_missedHold >= 3)
        {
            BeginHide();
            HoldLost?.Invoke();
        }
    }

    private void HideOverlay()
    {
        if (!_visible) return;
        // Пустой кадр перед скрытием: при следующем показе не мелькнёт старая лупа.
        _gpu!.Clear();
        ShowWindow(_window, SW_HIDE);
        _visible = false;
        _idleTimer = SetTimer(IntPtr.Zero, 0, IdleReleaseMs, IntPtr.Zero);
    }

    private void CreateOverlayWindow()
    {
        var instance = GetModuleHandle(null);
        var wc = new WNDCLASSEX
        {
            cbSize = (uint)Marshal.SizeOf<WNDCLASSEX>(),
            lpfnWndProc = Marshal.GetFunctionPointerForDelegate(_wndProc),
            hInstance = instance,
            lpszClassName = WindowClass,
        };
        RegisterClassEx(ref wc);

        const int exStyle = WS_EX_LAYERED | WS_EX_TRANSPARENT | WS_EX_TOPMOST | WS_EX_TOOLWINDOW
                            | WS_EX_NOACTIVATE | WS_EX_NOREDIRECTIONBITMAP;

        _window = CreateWindowEx(exStyle, WindowClass, "magniF lens", WS_POPUP, 0, 0, 1, 1,
            IntPtr.Zero, IntPtr.Zero, instance, IntPtr.Zero);
        if (_window == IntPtr.Zero) throw new InvalidOperationException($"CreateWindowEx failed ({Marshal.GetLastWin32Error()})");

        SetLayeredWindowAttributes(_window, 0, 255, LWA_ALPHA);
        SetWindowDisplayAffinity(_window, WDA_EXCLUDEFROMCAPTURE);
    }

    public void Dispose()
    {
        if (_threadId == 0) return;
        PostThreadMessage(_threadId, WM_QUIT, 0, 0);
        _thread.Join(1000);
    }
}

/// <summary>Константный буфер шейдера; раскладка совпадает с <c>cbuffer Params</c>.</summary>
[StructLayout(LayoutKind.Sequential, Pack = 4)]
internal struct LensParams
{
    public Vector2 LensPos;
    public Vector2 LensSize;
    public Vector2 SrcPos;
    public Vector2 TexSize;
    public float Zoom;
    public float Radius;
    public float Border;
    public float Opacity;
    public Vector4 BorderColor;
    public Vector4 MarkColor;
    public Vector2 MarkPos;
    public float MarkKind;
    public float MarkScale;
    public float Shadow;
    public float ShadowOpacity;
    public float ShadowOffset;
    public float Smooth;

    /// <summary>Прямоугольник, который реально нужно перерисовать: лупа и её тень.</summary>
    public readonly (int X, int Y, int W, int H) Bounds
    {
        get
        {
            var pad = Shadow + ShadowOffset + 2;
            var x = (int)MathF.Floor(LensPos.X - pad);
            var y = (int)MathF.Floor(LensPos.Y - pad);
            return (x, y, (int)MathF.Ceiling(LensSize.X + 2 * pad) + 1, (int)MathF.Ceiling(LensSize.Y + 2 * pad) + 1);
        }
    }

    public static LensParams Compute(Settings s, POINT cursor, RECT monitor, Vector2 texture, double scale, double zoom)
    {
        var monW = monitor.Right - monitor.Left;
        var monH = monitor.Bottom - monitor.Top;
        var cx = cursor.x - monitor.Left + 0.5;
        var cy = cursor.y - monitor.Top + 0.5;

        var w = Math.Max(8, Math.Round(s.LensWidth * scale));
        var h = Math.Max(8, Math.Round(s.LensHeight * scale));

        // Лупа — по центру курсора, но целиком в пределах монитора.
        var lensX = Place(cx - w / 2, w, monW);
        var lensY = Place(cy - h / 2, h, monH);

        // Источник — область вокруг курсора, прижатая к краям изображения.
        var srcX = Place(cx - w / zoom / 2, w / zoom, texture.X);
        var srcY = Place(cy - h / zoom / 2, h / zoom, texture.Y);

        return new LensParams
        {
            LensPos = new((float)lensX, (float)lensY),
            LensSize = new((float)w, (float)h),
            SrcPos = new((float)srcX, (float)srcY),
            TexSize = texture,
            Zoom = (float)zoom,
            Radius = (float)Math.Min(s.CornerRadius * scale, Math.Min(w, h) / 2),
            Border = s.BorderThickness == 0 ? 0 : (float)Math.Max(1, s.BorderThickness * scale),
            Opacity = 1,
            BorderColor = ToVector(s.BorderColor),
            MarkColor = ToVector(s.IndicatorColor),
            // Реальная точка курсора внутри лупы: у краёв монитора она уходит из центра.
            MarkPos = new((float)(lensX + (cx - srcX) * zoom), (float)(lensY + (cy - srcY) * zoom)),
            MarkKind = (float)s.Indicator,
            MarkScale = (float)scale,
            Shadow = s.ShadowEnabled && s.ShadowOpacity > 0 ? (float)(s.ShadowSize * scale) : 0,
            ShadowOpacity = s.ShadowOpacity / 100f,
            ShadowOffset = s.ShadowEnabled ? (float)(s.ShadowSize * scale / 4) : 0,
            Smooth = s.SmoothScaling ? 1 : 0,
        };
    }

    private static double Place(double start, double length, double max) =>
        length >= max ? (max - length) / 2 : Math.Clamp(start, 0, max - length);

    private static Vector4 ToVector(string argb)
    {
        var v = Settings.ParseArgb(argb);
        return new Vector4(((v >> 16) & 0xFF) / 255f, ((v >> 8) & 0xFF) / 255f, (v & 0xFF) / 255f, (v >> 24) / 255f);
    }
}

/// <summary>Direct3D 11: захват монитора, цепочка обмена DirectComposition и шейдер лупы.</summary>
internal sealed class GpuLens : IDisposable
{
    private readonly IntPtr _window;
    private readonly IDXGIFactory2 _factory;

    private IDXGIAdapter1? _adapter;
    private ID3D11Device? _device;
    private ID3D11DeviceContext? _context;
    private ID3D11VertexShader? _vs;
    private ID3D11PixelShader? _ps;
    private ID3D11Buffer? _constants;
    private ID3D11SamplerState? _sampler;

    private IDCompositionDevice? _dcomp;
    private IDCompositionTarget? _target;
    private IDCompositionVisual? _visual;
    private IDXGISwapChain1? _swapChain;
    private ID3D11RenderTargetView? _rtv;
    private int _swapW, _swapH;

    private IntPtr _monitor;
    private RECT _monitorRect;
    private IDXGIOutputDuplication? _duplication;
    private ID3D11Texture2D? _desktop;
    private ID3D11ShaderResourceView? _desktopView;
    private bool _hasFrame;

    public GpuLens(IntPtr window)
    {
        _window = window;
        _factory = DXGI.CreateDXGIFactory1<IDXGIFactory2>();
        // Устройство на основном адаптере создаём сразу: первый показ не ждёт компиляции шейдеров.
        _factory.EnumAdapters1(0, out var adapter).CheckError();
        CreateDevice(adapter);
    }

    public Vector2 TextureSize { get; private set; }

    /// <summary>Готовит захват монитора под курсором и цепочку обмена его размера.</summary>
    public bool Prepare(IntPtr monitor, RECT rect)
    {
        try
        {
            if (monitor != _monitor || !SameRect(rect, _monitorRect) || _duplication is null)
            {
                if (!SwitchMonitor(monitor, rect)) return false;
            }

            AcquireFrame();
            return _hasFrame;
        }
        catch (SharpGen.Runtime.SharpGenException ex) when (IsRecoverable(ex.ResultCode))
        {
            ReleaseCapture();
            if (ex.ResultCode == Vortice.DXGI.ResultCode.DeviceRemoved || ex.ResultCode == Vortice.DXGI.ResultCode.DeviceReset)
                RecreateDevice();
            return false;
        }
    }

    public void Render(in LensParams p)
    {
        var ctx = _context!;
        var constants = p;
        ctx.UpdateSubresource(in constants, _constants!);

        ctx.OMSetRenderTargets(_rtv!);
        ctx.ClearRenderTargetView(_rtv!, new Vortice.Mathematics.Color4(0, 0, 0, 0));

        var (x, y, w, h) = p.Bounds;
        ctx.RSSetViewport(x, y, w, h);
        ctx.IASetPrimitiveTopology(PrimitiveTopology.TriangleList);
        ctx.VSSetShader(_vs);
        ctx.PSSetShader(_ps);
        ctx.PSSetConstantBuffer(0, _constants);
        ctx.PSSetShaderResource(0, _desktopView!);
        ctx.PSSetSampler(0, _sampler);
        ctx.Draw(3, 0);

        _swapChain!.Present(1, PresentFlags.None);
    }

    public void Clear()
    {
        if (_swapChain is null || _rtv is null) return;
        _context!.OMSetRenderTargets(_rtv);
        _context.ClearRenderTargetView(_rtv, new Vortice.Mathematics.Color4(0, 0, 0, 0));
        _swapChain.Present(0, PresentFlags.None);
    }

    /// <summary>Отпускает захват экрана; устройство и шейдеры остаются.</summary>
    public void ReleaseCapture()
    {
        _desktopView?.Dispose(); _desktopView = null;
        _desktop?.Dispose(); _desktop = null;
        _duplication?.Dispose(); _duplication = null;
        _monitor = IntPtr.Zero;
        _hasFrame = false;
    }

    // ---- захват ----

    private bool SwitchMonitor(IntPtr monitor, RECT rect)
    {
        ReleaseCapture();

        var (adapter, output) = FindOutput(monitor);
        if (output is null) return false;

        using (output)
        {
            if (adapter is not null) RecreateDevice(adapter);

            using var output5 = output.QueryInterfaceOrNull<IDXGIOutput5>();
            if (output5 is not null)
                _duplication = output5.DuplicateOutput1(_device!, 1, [Format.B8G8R8A8_UNorm]);
            else
                using (var output1 = output.QueryInterface<IDXGIOutput1>())
                    _duplication = output1.DuplicateOutput(_device!);
        }

        var w = rect.Right - rect.Left;
        var h = rect.Bottom - rect.Top;
        _monitor = monitor;
        _monitorRect = rect;
        SetWindowPos(_window, HWND_TOPMOST, rect.Left, rect.Top, w, h, SWP_NOACTIVATE);
        EnsureSwapChain(w, h);
        return true;
    }

    /// <summary>Выход DXGI для монитора; адаптер возвращается, только если он не текущий.</summary>
    private (IDXGIAdapter1? Adapter, IDXGIOutput? Output) FindOutput(IntPtr monitor)
    {
        for (uint a = 0; _factory.EnumAdapters1(a, out var adapter).Success; a++)
        {
            for (uint o = 0; adapter.EnumOutputs(o, out var output).Success; o++)
            {
                if (output.Description.Monitor == monitor)
                {
                    var same = adapter.Description1.Luid.Equals(_adapter!.Description1.Luid);
                    if (same) adapter.Dispose();
                    return (same ? null : adapter, output);
                }
                output.Dispose();
            }
            adapter.Dispose();
        }
        return (null, null);
    }

    private void AcquireFrame()
    {
        var result = _duplication!.AcquireNextFrame(_hasFrame ? 0u : 200u, out var info, out var resource);
        if (result.Code == Vortice.DXGI.ResultCode.WaitTimeout.Code) return;
        result.CheckError();

        try
        {
            // LastPresentTime == 0 — изменился только указатель, изображение прежнее.
            if (info.LastPresentTime == 0 && _hasFrame) return;

            using var texture = resource.QueryInterface<ID3D11Texture2D>();
            var desc = texture.Description;
            if (_desktop is null || _desktop.Description.Width != desc.Width || _desktop.Description.Height != desc.Height)
            {
                _desktopView?.Dispose();
                _desktop?.Dispose();
                _desktop = _device!.CreateTexture2D(new Texture2DDescription(
                    desc.Format, desc.Width, desc.Height, 1, 1, BindFlags.ShaderResource));
                _desktopView = _device.CreateShaderResourceView(_desktop);
                TextureSize = new Vector2(desc.Width, desc.Height);
            }

            _context!.CopyResource(_desktop, texture);
            _hasFrame = true;
        }
        finally
        {
            resource.Dispose();
            _duplication.ReleaseFrame();
        }
    }

    // ---- устройство и вывод ----

    private void CreateDevice(IDXGIAdapter1 adapter)
    {
        _adapter = adapter;
        D3D11.D3D11CreateDevice(adapter, DriverType.Unknown, DeviceCreationFlags.BgraSupport,
            [Vortice.Direct3D.FeatureLevel.Level_11_1, Vortice.Direct3D.FeatureLevel.Level_11_0],
            out _device, out _context).CheckError();

        var source = LoadShaderSource();
        var vsCode = Vortice.D3DCompiler.Compiler.Compile(source, "VS", "LensShader.hlsl", "vs_5_0");
        var psCode = Vortice.D3DCompiler.Compiler.Compile(source, "PS", "LensShader.hlsl", "ps_5_0");
        _vs = _device!.CreateVertexShader(vsCode.Span);
        _ps = _device.CreatePixelShader(psCode.Span);

        _constants = _device.CreateBuffer(new BufferDescription(
            (uint)((Marshal.SizeOf<LensParams>() + 15) / 16 * 16), BindFlags.ConstantBuffer));
        _sampler = _device.CreateSamplerState(new SamplerDescription(Filter.MinMagMipLinear, TextureAddressMode.Clamp));

        // Очередь не длиннее одного кадра: Present(1) ждёт обновления экрана, и лупа
        // всегда рисуется по свежему положению курсора, без накопления задержки.
        using var dxgiDevice = _device.QueryInterface<IDXGIDevice1>();
        dxgiDevice.MaximumFrameLatency = 1;
        _dcomp = DComp.DCompositionCreateDevice<IDCompositionDevice>(dxgiDevice);
        _dcomp.CreateTargetForHwnd(_window, true, out _target).CheckError();
        _dcomp.CreateVisual(out _visual).CheckError();
        _target!.SetRoot(_visual);
    }

    private void EnsureSwapChain(int w, int h)
    {
        if (_swapChain is not null && w == _swapW && h == _swapH) return;

        _rtv?.Dispose();
        _rtv = null;

        if (_swapChain is null)
        {
            var desc = new SwapChainDescription1
            {
                Width = (uint)w,
                Height = (uint)h,
                Format = Format.B8G8R8A8_UNorm,
                SampleDescription = new SampleDescription(1, 0),
                BufferUsage = Usage.RenderTargetOutput,
                BufferCount = 2,
                SwapEffect = SwapEffect.FlipSequential,
                AlphaMode = AlphaMode.Premultiplied,
                Scaling = Scaling.Stretch,
            };
            _swapChain = _factory.CreateSwapChainForComposition(_device!, desc);
            _visual!.SetContent(_swapChain);
            _dcomp!.Commit();
        }
        else
        {
            _swapChain.ResizeBuffers(2, (uint)w, (uint)h, Format.B8G8R8A8_UNorm, SwapChainFlags.None).CheckError();
        }

        using var back = _swapChain.GetBuffer<ID3D11Texture2D>(0);
        _rtv = _device!.CreateRenderTargetView(back);
        _swapW = w;
        _swapH = h;
    }

    private void RecreateDevice(IDXGIAdapter1? adapter = null)
    {
        adapter ??= _factory.EnumAdapters1(0, out var first).Success ? first : throw new InvalidOperationException("No adapter");
        DisposeDevice();
        CreateDevice(adapter);
    }

    private static string LoadShaderSource()
    {
        using var stream = typeof(GpuLens).Assembly.GetManifestResourceStream("magniF.UI.LensShader.hlsl")!;
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }

    private static bool IsRecoverable(SharpGen.Runtime.Result code) =>
        code == Vortice.DXGI.ResultCode.AccessLost || code == Vortice.DXGI.ResultCode.DeviceRemoved
        || code == Vortice.DXGI.ResultCode.DeviceReset || code == Vortice.DXGI.ResultCode.InvalidCall
        || code == Vortice.DXGI.ResultCode.Unsupported || code == Vortice.DXGI.ResultCode.NotCurrentlyAvailable
        || code.Code == unchecked((int)0x80070005); // E_ACCESSDENIED — защищённый рабочий стол

    private static bool SameRect(RECT a, RECT b) =>
        a.Left == b.Left && a.Top == b.Top && a.Right == b.Right && a.Bottom == b.Bottom;

    private void DisposeDevice()
    {
        ReleaseCapture();
        _rtv?.Dispose(); _rtv = null;
        _visual?.SetContent(null);
        _swapChain?.Dispose(); _swapChain = null;
        _swapW = _swapH = 0;
        _visual?.Dispose(); _visual = null;
        _target?.Dispose(); _target = null;
        _dcomp?.Dispose(); _dcomp = null;
        _sampler?.Dispose(); _sampler = null;
        _constants?.Dispose(); _constants = null;
        _ps?.Dispose(); _ps = null;
        _vs?.Dispose(); _vs = null;
        _context?.ClearState();
        _context?.Dispose(); _context = null;
        _device?.Dispose(); _device = null;
        _adapter?.Dispose(); _adapter = null;
    }

    public void Dispose()
    {
        DisposeDevice();
        _factory.Dispose();
    }
}

