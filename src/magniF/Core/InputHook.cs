using static magniF.Interop.NativeMethods;

namespace magniF.Core;

/// <summary>
/// Низкоуровневые хуки клавиатуры и мыши на собственном потоке: колбэки не зависят от
/// загрузки UI и отвечают системе мгновенно. Здесь же живёт распознавание удержания бинда:
/// <c>Idle → Pending → Active</c>, с отменой, если во время ожидания нажато что-то ещё
/// (так Ctrl+C не вызывает лупу).
/// </summary>
internal sealed class InputHook : IDisposable
{
    private const uint WM_TIMER_FIRED = WM_APP + 1;
    private const uint WM_BEGIN_RECORD = WM_APP + 2;
    private const uint WM_CANCEL_RECORD = WM_APP + 3;
    private const uint WM_RESYNC = WM_APP + 4;

    /// <summary>Метка собственных инжектированных нажатий — хук их пропускает.</summary>
    private static readonly UIntPtr OwnInputTag = new(0x6D61676E); // "magn"

    private enum Phase { Idle, Pending, Active, Cancelled }

    private readonly Func<Settings> _settings;
    private readonly Thread _thread;
    private readonly ManualResetEventSlim _ready = new();
    private readonly HashSet<int> _down = [];
    private readonly Timer _timer;

    // Делегаты держим в полях, иначе GC соберёт их, пока хук установлен.
    private readonly HookProc _keyboardProc;
    private readonly HookProc _mouseProc;

    private uint _threadId;
    private IntPtr _keyboardHook;
    private IntPtr _mouseHook;

    private Phase _phase;
    private int _generation;

    private bool _recording;
    private readonly List<int> _recorded = [];
    private Action<List<int>?>? _recordDone;

    public InputHook(Func<Settings> settings)
    {
        _settings = settings;
        _keyboardProc = KeyboardProc;
        _mouseProc = MouseProc;
        _timer = new Timer(_ => PostThreadMessage(_threadId, WM_TIMER_FIRED, Volatile.Read(ref _generation), 0));

        _thread = new Thread(Run) { IsBackground = true, Name = "magniF.InputHook" };
        _thread.Start();
        _ready.Wait();
    }

    /// <summary>Бинд удержан достаточно долго — показать лупу. Вызывается на потоке хуков.</summary>
    public event Action? Activated;

    /// <summary>Бинд отпущен. Вызывается на потоке хуков.</summary>
    public event Action? Deactivated;

    /// <summary>Колесо при открытой лупе: число делений со знаком. Вызывается на потоке хуков.</summary>
    public event Action<double>? Wheel;

    /// <summary>
    /// Следующее нажатие (клавиши, комбинации или кнопки мыши) записывается как бинд и не уходит
    /// в другие программы. <paramref name="done"/> получает коды или null при Esc; вызывается на потоке хуков.
    /// </summary>
    public void BeginRecording(Action<List<int>?> done)
    {
        _recordDone = done;
        PostThreadMessage(_threadId, WM_BEGIN_RECORD, 0, 0);
    }

    public void CancelRecording() => PostThreadMessage(_threadId, WM_CANCEL_RECORD, 0, 0);

    /// <summary>Лупа заметила, что бинд уже не зажат, а отпускание мы пропустили.</summary>
    public void Resync() => PostThreadMessage(_threadId, WM_RESYNC, 0, 0);

    private void Run()
    {
        _threadId = GetCurrentThreadId();
        PeekMessage(out _, IntPtr.Zero, 0, 0, PM_NOREMOVE); // создать очередь до первых PostThreadMessage

        var module = GetModuleHandle(null);
        _keyboardHook = SetWindowsHookEx(WH_KEYBOARD_LL, _keyboardProc, module, 0);
        _mouseHook = SetWindowsHookEx(WH_MOUSE_LL, _mouseProc, module, 0);
        _ready.Set();

        while (GetMessage(out var msg, IntPtr.Zero, 0, 0) > 0)
        {
            switch (msg.message)
            {
                case WM_TIMER_FIRED:
                    if (_phase == Phase.Pending && (int)msg.wParam == _generation) Activate();
                    break;
                case WM_BEGIN_RECORD:
                    Cancel();
                    _recording = true;
                    _recorded.Clear();
                    break;
                case WM_CANCEL_RECORD:
                    FinishRecording(null);
                    break;
                case WM_RESYNC:
                    PruneReleased(except: -1);
                    if (_phase == Phase.Active) Deactivate();
                    _phase = Phase.Idle;
                    break;
                default:
                    TranslateMessage(ref msg);
                    DispatchMessage(ref msg);
                    break;
            }
        }

        if (_keyboardHook != IntPtr.Zero) UnhookWindowsHookEx(_keyboardHook);
        if (_mouseHook != IntPtr.Zero) UnhookWindowsHookEx(_mouseHook);
    }

    private IntPtr KeyboardProc(int nCode, IntPtr wParam, IntPtr lParam)
    {
        if (nCode < 0) return CallNextHookEx(_keyboardHook, nCode, wParam, lParam);

        var data = System.Runtime.InteropServices.Marshal.PtrToStructure<KBDLLHOOKSTRUCT>(lParam);
        if (data.dwExtraInfo == OwnInputTag) return CallNextHookEx(_keyboardHook, nCode, wParam, lParam);

        var vk = (int)data.vkCode;
        var msg = (int)wParam;
        var swallow = msg is WM_KEYDOWN or WM_SYSKEYDOWN ? OnDown(vk) : OnUp(vk);

        return swallow ? 1 : CallNextHookEx(_keyboardHook, nCode, wParam, lParam);
    }

    private IntPtr MouseProc(int nCode, IntPtr wParam, IntPtr lParam)
    {
        if (nCode < 0) return CallNextHookEx(_mouseHook, nCode, wParam, lParam);

        var data = System.Runtime.InteropServices.Marshal.PtrToStructure<MSLLHOOKSTRUCT>(lParam);
        var swallow = false;

        switch ((int)wParam)
        {
            case WM_MBUTTONDOWN: swallow = OnDown(KeyBinding.VK_MBUTTON); break;
            case WM_MBUTTONUP: swallow = OnUp(KeyBinding.VK_MBUTTON); break;
            case WM_XBUTTONDOWN: swallow = OnDown(XButton(data.mouseData)); break;
            case WM_XBUTTONUP: swallow = OnUp(XButton(data.mouseData)); break;
            case WM_LBUTTONDOWN or WM_RBUTTONDOWN:
                // Клик во время ожидания — это Ctrl+клик, а не просьба об увеличении.
                if (_phase == Phase.Pending) Cancel();
                break;
            case WM_MOUSEWHEEL:
                if (_phase == Phase.Active && _settings().WheelZoom)
                {
                    var delta = (short)(data.mouseData >> 16);
                    Wheel?.Invoke(delta / 120.0);
                    swallow = true;
                }
                break;
        }

        return swallow ? 1 : CallNextHookEx(_mouseHook, nCode, wParam, lParam);
    }

    private static int XButton(uint mouseData) =>
        (mouseData >> 16) == 1 ? KeyBinding.VK_XBUTTON1 : KeyBinding.VK_XBUTTON2;

    /// <returns>true — событие не передаётся дальше.</returns>
    private bool OnDown(int vk)
    {
        var repeat = !_down.Add(vk);

        if (_recording)
        {
            if (!repeat)
            {
                if (vk == KeyBinding.VK_ESCAPE && _recorded.Count == 0) FinishRecording(null);
                else _recorded.Add(vk);
            }
            return true;
        }

        if (repeat) return false;
        PruneReleased(except: vk);

        var settings = _settings();
        var bind = settings.Binding;
        var matches = settings.Enabled && bind.Count > 0 && IsExactly(bind);

        switch (_phase)
        {
            case Phase.Idle when matches:
                _phase = Phase.Pending;
                Interlocked.Increment(ref _generation);
                if (settings.ActivationDelay <= 0) Activate();
                else _timer.Change(settings.ActivationDelay, Timeout.Infinite);
                break;

            case Phase.Idle when bind.Contains(KeyBinding.Normalize(vk)):
                // Часть бинда при зажатой посторонней клавише — ждём полного отпускания.
                break;

            case Phase.Pending:
                Cancel();
                break;
        }

        return false;
    }

    private bool OnUp(int vk)
    {
        _down.Remove(vk);

        if (_recording)
        {
            _down.RemoveWhere(d => (GetAsyncKeyState(d) & 0x8000) == 0);
            if (_down.Count == 0 && _recorded.Count > 0) FinishRecording(KeyBinding.Normalize(_recorded));
            return true;
        }

        var bind = _settings().Binding;
        if (!bind.Contains(KeyBinding.Normalize(vk)) || IsHeld(KeyBinding.Normalize(vk))) return false;

        if (_phase == Phase.Active) Deactivate();
        _phase = Phase.Idle;
        Interlocked.Increment(ref _generation);
        return false;
    }

    private void Activate()
    {
        _phase = Phase.Active;

        // Отпускание одиночного Alt активирует меню окна, одиночного Win — «Пуск».
        // Нейтральное нажатие между ними превращает это в «комбинацию».
        var bind = _settings().Binding;
        if (bind.Contains(KeyBinding.VK_MENU) || bind.Contains(KeyBinding.VK_LWIN)) SendMaskKey();

        Activated?.Invoke();
    }

    private void Deactivate() => Deactivated?.Invoke();

    private void Cancel()
    {
        Interlocked.Increment(ref _generation);
        if (_phase is Phase.Pending) _phase = Phase.Cancelled;
    }

    private bool IsHeld(int normalized) => _down.Any(d => KeyBinding.Normalize(d) == normalized);

    private bool IsExactly(List<int> bind)
    {
        if (!bind.All(IsHeld)) return false;
        return _down.All(d => bind.Contains(KeyBinding.Normalize(d)));
    }

    /// <summary>
    /// Хук может пропустить отпускание (блокировка сеанса, UAC, окно администратора).
    /// Сверяемся с реальным состоянием клавиш, чтобы «залипшая» клавиша не ломала бинд.
    /// </summary>
    private void PruneReleased(int except)
    {
        _down.RemoveWhere(vk => vk != except && (GetAsyncKeyState(vk) & 0x8000) == 0);
        if (_phase == Phase.Cancelled && !_settings().Binding.Any(IsHeld)) _phase = Phase.Idle;
    }

    private void FinishRecording(List<int>? result)
    {
        if (!_recording) return;
        _recording = false;
        _recorded.Clear();
        _phase = Phase.Cancelled; // клавиши записи ещё могут быть зажаты
        PruneReleased(except: -1);
        var done = _recordDone;
        _recordDone = null;
        done?.Invoke(result);
    }

    private static void SendMaskKey()
    {
        var inputs = new INPUT[2];
        inputs[0].type = INPUT_KEYBOARD;
        inputs[0].ki = new KEYBDINPUT { wVk = KeyBinding.VK_MASK, dwExtraInfo = OwnInputTag };
        inputs[1].type = INPUT_KEYBOARD;
        inputs[1].ki = new KEYBDINPUT { wVk = KeyBinding.VK_MASK, dwFlags = KEYEVENTF_KEYUP, dwExtraInfo = OwnInputTag };
        SendInput(2, inputs, System.Runtime.InteropServices.Marshal.SizeOf<INPUT>());
    }

    public void Dispose()
    {
        _timer.Dispose();
        PostThreadMessage(_threadId, WM_QUIT, 0, 0);
        _thread.Join(1000);
    }
}
