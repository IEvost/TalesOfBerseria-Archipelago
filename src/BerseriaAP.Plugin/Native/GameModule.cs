using System.Diagnostics;
using System.Runtime.InteropServices;

namespace BerseriaAP.Native;

/// <summary>
/// Locates functions inside the native game engine (TLSampleViewerTO12.dll).
/// Each target has a known RVA for game version 1.5.1.0 plus a byte signature; if the
/// bytes at the RVA don't match (game patched), the signature is searched in .text.
/// </summary>
internal static unsafe class GameModule
{
    public const string DllName = "TLSampleViewerTO12.dll";
    public static IntPtr Base { get; private set; }
    static byte* _text; static int _textSize;

    [DllImport("kernel32", CharSet = CharSet.Unicode)] static extern IntPtr GetModuleHandleW(string name);

    public static bool Init() => Init(GetModuleHandleW(DllName));

    public static bool Init(IntPtr moduleBase)
    {
        Base = moduleBase;
        if (Base == IntPtr.Zero) return false;
        var b = (byte*)Base;
        int pe = *(int*)(b + 0x3C);
        short nsec = *(short*)(b + pe + 6);
        short optSize = *(short*)(b + pe + 20);
        byte* sec = b + pe + 24 + optSize;
        for (int i = 0; i < nsec; i++, sec += 40)
        {
            if (sec[0] == '.' && sec[1] == 't' && sec[2] == 'e' && sec[3] == 'x' && sec[4] == 't')
            {
                _text = b + *(int*)(sec + 12);
                _textSize = *(int*)(sec + 8);
            }
        }
        return _text != null;
    }

    /// <summary>Resolve a function: verify signature at expected RVA, else scan .text.</summary>
    public static IntPtr Resolve(string name, int rva, string signature)
    {
        var (pat, mask) = Parse(signature);
        var expected = (byte*)Base + rva;
        if (Match(expected, pat, mask)) return (IntPtr)expected;
        for (int i = 0; i <= _textSize - pat.Length; i++)
            if (Match(_text + i, pat, mask))
            {
                Plugin.Log.LogWarning($"{name}: signature found at moved RVA 0x{(long)(_text + i - (byte*)Base):X}");
                return (IntPtr)(_text + i);
            }
        throw new InvalidOperationException($"Could not locate native function {name} (unsupported game version?)");
    }

    static bool Match(byte* p, byte[] pat, bool[] mask)
    {
        for (int i = 0; i < pat.Length; i++) if (mask[i] && p[i] != pat[i]) return false;
        return true;
    }

    static (byte[], bool[]) Parse(string sig)
    {
        var parts = sig.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        var pat = new byte[parts.Length]; var mask = new bool[parts.Length];
        for (int i = 0; i < parts.Length; i++)
        {
            if (parts[i] is "?" or "??") continue;
            pat[i] = Convert.ToByte(parts[i], 16); mask[i] = true;
        }
        return (pat, mask);
    }
}
