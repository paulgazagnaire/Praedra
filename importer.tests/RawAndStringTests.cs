using static Rebellion.Importer.Tests.Bytes;

namespace Rebellion.Importer.Tests;

/// <summary>The TEXTSTRA string table, the raw CSV headers and the CSV text. rule: B1-own-importer §Work 1, 2; 02-gdata-formats §3, §5.</summary>
public class RawAndStringTests
{
    // B1-own-importer Tests (synthetic): one RT_STRING block, id = (block - 1) * 16 + position, empty strings skipped
    [Fact]
    public void StringBlockDecodes()
    {
        var block = StringBlock((0, "Yes"), (1, "No"), (11, "\nSector"));
        var s = StringTable.DecodeBlock("T.DLL", 372, block).ToList();
        Assert.Equal(new[] { 5936, 5937, 5947 }, s.Select(kv => kv.Key));
        Assert.Equal("\nSector", s[2].Value);
    }

    // B1-own-importer Tests (failure): a string block that ends early
    [Fact]
    public void ShortStringBlockFails()
    {
        var block = StringBlock((0, "Yes"));
        Assert.Throws<ImportException>(() => StringTable.DecodeBlock("T.DLL", 1, block.AsSpan(0, block.Length - 2)).ToList());
    }

    // B1-own-importer Tests (synthetic PE): the RT_STRING resources of a small DLL, read with PEReader and the resource directory walk
    [Fact]
    public void StringTableOfASyntheticDll()
    {
        var dll = ResourceDll((629, StringBlock((0, "Mon Calamari Cruiser"), (1, "Bulk Cruiser"))), (305, StringBlock((0, "Yes"), (15, "Last"))));
        var s = StringTable.Read("TEXTSTRA.DLL", dll);
        Assert.Equal(new[] { 4864, 4879, 10048, 10049 }, s.Keys);
        Assert.Equal("Mon Calamari Cruiser", s[10048]);
        Assert.Equal("Last", s[4879]);
    }

    // B1-own-importer Tests (failure): a DLL without string resources
    [Fact]
    public void DllWithoutStringsFails()
    {
        var dll = ResourceDllOfType(10, (1, StringBlock((0, "data")))); // RT_RCDATA only
        Assert.Contains("RT_STRING", Assert.Throws<ImportException>(() => StringTable.Read("X.DLL", dll)).Message);
    }

    // B1-own-importer Work 2: headers from sheet section 5 (CAPSHPSD 50 words), table layouts, w0..wN for unlisted files
    [Fact]
    public void RawHeaders()
    {
        var cap = Layouts.Header("CAPSHPSD", null, 50);
        Assert.Equal(("id", "word5", "cost", "hull", "f49"), (cap[0], cap[5], cap[8], cap[38], cap[49]));
        Assert.Equal(new[] { "w0", "w1", "w2" }, Layouts.Header("SECTORXX", null, 3));
        Assert.Equal(new[] { "index", "f1", "threshold", "value" }, Layouts.Header("FOILTB", EntryKinds.Int, 4));
        Assert.Equal("v7", Layouts.Header("GNPRTB", EntryKinds.GeneralParam, 11)[10]);
        Assert.Equal("v15", Layouts.Header("SDPRTB", EntryKinds.SideParam, 19)[18]);
        Assert.Equal(new[] { "index", "f1", "key", "f3", "f4", "n", "object1", "object2" }, Layouts.Header("CMUNAFTB", EntryKinds.SeedFamily, 8));
        Assert.Equal(new[] { "index", "f1", "key", "object" }, Layouts.Header("SYFCCRTB", EntryKinds.Seed, 4));
        Assert.Contains("the sheet names 50", Assert.Throws<ImportException>(() => Layouts.Header("CAPSHPSD", null, 49)).Message);
    }

    // B1-own-importer Work 2: a raw table keeps every word, word 5 packed, seed family rows ragged under the widest header
    [Fact]
    public void RawTableFromDat()
    {
        var t = RawStage.FromDat("CMUNAFTB", Tb(EntryKinds.SeedFamily, 2, new long[] { 1, 1, 1, 1, 1, 1, 1, 0, 335544389 },
            new long[] { 2, 1, 2, 1, 1, 2, 1, 0, 335544390, 1, 0, 268435457 }));
        Assert.Equal(8, t.Header.Length);
        Assert.Equal(new[] { "1", "1", "1", "1", "1", "1", "335544389" }, t.Rows[0]);
        Assert.Equal("index,f1,key,f3,f4,n,object1,object2\n1,1,1,1,1,1,335544389\n2,1,2,1,1,2,335544390,268435457\n", Csv.Write(t.Header, t.Rows));
    }

    // B1-own-importer Work 2: CSV text as the reference reader writes it (quotes only around a comma, a quote or a line break)
    [Fact]
    public void CsvQuotingRoundTrips()
    {
        var text = Csv.Write(new[] { "id", "text" }, new[] { new[] { "5947", "\nSector" }, new[] { "1", "a,b" }, new[] { "2", "say \"hi\"" }, new[] { "3", "plain" } });
        Assert.Equal("id,text\n5947,\"\nSector\"\n1,\"a,b\"\n2,\"say \"\"hi\"\"\"\n3,plain\n", text);
        var back = Csv.Parse("t", text);
        Assert.Equal(new[] { "\nSector", "a,b", "say \"hi\"", "plain" }, back.Rows.Select(r => r[1]));
    }
}
