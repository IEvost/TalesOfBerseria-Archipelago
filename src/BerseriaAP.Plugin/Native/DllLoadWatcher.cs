using System.Runtime.InteropServices;

namespace BerseriaAP.Native;

/// <summary>
/// Runs a callback synchronously when a given DLL is mapped into the process (LdrRegisterDllNotification),
/// i.e. before any of its code (DllMain, game thread, Lua) has executed. Installing detours here avoids
/// the race of patching a function while another thread is executing it.
/// </summary>
internal static unsafe class DllLoadWatcher
{
    [StructLayout(LayoutKind.Sequential)]
    struct UNICODE_STRING { public ushort Length, MaximumLength; public IntPtr Buffer; }

    [StructLayout(LayoutKind.Sequential)]
    struct LDR_DLL_LOADED_NOTIFICATION_DATA
    {
        public uint Flags;
        public UNICODE_STRING* FullDllName;
        public UNICODE_STRING* BaseDllName;
        public IntPtr DllBase;
        public uint SizeOfImage;
    }

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    delegate void LdrNotification(uint reason, LDR_DLL_LOADED_NOTIFICATION_DATA* data, IntPtr context);

    [DllImport("ntdll")] static extern int LdrRegisterDllNotification(uint flags, LdrNotification cb, IntPtr ctx, out IntPtr cookie);
    [DllImport("ntdll")] static extern int LdrUnregisterDllNotification(IntPtr cookie);

    const uint LDR_DLL_NOTIFICATION_REASON_LOADED = 1;
    static LdrNotification? _cb; // keep alive
    static IntPtr _cookie;

    /// <summary>Calls onLoaded(base) on the loader thread when dllName is loaded. Returns false if registration failed.</summary>
    public static bool Watch(string dllName, Action<IntPtr> onLoaded)
    {
        _cb = (reason, data, _) =>
        {
            if (reason != LDR_DLL_NOTIFICATION_REASON_LOADED || data == null || data->BaseDllName == null) return;
            var name = new string((char*)data->BaseDllName->Buffer, 0, data->BaseDllName->Length / 2);
            if (!name.Equals(dllName, StringComparison.OrdinalIgnoreCase)) return;
            try { onLoaded(data->DllBase); }
            catch (Exception e) { Plugin.Log.LogError(e); }
        };
        // Pre-JIT the callback so no JIT work happens under the loader lock.
        System.Runtime.CompilerServices.RuntimeHelpers.PrepareDelegate(_cb);
        return LdrRegisterDllNotification(0, _cb, IntPtr.Zero, out _cookie) == 0;
    }
}
