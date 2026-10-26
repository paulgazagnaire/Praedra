using System.Globalization;
using Praedra.Core.Clock;
using Praedra.Core.Control;
using Praedra.Core.Fog;
using Praedra.Core.Setup;

// Endgame what-ifs for the tourney (endgame-stall proposals, Paul 2026-10-26): reads one game day by day and notes the first day each
// candidate end rule would have ended it. Changes nothing in the game; the candidates are not rules (yet).
internal sealed class EndgameWhatIf
{
    /// <summary>One candidate end rule: who would win today (null = nobody), and for how many days running that must hold.</summary>
    private sealed record Rule(string Name, string Key, int Hold, Func<Snapshot, Side?> Leader);
    private readonly record struct Snapshot(int Hand, int Collective, bool HearthGone, bool CharterTaken);

    private static Side? Share(Snapshot s, int pct, bool needCapital)
    {
        int both = Math.Max(1, s.Hand + s.Collective);
        if (s.Hand * 100 >= pct * both && (!needCapital || s.HearthGone)) return Side.Hand;
        if (s.Collective * 100 >= pct * both && (!needCapital || s.CharterTaken)) return Side.Collective;
        return null;
    }

    private static readonly Rule[] Rules =
    {
        new("Hearth destroyed = Hand wins", "hearth", 0, s => s.HearthGone ? Side.Hand : null),
        new("Hearth gone + Collective <= 25 systems", "remnant25", 0, s => s.HearthGone && s.Collective <= 25 ? Side.Hand : null),
        new("capital gone + 70% of held, 180 days", "cap70", 180, s => Share(s, 70, true)),
        new("capital gone + 80% of held, 180 days", "cap80", 180, s => Share(s, 80, true)),
        new("capital gone + 90% of held, 90 days", "cap90", 90, s => Share(s, 90, true)),
        new("90% of held systems, 365 days", "dom90", 365, s => Share(s, 90, false)),
    };

    private int _scanned;
    private readonly Side?[] _streakSide = new Side?[Rules.Length];
    private readonly int[] _streakStart = new int[Rules.Length];

    public int? HqGoneDay { get; private set; }
    public int? CharterLostDay { get; private set; }
    public (Side Side, int Day)?[] Fired { get; } = new (Side, int)?[Rules.Length];

    public void Observe(GameHost host)
    {
        int day = host.Game.Day;
        for (var evs = host.Game.Events; _scanned < evs.Count; _scanned++)
            switch (evs[_scanned])
            {
                case HqDestroyed hd when HqGoneDay == null: HqGoneDay = hd.Day; break;
                case SystemCaptured sc when CharterLostDay == null && sc.System == ControlModule.CapitalSystem && sc.From == Side.Hand: CharterLostDay = sc.Day; break;
            }
        int h = 0, c = 0;
        foreach (var kv in host.Game.State.SystemOwners)
            if (kv.Value == Side.Hand) h++; else if (kv.Value == Side.Collective) c++;
        bool charterTaken = !host.Game.State.SystemOwners.TryGetValue(ControlModule.CapitalSystem, out var ch) || ch != Side.Hand;
        var snap = new Snapshot(h, c, HqGoneDay != null, charterTaken);
        for (int i = 0; i < Rules.Length; i++)
        {
            var leader = Rules[i].Leader(snap);
            if (leader != _streakSide[i]) { _streakSide[i] = leader; _streakStart[i] = day; }
            if (Fired[i] == null && leader is Side l && day - _streakStart[i] >= Rules[i].Hold) Fired[i] = (l, day);
        }
    }

    public static string CsvHeader => "hq_gone_day,charter_lost_day," + string.Join(",", Rules.Select(r => $"{r.Key}_side,{r.Key}_day"));

    public string Csv() => string.Join(",", new[] { Str(HqGoneDay), Str(CharterLostDay) }
        .Concat(Fired.SelectMany(d => new[] { d?.Side.ToString() ?? "", Str(d?.Day) })));

    private static string Str(int? v) => v?.ToString(CultureInfo.InvariantCulture) ?? "";

    /// <summary>Per candidate: how many games it would have ended before they ended (or before the cut-off), and whether it picks the real winner.</summary>
    public static void Print(IReadOnlyList<(Side? Winner, int Day, EndgameWhatIf W)> games)
    {
        Console.WriteLine();
        int open = games.Count(g => g.Winner == null);
        Console.WriteLine($"Endgame what-ifs ({games.Count} games, {open} without a winner). Hearth destroyed in {games.Count(g => g.W.HqGoneDay != null)}, " +
                          $"Charter lost by the Hand in {games.Count(g => g.W.CharterLostDay != null)} ({games.Count(g => g.W.CharterLostDay != null && g.Winner == null)} of them without a winner).");
        void Line(string name, Func<EndgameWhatIf, (Side Side, int Day)?> rule)
        {
            var fired = games.Select(g => (g.Winner, g.Day, F: rule(g.W))).Where(g => g.F is { } f && (g.Winner == null || f.Day < g.Day)).ToList();
            int ends = fired.Count(g => g.Winner == null), early = fired.Count(g => g.Winner != null);
            int same = fired.Count(g => g.Winner != null && g.Winner == g.F!.Value.Side), flipped = early - same;
            var days = fired.Where(g => g.Winner == null).Select(g => g.F!.Value.Day).OrderBy(d => d).ToList();
            Console.WriteLine($"  {name,-40} ends {ends,2} of {open} open games (median day {(days.Count == 0 ? "-" : days[days.Count / 2])}; " +
                              $"Hand {fired.Count(g => g.Winner == null && g.F!.Value.Side == Side.Hand)}, Collective {fired.Count(g => g.Winner == null && g.F!.Value.Side == Side.Collective)}); " +
                              $"ends {early} won games earlier ({flipped} with the other winner)");
        }
        for (int i = 0; i < Rules.Length; i++)
        {
            int k = i;
            Line(Rules[i].Name, w => w.Fired[k]);
        }
    }
}
