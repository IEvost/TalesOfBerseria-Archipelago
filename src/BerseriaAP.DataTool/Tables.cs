using System.Text;

namespace BerseriaAP.DataTool;

/// <summary>
/// Reader for the engine's binary database tables (*.DAT).
/// Layout: header = N x (u64 offset, u64 count) pairs, the first offset equals the header size.
/// Records are fixed-size; string fields are self-relative i64 offsets to NUL-terminated UTF-8.
/// </summary>
internal sealed class Table
{
    public byte[] Data { get; }
    public List<(long Offset, long Count)> Sections { get; } = new();

    public Table(byte[] data)
    {
        Data = data;
        long headerSize = BitConverter.ToInt64(data, 0);
        for (int i = 0; i < headerSize; i += 16)
            Sections.Add((BitConverter.ToInt64(data, i), BitConverter.ToInt64(data, i + 8)));
    }

    public uint U32(long off) => BitConverter.ToUInt32(Data, (int)off);

    public string? RelString(long fieldOff)
    {
        long rel = BitConverter.ToInt64(Data, (int)fieldOff);
        if (rel == 0) return null;
        long p = fieldOff + rel;
        if (p < 0 || p >= Data.Length) return null;
        int end = Array.IndexOf(Data, (byte)0, (int)p);
        return end < 0 ? null : Encoding.UTF8.GetString(Data, (int)p, end - (int)p);
    }

    public string? TryRelString(long fieldOff)
    {
        try { return RelString(fieldOff); } catch { return null; }
    }

    /// <summary>Records of section 0 for tables shaped like {u32 Id; ...; str Map @+8; str Area @+0x10}.</summary>
    public IEnumerable<(uint Id, string Map, string Area)> PlacedRecords(int stride)
    {
        var (o, c) = Sections[0];
        for (long i = 0; i < c; i++)
        {
            long r = o + i * stride;
            yield return (U32(r), RelString(r + 8) ?? "", RelString(r + 0x10) ?? "");
        }
    }
}
