using Praedra.Core.AI;
using Praedra.Core.Clock;
using Praedra.Core.Setup;
using Xunit;

namespace Praedra.Core.Tests.Setup;

// The screens' save header (godot GameBoot.ReadSave): a save from before the side rename still opens.
public class PlayerSaveHeaderTests
{
    [Theory]
    [InlineData("PRAEDRA-SAVE 1 Hand Hard", Side.Hand, AiLevel.Hard)]
    [InlineData("PRAEDRA-SAVE 1 Collective Easy", Side.Collective, AiLevel.Easy)]
    [InlineData("PRAEDRA-SAVE 1 Empire Hard", Side.Hand, AiLevel.Hard)]          // written before stage 3a
    [InlineData("REBELLION-SAVE 1 Alliance", Side.Collective, AiLevel.Medium)]   // before the Praedra names, no level
    public void HeadersRead(string line, Side player, AiLevel level) =>
        Assert.Equal((player, level), PlayerSaveHeader.Read(line));

    [Theory]
    [InlineData("PRAEDRA-SAVE 1 Nobody Hard")]
    [InlineData("SOMETHING 1 Hand")]
    [InlineData("PRAEDRA-SAVE 1")]
    public void OtherLinesAreNotHeaders(string line) => Assert.Null(PlayerSaveHeader.Read(line));

    [Fact]
    public void WriteThenRead() =>
        Assert.Equal((Side.Collective, AiLevel.Hard), PlayerSaveHeader.Read(PlayerSaveHeader.Write(Side.Collective, AiLevel.Hard)));
}
