using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using BerseriaAP.Util;

namespace BerseriaAP.Ui;

/// <summary>
/// Small always-on-top Win32 window to connect to Archipelago and watch the message log.
/// (The game's Unity build has IMGUI stripped, so this is a native window with its own message loop.)
/// Toggle with F1 while the game window is in front.
/// </summary>
internal sealed class ConnectWindow
{
    public sealed record Settings(string Server, string Slot, string Password);

    readonly Func<Settings> _getSettings;
    readonly Action<Settings> _onConnect;
    readonly Action _onDisconnect;
    readonly Func<string> _getStatus;
    readonly Func<IEnumerable<string>> _drainLog;

    IntPtr _hwnd, _eServer, _eSlot, _ePass, _bConnect, _bDisconnect, _sStatus, _eLog, _font;
    WndProc? _wndProc; // keep alive
    bool _visible;
    string _lastStatus = "";
    readonly StringBuilder _log = new();

    public ConnectWindow(Func<Settings> getSettings, Action<Settings> onConnect, Action onDisconnect,
                         Func<string> getStatus, Func<IEnumerable<string>> drainLog)
    {
        _getSettings = getSettings; _onConnect = onConnect; _onDisconnect = onDisconnect;
        _getStatus = getStatus; _drainLog = drainLog;
    }

    public void Start(bool showImmediately)
    {
        var t = new Thread(() => Run(showImmediately)) { IsBackground = true, Name = "BerseriaAP UI" };
        t.SetApartmentState(ApartmentState.STA);
        t.Start();
    }

    // ------------------------------------------------------------------ window thread
    const int IdConnect = 101, IdDisconnect = 102, TimerId = 1;
    const int W = 460, H = 380;

    void Run(bool show)
    {
        try
        {
            var hInst = GetModuleHandleW(null);
            _wndProc = WindowProc;
            var wc = new WNDCLASSEXW
            {
                cbSize = (uint)Marshal.SizeOf<WNDCLASSEXW>(),
                lpfnWndProc = Marshal.GetFunctionPointerForDelegate(_wndProc),
                hInstance = hInst,
                hCursor = LoadCursorW(IntPtr.Zero, (IntPtr)32512),
                hbrBackground = (IntPtr)(COLOR_BTNFACE + 1),
                lpszClassName = "BerseriaAPConnect",
            };
            if (RegisterClassExW(ref wc) == 0)
                throw new InvalidOperationException($"RegisterClassExW failed: {Marshal.GetLastWin32Error()}");
            _hwnd = CreateWindowExW(WS_EX_TOPMOST | WS_EX_TOOLWINDOW, "BerseriaAPConnect", "Archipelago — Tales of Berseria (F1)",
                WS_CAPTION | WS_SYSMENU, 100, 100, W, H, IntPtr.Zero, IntPtr.Zero, hInst, IntPtr.Zero);
            if (_hwnd == IntPtr.Zero)
                throw new InvalidOperationException($"CreateWindowExW failed: {Marshal.GetLastWin32Error()}");
            SafeLog.Info("Connect window ready (F1 to toggle).");
            _font = CreateFontW(-15, 0, 0, 0, 400, 0, 0, 0, 1, 0, 0, 5, 0, "Segoe UI");

            int y = 14;
            Label("Server", 14, y); _eServer = Edit(110, y - 3, 320, false); y += 32;
            Label("Slot name", 14, y); _eSlot = Edit(110, y - 3, 320, false); y += 32;
            Label("Password", 14, y); _ePass = Edit(110, y - 3, 320, true); y += 36;
            _bConnect = Button("Connect", IdConnect, 110, y, 120);
            _bDisconnect = Button("Disconnect", IdDisconnect, 240, y, 120); y += 40;
            _sStatus = Control("STATIC", "", 0, 14, y, 416, 20); y += 26;
            _eLog = Control("EDIT", "", WS_BORDER | WS_VSCROLL | ES_MULTILINE | ES_READONLY | ES_AUTOVSCROLL, 14, y, 416, H - y - 50);

            var s = _getSettings();
            SetWindowTextW(_eServer, s.Server); SetWindowTextW(_eSlot, s.Slot); SetWindowTextW(_ePass, s.Password);
            SetTimer(_hwnd, (IntPtr)TimerId, 100, IntPtr.Zero);
            if (show) Show(true);

            while (GetMessageW(out var msg, IntPtr.Zero, 0, 0) > 0)
            {
                // Enter in a text box = Connect; Esc hides the window.
                if (msg.message == WM_KEYDOWN && msg.wParam == (IntPtr)VK_RETURN && IsChild(_hwnd, msg.hwnd)) { DoConnect(); continue; }
                if (msg.message == WM_KEYDOWN && msg.wParam == (IntPtr)VK_ESCAPE) { Show(false); continue; }
                if (!IsDialogMessageW(_hwnd, ref msg)) { TranslateMessage(ref msg); DispatchMessageW(ref msg); }
            }
        }
        catch (Exception e) { SafeLog.Error("Connect window failed: " + e); }
    }

    bool _f1Down;
    IntPtr WindowProc(IntPtr hwnd, uint msg, IntPtr wParam, IntPtr lParam)
    {
        switch (msg)
        {
            case WM_COMMAND:
                int id = (int)((long)wParam & 0xFFFF);
                if (id == IdConnect) DoConnect();
                else if (id == IdDisconnect) _onDisconnect();
                return IntPtr.Zero;
            case WM_TIMER:
                OnTimer();
                return IntPtr.Zero;
            case WM_CLOSE:
                Show(false); // never destroy; just hide
                return IntPtr.Zero;
        }
        return DefWindowProcW(hwnd, msg, wParam, lParam);
    }

    void OnTimer()
    {
        // F1 toggles while the game (or this window) is in the foreground.
        bool ours = ForegroundIsOurs();
        bool down = (GetAsyncKeyState(VK_F1) & 0x8000) != 0;
        if (down && !_f1Down && ours) Show(!_visible);
        _f1Down = down;

        // Only stay on top of the game: hide while another application is in front,
        // and come back (without stealing focus) when the game is active again.
        if (_visible && !ours && !_suspended) { _suspended = true; ShowWindow(_hwnd, SW_HIDE); }
        else if (_suspended && ours) { _suspended = false; ShowWindow(_hwnd, SW_SHOWNOACTIVATE); }

        var st = _getStatus();
        if (st != _lastStatus) { _lastStatus = st; SetWindowTextW(_sStatus, "Status: " + st); }

        bool added = false;
        foreach (var line in _drainLog())
        {
            _log.Append(line).Append("\r\n");
            added = true;
        }
        if (added)
        {
            if (_log.Length > 20000) _log.Remove(0, _log.Length - 15000);
            SetWindowTextW(_eLog, _log.ToString());
            SendMessageW(_eLog, EM_LINESCROLL, IntPtr.Zero, (IntPtr)100000);
        }
    }

    void DoConnect()
    {
        var s = new Settings(GetText(_eServer).Trim(), GetText(_eSlot).Trim(), GetText(_ePass));
        if (s.Slot.Length == 0) { SetWindowTextW(_sStatus, "Status: enter your slot name"); return; }
        _onConnect(s);
    }

    IntPtr _gameWindow;
    bool _suspended;

    void Show(bool show)
    {
        _visible = show;
        _suspended = false;
        if (show)
        {
            var fg = GetForegroundWindow();
            if (fg != _hwnd && IsOurProcess(fg)) _gameWindow = fg;
            // Center over the game window.
            if (_gameWindow != IntPtr.Zero && GetWindowRect(_gameWindow, out var r))
                SetWindowPos(_hwnd, HWND_TOPMOST, (r.Left + r.Right - W) / 2, (r.Top + r.Bottom - H) / 2, 0, 0, SWP_NOSIZE);
            ShowWindow(_hwnd, SW_SHOW);
            SetForegroundWindow(_hwnd);
            SetFocus(GetText(_eSlot).Length == 0 ? _eSlot : _bConnect);
        }
        else
        {
            ShowWindow(_hwnd, SW_HIDE);
            if (_gameWindow != IntPtr.Zero) SetForegroundWindow(_gameWindow);
        }
    }

    bool ForegroundIsOurs() => IsOurProcess(GetForegroundWindow());
    static bool IsOurProcess(IntPtr h)
    {
        if (h == IntPtr.Zero) return false;
        GetWindowThreadProcessId(h, out uint pid);
        return pid == (uint)Environment.ProcessId;
    }

    // ------------------------------------------------------------------ control helpers
    void Label(string text, int x, int y) => Control("STATIC", text, 0, x, y, 92, 22);
    IntPtr Edit(int x, int y, int w, bool password) =>
        Control("EDIT", "", WS_BORDER | WS_TABSTOP | ES_AUTOHSCROLL | (password ? ES_PASSWORD : 0), x, y, w, 26);
    IntPtr Button(string text, int id, int x, int y, int w)
    {
        var h = CreateWindowExW(0, "BUTTON", text, WS_CHILD | WS_VISIBLE | WS_TABSTOP, x, y, w, 30, _hwnd, (IntPtr)id, IntPtr.Zero, IntPtr.Zero);
        SendMessageW(h, WM_SETFONT, _font, (IntPtr)1);
        return h;
    }
    IntPtr Control(string cls, string text, uint style, int x, int y, int w, int h)
    {
        var c = CreateWindowExW(0, cls, text, WS_CHILD | WS_VISIBLE | style, x, y, w, h, _hwnd, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero);
        SendMessageW(c, WM_SETFONT, _font, (IntPtr)1);
        return c;
    }
    static string GetText(IntPtr h)
    {
        int n = GetWindowTextLengthW(h);
        var sb = new StringBuilder(n + 1);
        GetWindowTextW(h, sb, n + 1);
        return sb.ToString();
    }

    // ------------------------------------------------------------------ Win32
    delegate IntPtr WndProc(IntPtr hwnd, uint msg, IntPtr wParam, IntPtr lParam);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    struct WNDCLASSEXW
    {
        public uint cbSize, style; public IntPtr lpfnWndProc; public int cbClsExtra, cbWndExtra;
        public IntPtr hInstance, hIcon, hCursor, hbrBackground; public string? lpszMenuName; public string lpszClassName; public IntPtr hIconSm;
    }
    [StructLayout(LayoutKind.Sequential)] struct MSG { public IntPtr hwnd; public uint message; public IntPtr wParam, lParam; public uint time; public int ptX, ptY; }
    [StructLayout(LayoutKind.Sequential)] struct RECT { public int Left, Top, Right, Bottom; }

    const uint WS_CAPTION = 0x00C00000, WS_SYSMENU = 0x00080000, WS_CHILD = 0x40000000, WS_VISIBLE = 0x10000000,
        WS_BORDER = 0x00800000, WS_TABSTOP = 0x00010000, WS_VSCROLL = 0x00200000,
        WS_EX_TOPMOST = 0x8, WS_EX_TOOLWINDOW = 0x80,
        ES_AUTOHSCROLL = 0x80, ES_PASSWORD = 0x20, ES_MULTILINE = 0x4, ES_READONLY = 0x800, ES_AUTOVSCROLL = 0x40;
    const uint WM_COMMAND = 0x111, WM_TIMER = 0x113, WM_CLOSE = 0x10, WM_SETFONT = 0x30, WM_KEYDOWN = 0x100, EM_LINESCROLL = 0xB6;
    const int VK_F1 = 0x70, VK_RETURN = 0x0D, VK_ESCAPE = 0x1B, SW_SHOW = 5, SW_HIDE = 0, SW_SHOWNOACTIVATE = 4, COLOR_BTNFACE = 15;
    const uint SWP_NOSIZE = 1;
    static readonly IntPtr HWND_TOPMOST = (IntPtr)(-1);

    [DllImport("user32", CharSet = CharSet.Unicode, SetLastError = true)] static extern ushort RegisterClassExW(ref WNDCLASSEXW wc);
    [DllImport("user32", CharSet = CharSet.Unicode, SetLastError = true)] static extern IntPtr CreateWindowExW(uint ex, string cls, string name, uint style, int x, int y, int w, int h, IntPtr parent, IntPtr menu, IntPtr inst, IntPtr param);
    [DllImport("user32")] static extern IntPtr DefWindowProcW(IntPtr h, uint m, IntPtr w, IntPtr l);
    [DllImport("user32")] static extern int GetMessageW(out MSG m, IntPtr h, uint a, uint b);
    [DllImport("user32")] static extern bool TranslateMessage(ref MSG m);
    [DllImport("user32")] static extern IntPtr DispatchMessageW(ref MSG m);
    [DllImport("user32")] static extern bool IsDialogMessageW(IntPtr h, ref MSG m);
    [DllImport("user32")] static extern bool IsChild(IntPtr parent, IntPtr h);
    [DllImport("user32")] static extern bool ShowWindow(IntPtr h, int cmd);
    [DllImport("user32")] static extern bool SetForegroundWindow(IntPtr h);
    [DllImport("user32")] static extern IntPtr GetForegroundWindow();
    [DllImport("user32")] static extern IntPtr SetFocus(IntPtr h);
    [DllImport("user32")] static extern bool SetWindowPos(IntPtr h, IntPtr after, int x, int y, int cx, int cy, uint f);
    [DllImport("user32")] static extern bool GetWindowRect(IntPtr h, out RECT r);
    [DllImport("user32")] static extern uint GetWindowThreadProcessId(IntPtr h, out uint pid);
    [DllImport("user32")] static extern IntPtr SetTimer(IntPtr h, IntPtr id, uint ms, IntPtr fn);
    [DllImport("user32")] static extern short GetAsyncKeyState(int vk);
    [DllImport("user32", CharSet = CharSet.Unicode)] static extern bool SetWindowTextW(IntPtr h, string s);
    [DllImport("user32", CharSet = CharSet.Unicode)] static extern int GetWindowTextW(IntPtr h, StringBuilder sb, int n);
    [DllImport("user32")] static extern int GetWindowTextLengthW(IntPtr h);
    [DllImport("user32")] static extern IntPtr SendMessageW(IntPtr h, uint m, IntPtr w, IntPtr l);
    [DllImport("user32")] static extern IntPtr LoadCursorW(IntPtr inst, IntPtr id);
    [DllImport("kernel32", CharSet = CharSet.Unicode)] static extern IntPtr GetModuleHandleW(string? name);
    [DllImport("gdi32", CharSet = CharSet.Unicode)] static extern IntPtr CreateFontW(int h, int w, int esc, int ori, int weight, uint italic, uint underline, uint strike, uint charset, uint outPrec, uint clipPrec, uint quality, uint pitch, string face);
}
