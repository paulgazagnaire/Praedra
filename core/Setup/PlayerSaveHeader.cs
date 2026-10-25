using Praedra.Core.AI;
using Praedra.Core.Clock;
using Praedra.Core.Data;

namespace Praedra.Core.Setup;

/// <summary>
/// The first line of the screens' save file: "PRAEDRA-SAVE 1 &lt;side&gt; &lt;level&gt;" (who the player is and the opponent's level; the core's save
/// follows on the next lines). A save from before the Praedra names says REBELLION-SAVE and an old side word (Empire, Alliance); both still read.
/// </summary>
public static class PlayerSaveHeader
{
    public static string Write(Side player, AiLevel level) => $"PRAEDRA-SAVE 1 {player} {level}";

    /// <summary>The player and opponent level of a header line, or null when it is not one. Older headers without a level: Medium.</summary>
    public static (Side Player, AiLevel Level)? Read(string line, NameMigrations? names = null)
    {
        var parts = line.Trim().Split(' ');
        if (parts.Length is not (3 or 4) || parts[0] is not ("PRAEDRA-SAVE" or "REBELLION-SAVE")) return null;
        // the side word goes through the word table, so "Empire" reads as Hand and "Alliance" as Collective
        string side = (names ?? GameData.Default.Names).CurrentIdentifier(parts[2]);
        if (!Enum.TryParse(side, out Side player) || !Enum.IsDefined(player)) return null;
        var level = parts.Length == 4 && Enum.TryParse(parts[3], out AiLevel l) && Enum.IsDefined(l) ? l : AiLevel.Medium;
        return (player, level);
    }
}
