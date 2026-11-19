using System.Runtime.InteropServices;
using System.Text;
using BepInEx.Unity.IL2CPP.Hook;

namespace BerseriaAP.Native;

/// <summary>
/// Runs Lua code inside the game's embedded Lua 5.1 VM.
///
/// Every game script task is a coroutine that calls coroutine.yield() once per frame. We detour the
/// C function behind it (luaB_yield). Inside a C function it is legal to pcall on the same lua_State,
/// so our chunk runs there (on the game thread, between frames of that task). Our code never yields.
/// </summary>
internal static unsafe class LuaBridge
{
    // ---- Lua 5.1 struct offsets (x64 build, verified from disassembly) ----
    const int L_top = 0x10, L_lG = 0x20, L_stackLast = 0x38, L_stack = 0x40;
    const int G_mainthread = 0xB0;
    const int TValueSize = 16, LUA_TSTRING = 4, LUA_TNUMBER = 3, LUA_TBOOLEAN = 1;
    const int TString_len = 0x10, TString_data = 0x18;

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] delegate int LoadBufferFn(IntPtr L, byte* buf, ulong size, byte* name);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] delegate int DPcallFn(IntPtr L, IntPtr func, IntPtr u, long oldTop, long ef);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] delegate int LuaCFunction(IntPtr L);

    static LoadBufferFn _loadBuffer = null!;
    static DPcallFn _dpcall = null!;
    static IntPtr _fCall;
    static INativeDetour? _detour;

    /// <summary>The lua_State currently inside a safe point.</summary>
    public static IntPtr MainState { get; private set; }
    public static int GameThreadId { get; private set; }

    /// <summary>Called on the game thread whenever it is safe to run Lua (many times per frame).</summary>
    public static event Action? OnSafePoint;

    struct CallS { public IntPtr func; public int nresults; }

    public static void Init()
    {
        _loadBuffer = Marshal.GetDelegateForFunctionPointer<LoadBufferFn>(
            GameModule.Resolve("luaL_loadbuffer", 0x552150, "4C 8B DC 49 89 5B 08 49 89 73 10 57 48 81 EC 90 00 00 00 48 8B D9"));
        _fCall = GameModule.Resolve("f_call", 0x5172d0, "44 8B 42 08 48 8B 12 E9 ?? ?? ?? ?? CC CC CC CC 48 89 5C 24 08 48 89 74 24 10");
        _pcallHook = PcallHook;
        _detour = INativeDetour.CreateAndApply(
            GameModule.Resolve("luaD_pcall", 0x58ba60, "48 89 5C 24 08 48 89 6C 24 10 48 89 74 24 18 48 89 7C 24 20 41 54 41 56 41 57 48 83 EC 20 4C 8B 71 28"),
            _pcallHook, out _dpcall); // from now on _dpcall is the trampoline (unhooked original)
        Plugin.Log.LogInfo("Lua bridge hooks installed.");
    }

    static DPcallFn _pcallHook = null!; // keep delegate alive
    [ThreadStatic] static bool _inSafePoint;
    static readonly Dictionary<int, long> _threadCalls = new();
    static readonly System.Diagnostics.Stopwatch _statSw = System.Diagnostics.Stopwatch.StartNew();
    static int _mainThread = -1;

    /// <summary>
    /// Detour of luaD_pcall. Every native->Lua protected call (engine callbacks such as OnAreaExecute,
    /// run every frame) passes here while the state is consistent, so we can run our own chunk first.
    ///
    /// IMPORTANT: the caller has already computed CallS.func, a raw pointer into the Lua stack. Our chunk
    /// may grow (realloc) the stack, so we only act on f_call-style calls (known CallS layout) and rebase
    /// that pointer afterwards. Other Pfuncs (parser, resume, cpcall) are passed through untouched.
    /// </summary>
    static int PcallHook(IntPtr L, IntPtr func, IntPtr u, long oldTop, long ef)
    {
        if (!_inSafePoint && func == _fCall && u != IntPtr.Zero)
        {
            byte* stackBefore = *(byte**)((byte*)L + L_stack);
            _inSafePoint = true;
            try
            {
                int tid = Environment.CurrentManagedThreadId;
                lock (_threadCalls)
                {
                    _threadCalls[tid] = _threadCalls.GetValueOrDefault(tid) + 1;
                    if (_statSw.ElapsedMilliseconds > 10000)
                    {
                        _statSw.Restart();
                        var top = _threadCalls.OrderByDescending(kv => kv.Value).First();
                        _mainThread = top.Key;
                        Util.SafeLog.Debug("pcall threads: " + string.Join(", ", _threadCalls.Select(kv => $"{kv.Key}:{kv.Value}")));
                        _threadCalls.Clear();
                    }
                }
                if (tid == _mainThread)
                {
                    MainState = L;
                    GameThreadId = tid;
                    OnSafePoint?.Invoke();
                }
            }
            catch (Exception e) { Util.SafeLog.Error($"SafePoint error: {e}"); }
            finally
            {
                _inSafePoint = false;
                byte* stackAfter = *(byte**)((byte*)L + L_stack);
                if (stackAfter != stackBefore)
                {
                    var c = (CallS*)u;
                    c->func = (IntPtr)(stackAfter + ((byte*)c->func - stackBefore));
                }
            }
        }
        return _dpcall(L, func, u, oldTop, ef);
    }

    /// <summary>
    /// Execute a Lua chunk on the main state. Must be called from <see cref="OnSafePoint"/>.
    /// Returns (ok, result) where result is the first return value converted to string, or the error.
    /// </summary>
    public static (bool ok, string result) Execute(string code, string chunkName = "=BerseriaAP")
    {
        var L = MainState;
        if (L == IntPtr.Zero) return (false, "no lua state");
        byte* p = (byte*)L;
        // Stack may be reallocated while our chunk runs: remember top as an offset, never as a pointer.
        long savedTopOff = (long)*(IntPtr*)(p + L_top) - (long)*(IntPtr*)(p + L_stack);
        if (*(long*)(p + L_stackLast) - (long)*(IntPtr*)(p + L_top) < 8 * TValueSize) return (false, "lua stack too small");

        var src = Encoding.UTF8.GetBytes(code);
        var name = Encoding.ASCII.GetBytes(chunkName + "\0");
        int status;
        fixed (byte* s = src) fixed (byte* n = name)
            status = _loadBuffer(L, s, (ulong)src.Length, n);

        if (status == 0)
        {
            var c = new CallS { func = *(IntPtr*)(p + L_top) - TValueSize, nresults = 1 };
            long oldTop = (long)c.func - (long)*(IntPtr*)(p + L_stack);
            status = _dpcall(L, _fCall, (IntPtr)(&c), oldTop, 0);
        }

        // Result (or error message) sits at the old top.
        var savedTop = (IntPtr)((long)*(IntPtr*)(p + L_stack) + savedTopOff);
        string result = ReadValue(savedTop);
        *(IntPtr*)(p + L_top) = savedTop;
        return (status == 0, result);
    }

    static string ReadValue(IntPtr tv)
    {
        byte* v = (byte*)tv;
        int tt = *(int*)(v + 8);
        switch (tt)
        {
            case 0: return "nil";
            case LUA_TBOOLEAN: return *(int*)v != 0 ? "true" : "false";
            case LUA_TNUMBER: return (*(double*)v).ToString(System.Globalization.CultureInfo.InvariantCulture);
            case LUA_TSTRING:
                byte* ts = *(byte**)v;
                long len = *(long*)(ts + TString_len);
                return Encoding.UTF8.GetString(ts + TString_data, (int)Math.Min(len, 16 * 1024 * 1024));
            default: return $"<type {tt}>";
        }
    }
}
