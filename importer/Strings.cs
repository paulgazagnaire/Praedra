using System.Buffers.Binary;
using System.Reflection.PortableExecutable;
using System.Text;

namespace Rebellion.Importer;

/// <summary>
/// The RT_STRING table of a Windows resource DLL (TEXTSTRA.DLL): blocks of 16 counted UTF-16 strings, string id = (block id - 1) * 16 + position.
/// Read with <see cref="PEReader"/> and a walk of the resource directory (type, then name, then language). rule: 02-gdata-formats §3.
/// </summary>
public static class StringTable
{
    /// <summary>Resource type of string tables. rule: 02-gdata-formats §3 ("RT_STRING resources (type 6)").</summary>
    public const int RtString = 6;
    /// <summary>Strings per block. rule: 02-gdata-formats §3.</summary>
    public const int StringsPerBlock = 16;
    /// <summary>Sizes of IMAGE_RESOURCE_DIRECTORY, IMAGE_RESOURCE_DIRECTORY_ENTRY and IMAGE_RESOURCE_DATA_ENTRY: facts of the PE format.</summary>
    private const int DirectoryHeaderBytes = 16, DirectoryEntryBytes = 8, DataEntryBytes = 16;
    private const uint HighBit = 0x8000_0000;

    /// <summary>Every non-empty string of the DLL's RT_STRING blocks, by id.</summary>
    public static SortedDictionary<int, string> Read(string name, byte[] dll)
    {
        using var pe = new PEReader(new MemoryStream(dll, writable: false));
        var dir = pe.PEHeaders.PEHeader?.ResourceTableDirectory ?? default;
        if (dir.RelativeVirtualAddress == 0 || dir.Size == 0) throw new ImportException($"{name}: no resource table.");
        var rsrc = pe.GetSectionData(dir.RelativeVirtualAddress).GetContent(0, dir.Size).AsSpan().ToArray();
        var outp = new SortedDictionary<int, string>();
        bool found = false;
        foreach (var (typeId, typeOff, typeIsDir) in Entries(name, rsrc, 0))
        {
            if (typeId != RtString) continue;
            if (!typeIsDir) throw new ImportException($"{name}: RT_STRING entry is not a directory.");
            found = true;
            foreach (var (blockId, blockOff, blockIsDir) in Entries(name, rsrc, typeOff))
            {
                if (!blockIsDir) throw new ImportException($"{name}: string block {blockId} is not a directory.");
                foreach (var (_, langOff, langIsDir) in Entries(name, rsrc, blockOff))
                {
                    if (langIsDir) throw new ImportException($"{name}: string block {blockId} has a fourth directory level.");
                    if (langOff + DataEntryBytes > rsrc.Length) throw new ImportException($"{name}: resource data entry past the table.");
                    int rva = (int)U32(rsrc, langOff), size = (int)U32(rsrc, langOff + 4);
                    var data = pe.GetSectionData(rva);
                    if (data.Length < size) throw new ImportException($"{name}: string block {blockId} data runs past its section.");
                    foreach (var kv in DecodeBlock(name, blockId, data.GetContent(0, size).AsSpan())) outp[kv.Key] = kv.Value;
                }
            }
        }
        if (!found) throw new ImportException($"{name}: no RT_STRING resources.");
        return outp;
    }

    /// <summary>One string block: 16 strings, each a u16 length then that many UTF-16LE units; empty strings are skipped. rule: 02-gdata-formats §3.</summary>
    public static IEnumerable<KeyValuePair<int, string>> DecodeBlock(string name, int blockId, ReadOnlySpan<byte> d)
    {
        if (blockId < 1) throw new ImportException($"{name}: string block id {blockId} is below 1.");
        var list = new List<KeyValuePair<int, string>>();
        int p = 0;
        for (int i = 0; i < StringsPerBlock; i++)
        {
            if (p + 2 > d.Length) throw new ImportException($"{name}: string block {blockId} ends before string {i}.");
            int n = BinaryPrimitives.ReadUInt16LittleEndian(d[p..]);
            p += 2;
            if (p + n * 2 > d.Length) throw new ImportException($"{name}: string {i} of block {blockId} runs past the block.");
            if (n > 0) list.Add(new((blockId - 1) * StringsPerBlock + i, Encoding.Unicode.GetString(d.Slice(p, n * 2))));
            p += n * 2;
        }
        return list;
    }

    /// <summary>The entries of one resource directory, named entries first then id entries (file order). Offsets are from the table start.</summary>
    private static List<(int Id, int Offset, bool IsDir)> Entries(string name, byte[] r, int off)
    {
        if (off + DirectoryHeaderBytes > r.Length) throw new ImportException($"{name}: resource directory past the table.");
        int named = BinaryPrimitives.ReadUInt16LittleEndian(r.AsSpan(off + 12)), ids = BinaryPrimitives.ReadUInt16LittleEndian(r.AsSpan(off + 14));
        var list = new List<(int, int, bool)>();
        for (int i = 0; i < named + ids; i++)
        {
            int e = off + DirectoryHeaderBytes + i * DirectoryEntryBytes;
            if (e + DirectoryEntryBytes > r.Length) throw new ImportException($"{name}: resource directory entry past the table.");
            uint nameOrId = U32(r, e), target = U32(r, e + 4);
            int id = (nameOrId & HighBit) != 0 ? -1 : (int)nameOrId; // a named entry is never RT_STRING nor a block id
            list.Add((id, (int)(target & ~HighBit), (target & HighBit) != 0));
        }
        return list;
    }

    private static uint U32(byte[] b, int o) => BinaryPrimitives.ReadUInt32LittleEndian(b.AsSpan(o));
}
