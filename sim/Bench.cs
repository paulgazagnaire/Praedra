using System.Diagnostics;
using System.Globalization;
using Praedra.Core.AI;
using Praedra.Core.Clock;
using Praedra.Core.Setup;

// Speed bench (QA senior QS-1): plays AI-vs-AI games one after another and prints each game's wall time next to its end state hash, so a
// speed change can show it changed no result (same hashes) and how much time it saved. Reads state only; draws no random numbers itself. The
// wall clock is the runner's own, never the core's.
internal static class Bench
{
    public const string Usage = """
          dotnet run --project sim -c Release -- bench [--seeds 1-8] [--days 1500] [--levels mm,hh]
        bench plays each seed at each level pair (Hand level then Collective level: e easy, m medium, h hard; fog on, MedA start) to --days or a
        win, one game at a time, and prints per game the wall time, the end day, the random draws and the state hash, then the total time.
        """;

    public static int Run(string[] a)
    {
        var seeds = Enumerable.Range(1, 8).ToList();
        int days = 1500; string levels = "mm,hh";
        for (int i = 0; i < a.Length; i++)
        {
            string key = a[i];
            string Next() => ++i < a.Length ? a[i] : throw new ArgumentException($"{key} needs a value.");
            switch (key)
            {
                case "--seeds": seeds = Tourney.ParseSeeds(Next()); break;
                case "--days": days = int.TryParse(Next(), out var d) && d > 0 ? d : throw new ArgumentException("--days must be a positive integer."); break;
                case "--levels": levels = Next(); break;
                default: throw new ArgumentException($"Unknown option '{key}'.");
            }
        }
        var pairs = levels.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                          .Select(p => p.Length == 2 ? (Hand: Level(p[0]), Collective: Level(p[1])) : throw new ArgumentException($"--levels: '{p}' is not two letters (e, m, h)."))
                          .ToList();
        NewGame.Create(new NewGameOptions { Seed = seeds[0], Preset = StartPreset.MedA }); // loads the shared data before the first timed game
        double total = 0;
        foreach (var (hand, collective) in pairs)
            foreach (int seed in seeds)
            {
                var clock = Stopwatch.StartNew();
                var host = NewGame.Create(new NewGameOptions { Seed = seed, Preset = StartPreset.MedA });
                var match = new AiMatch(host, AiLevels.For(hand), AiLevels.For(collective));
                match.RunTo(days);
                clock.Stop();
                total += clock.Elapsed.TotalSeconds;
                Console.WriteLine(string.Create(CultureInfo.InvariantCulture,
                    $"{hand,-6} {collective,-6} seed {seed,2}: {clock.Elapsed.TotalSeconds,7:F2} s  day {host.Game.Day,4}  draws {host.Game.Rng.Draws,9}  orders {match.TotalSent,6}  hash {host.StateHash()}"));
            }
        Console.WriteLine(string.Create(CultureInfo.InvariantCulture, $"Total: {total:F2} s"));
        return 0;
    }

    private static AiLevel Level(char c) => c switch
    {
        'e' => AiLevel.Easy, 'm' => AiLevel.Medium, 'h' => AiLevel.Hard,
        _ => throw new ArgumentException($"--levels: '{c}' is not e, m or h."),
    };
}
