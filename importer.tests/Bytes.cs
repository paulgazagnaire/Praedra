using System.Text;

namespace Praedra.Importer.Tests;

/// <summary>Builders of synthetic game files (little-endian 32-bit words), for the importer's unit tests.</summary>
internal static class Bytes
{
    public static byte[] Words(params long[] w)
    {
        var b = new byte[w.Length * 4];
        for (int i = 0; i < w.Length; i++) BitConverter.GetBytes((uint)(w[i] & 0xFFFFFFFF)).CopyTo(b, i * 4);
        return b;
    }

    public static byte[] Cat(params byte[][] parts) => parts.SelectMany(p => p).ToArray();

    /// <summary>An SD file: version 1, count, family_lo, family_hi, then the records.</summary>
    public static byte[] Sd(int familyLo, int familyHi, params long[][] records) =>
        Cat(Words(1, records.Length, familyLo, familyHi), Cat(records.Select(r => Words(r)).ToArray()));

    /// <summary>A TB file: version 1, count, kind length, kind text, then the entries' words.</summary>
    public static byte[] Tb(string kind, int count, params long[][] entries) =>
        Cat(Words(1, count, kind.Length), Encoding.ASCII.GetBytes(kind), Cat(entries.Select(e => Words(e)).ToArray()));

    /// <summary>One RT_STRING block: 16 counted UTF-16LE strings (null = empty).</summary>
    public static byte[] StringBlock(params (int Position, string Text)[] strings)
    {
        var ms = new MemoryStream();
        for (int i = 0; i < 16; i++)
        {
            string s = strings.FirstOrDefault(x => x.Position == i).Text ?? "";
            ms.Write(BitConverter.GetBytes((ushort)s.Length));
            ms.Write(Encoding.Unicode.GetBytes(s));
        }
        return ms.ToArray();
    }

    /// <summary>
    /// A minimal PE32 DLL with one section (.rsrc at RVA 0x1000, file offset 0x200) holding a resource tree: RT_STRING, then the given block
    /// ids, each with one language (0x409) entry pointing at its block bytes.
    /// </summary>
    public static byte[] ResourceDll(params (int BlockId, byte[] Data)[] blocks) => ResourceDllOfType(6, blocks);

    /// <summary>As <see cref="ResourceDll"/>, with another resource type at the top of the tree.</summary>
    public static byte[] ResourceDllOfType(uint typeId, params (int BlockId, byte[] Data)[] blocks)
    {
        const int sectionRva = 0x1000, fileAlign = 0x200;
        int n = blocks.Length;
        // layout inside the section: root dir, type dir, n name dirs, n data entries, then the data
        int typeOff = 16 + 8, nameOff = typeOff + 16 + 8 * n;
        int dataEntryOff = nameOff + n * (16 + 8);
        int dataOff = dataEntryOff + n * 16;
        var rs = new MemoryStream();
        void Dir(int ids) { rs.Write(new byte[12]); rs.Write(BitConverter.GetBytes((ushort)0)); rs.Write(BitConverter.GetBytes((ushort)ids)); }
        void Entry(uint id, uint off) { rs.Write(BitConverter.GetBytes(id)); rs.Write(BitConverter.GetBytes(off)); }
        Dir(1); Entry(typeId, 0x8000_0000u | (uint)typeOff);
        Dir(n); for (int i = 0; i < n; i++) Entry((uint)blocks[i].BlockId, 0x8000_0000u | (uint)(nameOff + i * 24));
        for (int i = 0; i < n; i++) { Dir(1); Entry(0x409, (uint)(dataEntryOff + i * 16)); }
        int p = dataOff;
        for (int i = 0; i < n; i++)
        {
            rs.Write(BitConverter.GetBytes((uint)(sectionRva + p))); rs.Write(BitConverter.GetBytes((uint)blocks[i].Data.Length));
            rs.Write(new byte[8]);
            p += (blocks[i].Data.Length + 3) & ~3;
        }
        foreach (var (_, data) in blocks) { rs.Write(data); rs.Write(new byte[((data.Length + 3) & ~3) - data.Length]); }
        var rsrc = rs.ToArray();
        int rawSize = (rsrc.Length + fileAlign - 1) / fileAlign * fileAlign;

        var f = new MemoryStream();
        var w = new BinaryWriter(f);
        // DOS header: "MZ", e_lfanew = 0x40
        w.Write((ushort)0x5A4D); w.Write(new byte[0x3C - 2]); w.Write(0x40);
        // PE signature and COFF header: i386, one section, optional header 224 bytes, DLL
        w.Write(0x00004550); w.Write((ushort)0x14C); w.Write((ushort)1); w.Write(0); w.Write(0); w.Write(0); w.Write((ushort)0xE0); w.Write((ushort)0x2102);
        // optional header PE32, standard fields
        w.Write((ushort)0x10B); w.Write((byte)6); w.Write((byte)0); w.Write(0); w.Write(rawSize); w.Write(0); w.Write(0); w.Write(0); w.Write(0);
        // Windows fields
        w.Write(0x1000_0000); w.Write(0x1000); w.Write(fileAlign); w.Write((ushort)4); w.Write((ushort)0); w.Write((ushort)0); w.Write((ushort)0);
        w.Write((ushort)4); w.Write((ushort)0); w.Write(0); w.Write(sectionRva + ((rsrc.Length + 0xFFF) & ~0xFFF)); w.Write(fileAlign); w.Write(0);
        w.Write((ushort)2); w.Write((ushort)0); w.Write(0x100000); w.Write(0x1000); w.Write(0x100000); w.Write(0x1000); w.Write(0); w.Write(16);
        // data directories: index 2 = resources
        for (int i = 0; i < 16; i++) { if (i == 2) { w.Write(sectionRva); w.Write(rsrc.Length); } else { w.Write(0); w.Write(0); } }
        // section header .rsrc
        w.Write(Encoding.ASCII.GetBytes(".rsrc\0\0\0")); w.Write(rsrc.Length); w.Write(sectionRva); w.Write(rawSize); w.Write(fileAlign);
        w.Write(0); w.Write(0); w.Write((ushort)0); w.Write((ushort)0); w.Write(0x40000040);
        w.Write(new byte[fileAlign - (int)f.Length]);
        w.Write(rsrc); w.Write(new byte[rawSize - rsrc.Length]);
        return f.ToArray();
    }
}
