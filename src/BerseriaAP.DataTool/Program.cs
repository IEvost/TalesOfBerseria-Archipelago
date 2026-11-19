using System.Text;
using System.Text.RegularExpressions;
using TLTool;

// Berseria data tool: selective, read-only extraction of encrypted TLDAT archives.
// Usage:
//   bdt extract <archiveDir> <outDir> [--ext DAT,SCPACKR] [--names <file>] [--dll <TLSampleViewerTO12.dll>]
//   bdt list    <archiveDir>
return args.Length > 0 ? args[0] switch
{
    "extract" => Extract(args[1..]),
    "list" => List(args[1..]),
    "gen" => BerseriaAP.DataTool.Gen.Run(args[1..]),
    _ => Usage(),
} : Usage();

static int Usage()
{
    Console.Error.WriteLine("bdt extract <archiveDir> <outDir> [--ext A,B] [--names file] [--dll file]\nbdt list <archiveDir>");
    return 1;
}

static (TLDataHeader header, TLDataEncryptHeader? crypt) Open(string dir)
{
    var hdb = File.ReadAllBytes(Path.Combine(dir, "FILEHEADER.TOFHDB"));
    TLDataEncryptHeader? crypt = null;
    var hda = Path.Combine(dir, "FILEHEADER.TOFHDA");
    if (File.Exists(hda))
    {
        crypt = new TLDataEncryptHeader(File.ReadAllBytes(hda));
        TLCrypt.Decrypt(hdb, crypt.GetHeaderKey());
    }
    var header = new TLDataHeader();
    using var ms = new MemoryStream(hdb);
    header.ReadFrom(new BinaryStream(ms, false), new FileInfo(Path.Combine(dir, "TLFILE.TLDAT")), false);
    return (header, crypt);
}

static int List(string[] a)
{
    var (header, _) = Open(a[0]);
    foreach (var g in header.Entries.GroupBy(e => e.Extension).OrderByDescending(g => g.Count()))
        Console.WriteLine($"{g.Key,-12} {g.Count(),7}  {g.Sum(e => e.DataSource.Length) / 1048576.0,10:N1} MB");
    return 0;
}

static string? Opt(string[] a, string name) { int i = Array.IndexOf(a, name); return i >= 0 && i + 1 < a.Length ? a[i + 1] : null; }

static int Extract(string[] a)
{
    string dir = a[0], outDir = a[1];
    var exts = (Opt(a, "--ext") ?? "DAT").ToUpperInvariant().Split(',', StringSplitOptions.RemoveEmptyEntries).ToHashSet();
    var (header, crypt) = Open(dir);

    // Candidate names: explicit list + every identifier-like string in the game DLL, combined with each extension.
    var bases = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
    if (Opt(a, "--names") is { } nf) foreach (var l in File.ReadAllLines(nf)) if (l.Trim().Length > 0) bases.Add(l.Trim());
    if (Opt(a, "--dll") is { } dll)
        foreach (Match m in Regex.Matches(Encoding.Latin1.GetString(File.ReadAllBytes(dll)), @"[A-Za-z0-9_\\/\.]{3,64}"))
        {
            var s = m.Value;
            bases.Add(s);
            bases.Add(Path.GetFileName(s.Replace('\\', '/')));
        }
    var names = new Dictionary<(uint, string), string>();
    foreach (var b in bases)
    {
        var withExt = Path.HasExtension(b) ? new[] { b } : Array.Empty<string>();
        foreach (var n in withExt.Concat(exts.Select(e => b + "." + e)))
        {
            var ext = Path.GetExtension(n).TrimStart('.').ToUpperInvariant();
            names.TryAdd((TLHash.HashToUInt32(n, TLHashOptions.IgnoreCase), ext), n.ToUpperInvariant());
        }
    }

    int count = 0, named = 0;
    var map = new StringBuilder();
    foreach (var e in header.Entries)
    {
        if (!exts.Contains(e.Extension.ToUpperInvariant())) continue;
        var src = (TLFileDataSource)e.DataSource;
        bool hasName = names.TryGetValue((e.NameHash, e.Extension.ToUpperInvariant()), out var name);
        name ??= $"${e.NameHash:X8}.{e.Extension}";
        if (hasName) named++;
        var path = Path.Combine(outDir, e.Extension, name);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        using (var s = OpenEntry(src, crypt)) using (var f = File.Create(path)) s.CopyTo(f);
        map.AppendLine($"{e.NameHash:X8}\t{e.Extension}\t{name}\t{src.Length}");
        count++;
    }
    File.WriteAllText(Path.Combine(outDir, "_index.tsv"), map.ToString());
    Console.WriteLine($"Extracted {count} files ({named} named) to {outDir}");
    return 0;
}

static Stream OpenEntry(TLFileDataSource src, TLDataEncryptHeader? crypt)
{
    if (crypt == null || !crypt.GetFileKey(src.Index, out var key)) return src.OpenRead();
    var buf = new byte[src.CompressedLength];
    using (var raw = src.OpenReadRaw()) raw.ReadExactly(buf);
    TLCrypt.Decrypt(buf, key);
    var ms = new MemoryStream(buf);
    return src.IsCompressed ? CompressionUtility.GetTlzcDecompressionStream(ms, leaveOpen: false) : ms;
}
