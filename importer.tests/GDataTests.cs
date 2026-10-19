using static Praedra.Importer.Tests.Bytes;

namespace Praedra.Importer.Tests;

/// <summary>The SD and TB readers on synthetic bytes: each entry kind and each failure. rule: B1-own-importer §Tests; 02-gdata-formats §1, §2.</summary>
public class GDataTests
{
    // B1-own-importer Tests (synthetic): static-data file, header and equal records
    [Fact]
    public void SdFileReadsEveryWordOfEveryRecord()
    {
        var d = GData.ReadSd("X.DAT", Sd(20, 28, new long[] { 64, 1, 40, 41, 20, 141120 }, new long[] { 65, 1, 40, 41, 21, -5 }));
        Assert.Equal((1, 2, 20, 28, (string?)null), (d.Version, d.Count, d.FamilyLo, d.FamilyHi, d.EntryKind));
        Assert.Equal(new long[] { 64, 1, 40, 41, 20, 141120 }, d.Rows[0]);
        Assert.Equal(-5, d.Rows[1][5]);
    }

    // B1-own-importer Tests (failure): an SD size that does not divide into equal records
    [Fact]
    public void SdSizeThatDoesNotDivideFails()
    {
        var b = Cat(Sd(0, 1, new long[] { 1, 1 }, new long[] { 2, 1 }), new byte[] { 0 });
        var ex = Assert.Throws<ImportException>(() => GData.ReadSd("BAD.DAT", b));
        Assert.Contains("BAD.DAT", ex.Message);
        Assert.Contains("equal records", ex.Message);
    }

    // B1-own-importer Tests (failure): an SD record size that is not whole words
    [Fact]
    public void SdRecordNotWholeWordsFails()
    {
        var b = Cat(Words(1, 2, 0, 1), new byte[12]); // two records of 6 bytes
        Assert.Contains("multiple of 4", Assert.Throws<ImportException>(() => GData.ReadSd("BAD.DAT", b)).Message);
    }

    // B1-own-importer Tests (failure): an SD or TB count of 0
    [Fact]
    public void ZeroCountFails()
    {
        Assert.Throws<ImportException>(() => GData.ReadSd("Z.DAT", Words(1, 0, 0, 1)));
        Assert.Throws<ImportException>(() => GData.ReadTb("Z.DAT", Tb(EntryKinds.Int, 0)));
    }

    // B1-own-importer Tests (synthetic): IntTableEntry (index, 1, threshold, value)
    [Fact]
    public void IntTableEntries()
    {
        var d = GData.ReadTb("T.DAT", Tb(EntryKinds.Int, 2, new long[] { 1, 1, -20, 10 }, new long[] { 2, 1, 100, 99 }));
        Assert.Equal(EntryKinds.Int, d.EntryKind);
        Assert.Equal(new long[] { 1, 1, -20, 10 }, d.Rows[0]);
        Assert.Equal(new long[] { 2, 1, 100, 99 }, d.Rows[1]);
    }

    // B1-own-importer Tests (synthetic): GeneralParamTableEntry (index, 1, key, 8 values)
    [Fact]
    public void GeneralParamEntries()
    {
        var d = GData.ReadTb("G.DAT", Tb(EntryKinds.GeneralParam, 1, new long[] { 1, 1, 512, 5, 5, 5, 5, 5, 5, 5, 5 }));
        Assert.Equal(11, d.Rows[0].Length);
        Assert.Equal(512, d.Rows[0][2]);
    }

    // B1-own-importer Tests (synthetic): SideParamTableEntry (index, 1, key, 16 values)
    [Fact]
    public void SideParamEntries()
    {
        var e = new long[] { 1, 1, 5128 }.Concat(Enumerable.Range(0, 16).Select(i => (long)(i % 2 == 0 ? -2 : -1))).ToArray();
        var d = GData.ReadTb("S.DAT", Tb(EntryKinds.SideParam, 1, e));
        Assert.Equal(e, d.Rows[0]);
    }

    // B1-own-importer Tests (synthetic): SeedTableEntry (index, 1, key, object)
    [Fact]
    public void SeedEntries()
    {
        var d = GData.ReadTb("S.DAT", Tb(EntryKinds.Seed, 2, new long[] { 1, 1, 0, 0 }, new long[] { 2, 1, 36, 754974722 }));
        Assert.Equal(754974722, d.Rows[1][3]);
    }

    // B1-own-importer Tests (synthetic): SeedFamilyTableEntry, variable length; items (1, 0, object) flattened to their objects
    [Fact]
    public void SeedFamilyEntries()
    {
        var b = Tb(EntryKinds.SeedFamily, 2,
            new long[] { 1, 1, 1, 1, 1, 1, 1, 0, 335544389 },
            new long[] { 2, 1, 2, 1, 1, 2, 1, 0, 0, 1, 0, 0xFC000001 });
        var d = GData.ReadTb("C.DAT", b);
        Assert.Equal(new long[] { 1, 1, 1, 1, 1, 1, 335544389 }, d.Rows[0]);
        Assert.Equal(new long[] { 2, 1, 2, 1, 1, 2, 0, 0xFC000001 }, d.Rows[1]); // objects are unsigned
    }

    // B1-own-importer Tests (failure): a seed item that is not (1, 0, object)
    [Fact]
    public void SeedItemNotOneZeroFails()
    {
        var b = Tb(EntryKinds.SeedFamily, 1, new long[] { 1, 1, 1, 1, 1, 1, 1, 7, 335544389 });
        var ex = Assert.Throws<ImportException>(() => GData.ReadTb("CMUNXXTB.DAT", b));
        Assert.Contains("CMUNXXTB.DAT", ex.Message);
        Assert.Contains("(1, 0, object)", ex.Message);
    }

    // B1-own-importer Tests (failure): bytes left over after the last seed family entry
    [Fact]
    public void BytesLeftOverFails()
    {
        var b = Cat(Tb(EntryKinds.SeedFamily, 1, new long[] { 1, 1, 1, 1, 1, 0 }), Words(9));
        var ex = Assert.Throws<ImportException>(() => GData.ReadTb("L.DAT", b));
        Assert.Contains("4 bytes left over", ex.Message);
    }

    // B1-own-importer Tests (failure): a seed family entry whose items run past the file
    [Fact]
    public void SeedFamilyPastTheEndFails()
    {
        var b = Tb(EntryKinds.SeedFamily, 1, new long[] { 1, 1, 1, 1, 1, 3, 1, 0, 5 });
        Assert.Contains("do not fit", Assert.Throws<ImportException>(() => GData.ReadTb("P.DAT", b)).Message);
    }

    // B1-own-importer Tests (failure): fixed-size entries that do not divide (bytes left over)
    [Fact]
    public void FixedEntriesThatDoNotDivideFail()
    {
        var b = Cat(Tb(EntryKinds.Int, 2, new long[] { 1, 1, 0, 1 }, new long[] { 2, 1, 5, 6 }), new byte[] { 0 });
        var ex = Assert.Throws<ImportException>(() => GData.ReadTb("U.DAT", b));
        Assert.Contains("U.DAT", ex.Message);
        Assert.Contains("do not divide", ex.Message);
    }

    // B1-own-importer Tests (failure): fixed entries of the wrong size for their kind
    [Fact]
    public void WrongEntrySizeFails()
    {
        var b = Tb(EntryKinds.Int, 1, new long[] { 1, 1, 0, 1, 7 });
        Assert.Contains("the sheet says 16", Assert.Throws<ImportException>(() => GData.ReadTb("W.DAT", b)).Message);
    }

    // B1-own-importer Tests (failure): an entry index out of order
    [Fact]
    public void EntryIndexOutOfOrderFails()
    {
        var b = Tb(EntryKinds.Int, 2, new long[] { 2, 1, 0, 1 }, new long[] { 1, 1, 5, 6 });
        var ex = Assert.Throws<ImportException>(() => GData.ReadTb("O.DAT", b));
        Assert.Contains("O.DAT", ex.Message);
        Assert.Contains("out of order", ex.Message);
        var sf = Tb(EntryKinds.SeedFamily, 2, new long[] { 1, 1, 1, 1, 1, 0 }, new long[] { 3, 1, 1, 1, 1, 0 });
        Assert.Contains("out of order", Assert.Throws<ImportException>(() => GData.ReadTb("O.DAT", sf)).Message);
    }

    // B1-own-importer Tests (failure): an unknown entry kind, and a kind other than the sheet's for that file
    [Fact]
    public void UnknownOrUnexpectedEntryKindFails()
    {
        Assert.Contains("unknown entry kind", Assert.Throws<ImportException>(() => GData.ReadTb("K.DAT", Tb("FooEntry", 1, new long[] { 1, 1 }))).Message);
        var gn = Tb(EntryKinds.Int, 1, new long[] { 1, 1, 0, 1 });
        Assert.Contains("the sheet says 'GeneralParamTableEntry'", Assert.Throws<ImportException>(() => RawStage.FromDat("GNPRTB", gn)).Message);
    }
}
