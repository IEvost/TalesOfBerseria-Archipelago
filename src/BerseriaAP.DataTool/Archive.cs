using TLTool;

namespace BerseriaAP.DataTool;

/// <summary>Read-only access to an encrypted TLDAT archive (FILEHEADER.TOFHDA/TOFHDB + TLFILE.TLDAT).</summary>
internal sealed class Archive
{
    public TLDataHeader Header { get; }
    readonly TLDataEncryptHeader? _crypt;

    public Archive(string dir)
    {
        var hdb = File.ReadAllBytes(Path.Combine(dir, "FILEHEADER.TOFHDB"));
        var hda = Path.Combine(dir, "FILEHEADER.TOFHDA");
        if (File.Exists(hda))
        {
            _crypt = new TLDataEncryptHeader(File.ReadAllBytes(hda));
            TLCrypt.Decrypt(hdb, _crypt.GetHeaderKey());
        }
        Header = new TLDataHeader();
        using var ms = new MemoryStream(hdb);
        Header.ReadFrom(new BinaryStream(ms, false), new FileInfo(Path.Combine(dir, "TLFILE.TLDAT")), false);
    }

    public Stream Open(TLDataHeaderEntry e)
    {
        var src = (TLFileDataSource)e.DataSource;
        if (_crypt == null || !_crypt.GetFileKey(src.Index, out var key)) return src.OpenRead();
        var buf = new byte[src.CompressedLength];
        using (var raw = src.OpenReadRaw()) raw.ReadExactly(buf);
        TLCrypt.Decrypt(buf, key);
        var ms = new MemoryStream(buf);
        return src.IsCompressed ? CompressionUtility.GetTlzcDecompressionStream(ms, leaveOpen: false) : ms;
    }

    /// <summary>Read a file by name (e.g. "TREASUREBOXDATA.DAT"); null if absent.</summary>
    public byte[]? Read(string name)
    {
        var ext = Path.GetExtension(name).TrimStart('.').ToUpperInvariant();
        uint hash = TLHash.HashToUInt32(name, TLHashOptions.IgnoreCase);
        var e = Header.Entries.FirstOrDefault(x => x.NameHash == hash && x.Extension.Equals(ext, StringComparison.OrdinalIgnoreCase));
        if (e == null) return null;
        using var s = Open(e);
        using var ms = new MemoryStream();
        s.CopyTo(ms);
        return ms.ToArray();
    }
}

/// <summary>Game data lookup honoring the PATCH archive override over BASE.</summary>
internal sealed class GameFiles
{
    readonly Archive _patch, _base;
    public GameFiles(string streamingAssets)
    {
        var g = Path.Combine(streamingAssets, "GAMEDATA");
        _patch = new Archive(Path.Combine(g, "PATCH", "GENERAL", "COMMON"));
        _base = new Archive(Path.Combine(g, "BASE", "CHUNK0", "GENERAL", "COMMON"));
    }
    public byte[] Read(string name) => _patch.Read(name) ?? _base.Read(name)
        ?? throw new FileNotFoundException($"{name} not found in game archives");
}
