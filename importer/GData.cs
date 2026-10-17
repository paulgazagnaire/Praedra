using System.Buffers.Binary;
using System.Text;

namespace Rebellion.Importer;

/// <summary>The four fixed-size entry kinds and the variable one of the table files. rule: 02-gdata-formats §2.</summary>
public static class EntryKinds
{
    public const string Int = "IntTableEntry";
    public const string GeneralParam = "GeneralParamTableEntry";
    public const string SideParam = "SideParamTableEntry";
    public const string Seed = "SeedTableEntry";
    public const string SeedFamily = "SeedFamilyTableEntry";

    public static bool IsFixed(string kind) => kind is Int or GeneralParam or SideParam or Seed;

    /// <summary>Words per entry of a fixed kind: 16, 44, 76 and 16 bytes. rule: 02-gdata-formats §2 (entry layout table).</summary>
    public static int FixedWords(string kind) => kind switch
    {
        Int => 4, GeneralParam => 11, SideParam => 19, Seed => 4,
        _ => throw new ArgumentException($"'{kind}' is not a fixed entry kind."),
    };
}

/// <summary>
/// One decoded .DAT file: every word of every record in file order. For a static-data file <see cref="EntryKind"/> is null; for a
/// SeedFamilyTableEntry file each row is flat [index, f1, key, f3, f4, n, object1 .. objectN] (the two constant item words are checked, not kept).
/// rule: 02-gdata-formats §1, §2.
/// </summary>
public sealed record DatFile(string Name, int Version, int Count, string? EntryKind, int FamilyLo, int FamilyHi, IReadOnlyList<long[]> Rows);

/// <summary>Reader for the SD and TB formats of the format sheet. All numbers little-endian 32-bit. rule: 02-gdata-formats §1, §2.</summary>
public static class GData
{
    /// <summary>Bytes of one word. rule: 02-gdata-formats (all numbers are 32-bit).</summary>
    public const int WordBytes = 4;
    /// <summary>SD header: version, count, family_lo, family_hi. rule: 02-gdata-formats §1.</summary>
    public const int SdHeaderBytes = 16;
    /// <summary>TB header before the entry kind text: version, count, n. rule: 02-gdata-formats §2.</summary>
    public const int TbHeaderBytes = 12;
    /// <summary>SeedFamilyTableEntry head words: index, 1, key, 1, 1, n. rule: 02-gdata-formats §2.</summary>
    public const int SeedFamilyHeadWords = 6;
    /// <summary>Words of one seed item (1, 0, object). rule: 02-gdata-formats §2.</summary>
    public const int SeedItemWords = 3;
    /// <summary>Word of a seed family head that holds the item count n. rule: 02-gdata-formats §2.</summary>
    public const int SeedFamilyCountWord = 5;

    /// <summary>Read a static-data file (*SD.DAT). rule: 02-gdata-formats §1.</summary>
    public static DatFile ReadSd(string name, byte[] b)
    {
        if (b.Length < SdHeaderBytes) throw new ImportException($"{name}: {b.Length} bytes is shorter than the {SdHeaderBytes}-byte header.");
        int ver = I32(b, 0), cnt = I32(b, 4), lo = I32(b, 8), hi = I32(b, 12);
        if (cnt <= 0) throw new ImportException($"{name}: record count {cnt} is not positive.");
        int body = b.Length - SdHeaderBytes;
        if (body % cnt != 0) throw new ImportException($"{name}: {body} bytes after the header do not divide into {cnt} equal records.");
        int size = body / cnt;
        if (size % WordBytes != 0) throw new ImportException($"{name}: record size {size} is not a multiple of {WordBytes}.");
        var rows = new List<long[]>(cnt);
        for (int i = 0; i < cnt; i++) rows.Add(Words(b, SdHeaderBytes + i * size, size / WordBytes));
        return new DatFile(name, ver, cnt, null, lo, hi, rows);
    }

    /// <summary>Read a table file (*TB.DAT). rule: 02-gdata-formats §2.</summary>
    public static DatFile ReadTb(string name, byte[] b)
    {
        if (b.Length < TbHeaderBytes) throw new ImportException($"{name}: {b.Length} bytes is shorter than the {TbHeaderBytes}-byte header.");
        int ver = I32(b, 0), cnt = I32(b, 4), nl = I32(b, 8);
        if (cnt <= 0) throw new ImportException($"{name}: entry count {cnt} is not positive.");
        if (nl <= 0 || TbHeaderBytes + nl > b.Length) throw new ImportException($"{name}: entry kind length {nl} does not fit the file.");
        string kind = Encoding.ASCII.GetString(b, TbHeaderBytes, nl);
        int o = TbHeaderBytes + nl;
        List<long[]> rows;
        if (EntryKinds.IsFixed(kind))
        {
            int body = b.Length - o;
            if (body % cnt != 0) throw new ImportException($"{name}: {body} bytes of entries do not divide into {cnt} equal {kind} entries.");
            int size = body / cnt;
            if (size != EntryKinds.FixedWords(kind) * WordBytes)
                throw new ImportException($"{name}: {kind} entries of {size} bytes, the sheet says {EntryKinds.FixedWords(kind) * WordBytes}.");
            rows = new List<long[]>(cnt);
            for (int i = 0; i < cnt; i++) rows.Add(Words(b, o + i * size, size / WordBytes));
        }
        else if (kind == EntryKinds.SeedFamily) rows = ReadSeedFamily(name, b, o, cnt);
        else throw new ImportException($"{name}: unknown entry kind '{kind}'.");
        // rule: 02-gdata-formats §2 ("entry word 0 = index 1..count")
        for (int i = 0; i < rows.Count; i++)
            if (rows[i].Length == 0 || rows[i][0] != i + 1)
                throw new ImportException($"{name}: entry {i + 1} has index {(rows[i].Length == 0 ? "none" : rows[i][0].ToString())} (entries out of order).");
        return new DatFile(name, ver, cnt, kind, 0, 0, rows);
    }

    /// <summary>
    /// SeedFamilyTableEntry: per entry index, 1, key, 1, 1, n, then n items of (1, 0, object); object = family &lt;&lt; 24 | class id (unsigned).
    /// rule: 02-gdata-formats §2.
    /// </summary>
    private static List<long[]> ReadSeedFamily(string name, byte[] b, int o, int cnt)
    {
        var rows = new List<long[]>(cnt);
        for (int e = 0; e < cnt; e++)
        {
            if (o + SeedFamilyHeadWords * WordBytes > b.Length) throw new ImportException($"{name}: entry {e + 1} runs past the end of the file.");
            var head = Words(b, o, SeedFamilyHeadWords);
            o += SeedFamilyHeadWords * WordBytes;
            long n = head[SeedFamilyCountWord];
            if (n < 0 || o + n * SeedItemWords * WordBytes > b.Length) throw new ImportException($"{name}: entry {e + 1} has {n} items, which do not fit the file.");
            var row = new long[SeedFamilyHeadWords + n];
            Array.Copy(head, row, SeedFamilyHeadWords);
            for (int i = 0; i < n; i++)
            {
                int a = I32(b, o), z = I32(b, o + 4);
                uint obj = BinaryPrimitives.ReadUInt32LittleEndian(b.AsSpan(o + 8));
                if (a != 1 || z != 0) throw new ImportException($"{name}: seed item at byte {o}: expected (1, 0, object), got ({a}, {z}, {obj}).");
                row[SeedFamilyHeadWords + i] = obj;
                o += SeedItemWords * WordBytes;
            }
            rows.Add(row);
        }
        if (o != b.Length) throw new ImportException($"{name}: {b.Length - o} bytes left over after {cnt} entries.");
        return rows;
    }

    private static int I32(byte[] b, int o) => BinaryPrimitives.ReadInt32LittleEndian(b.AsSpan(o));

    private static long[] Words(byte[] b, int o, int n)
    {
        var w = new long[n];
        for (int i = 0; i < n; i++) w[i] = I32(b, o + i * WordBytes);
        return w;
    }
}
