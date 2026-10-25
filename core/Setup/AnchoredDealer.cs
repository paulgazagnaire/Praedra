using Praedra.Core.Clock;
using Praedra.Core.Map;

namespace Praedra.Core.Setup;

/// <summary>
/// How a new game's day-one owners are dealt. rule: m5-setup-and-victory §P1 Deal (DealMode).
/// Anchored is built from the anchors file and the game's own generator; File keeps the five fixed day-one CSVs plus fixed-owners.csv (the build before 2026-10-16).
/// The sheet also specifies Classic, Blocs and Scenario; they are not built.
/// </summary>
public enum DealMode { Anchored, File }

/// <summary>
/// Numbers and switches of the Anchored deal. Every default carries its source tag. rule: m5-setup-and-victory §Mode Anchored, §P1 HQ pick, §P2
/// </summary>
public sealed record DealParams
{
    /// <summary>The HQ is at least this many map units (straight line) from Charter. Proposal, m5-setup-and-victory §P1 HQ pick (HqMinDistance; setup-victory/10 v5 banner, replaces "5 lane hops" and "2 lanes").</summary>
    public int HqMinDistance { get; init; } = 200;
    /// <summary>The totals table is written for this many systems; totals scale by (systems in sectors) / this, rounded half up. DEFAULT, setup-victory/10 v5 banner (keeps the Classic share; Medium: Hand 90 -> 138, Collective 25 -> 38 at 553 in sectors).</summary>
    public int TotalsBaseSystems { get; init; } = 360;
    /// <summary>A pocket grows only into systems within this many map units of one of its systems. Proposal, m5-setup-and-victory §Mode Anchored step 6 (setup-victory/data/deal_day_one.py v5: 90).</summary>
    public int PocketReach { get; init; } = 90;
    /// <summary>Pocket growth prefers systems far from the Hand: key = distance to the pocket x 100 - this x distance to the Hand. Proposal, deal_day_one.py v5 (0.15).</summary>
    public int PocketAwayPercent { get; init; } = 15;
    /// <summary>Score bias of a system whose planets-file affiliation is Hand. Proposal, m5-setup-and-victory §Mode Blocs step 2 (HandAffiliationBias).</summary>
    public int HandAffiliationBias { get; init; } = -15;
    /// <summary>Score bias of a Collective-friendly system. Proposal, m5-setup-and-victory §Mode Blocs step 2 (CollectiveFriendlyBias).</summary>
    public int CollectiveFriendlyBias { get; init; } = 15;
    /// <summary>Collective pockets may grow into group core. Proposal, m5-setup-and-victory §Mode Blocs step 5 (CollectivePocketsMayEnterCore).</summary>
    public bool CollectivePocketsMayEnterCore { get; init; }
    /// <summary>One in this many of the Hand's bloc systems is a scattered outpost instead. Decided Paul 2026-10-16, m5-setup-and-victory §Mode Anchored step 3 (needE / 10).</summary>
    public int HandScatterDivisor { get; init; } = 10;
    /// <summary>One in this many of the Collective's dealt systems is a scattered rim cell instead. Decided Paul 2026-10-16, m5-setup-and-victory §Mode Anchored step 3 (needA / 6).</summary>
    public int CollectiveScatterDivisor { get; init; } = 6;
    /// <summary>Collective extra seeds. Proposal, m5-setup-and-victory §Mode Anchored step 5 (min(3, needA)).</summary>
    public int CollectiveExtraSeeds { get; init; } = 3;
    /// <summary>Hand bloc jitter j = rand(0..299). Proposal, m5-setup-and-victory §Mode Anchored step 4.</summary>
    public int HandJitterMax { get; init; } = 299;
    /// <summary>Collective seed jitter J = rand(0..99). Proposal, m5-setup-and-victory §Mode Anchored step 5.</summary>
    public int CollectiveJitterMax { get; init; } = 99;
    /// <summary>Distance measure for the bloc scores = isqrt(dx*dx + dy*dy) / this (the travel formula's base, no lanes since 2026-10-16). Proposal, m5-setup-and-victory §Mode Anchored.</summary>
    public int LaneTimeDivisor { get; init; } = 5;
    /// <summary>Strong holder: Collective support 60 + rand(0..30). Traced, m5-setup-and-victory §P2 (SDPRTB 0x1e02-0x1e05).</summary>
    public int StrongBase { get; init; } = 60;
    /// <summary>Weak holder: Collective support 20 + rand(0..30). Traced, m5-setup-and-victory §P2.</summary>
    public int WeakBase { get; init; } = 20;
    /// <summary>The spread of the two bands above. Traced, m5-setup-and-victory §P2.</summary>
    public int BandSpread { get; init; } = 30;
    /// <summary>Neutral core system: rand(0..18) + 50 - 18/2. Traced, m5-setup-and-victory §P2 (GNPRTB 7764 = 18).</summary>
    public int NeutralCoreSpread { get; init; } = 18;
    /// <summary>Neutral system outside group core: fixed, no draw. Traced shape, m5-setup-and-victory §P2.</summary>
    public int NeutralRimSupport { get; init; } = 50;
}

/// <summary>Raised by setup when its data or rule cannot produce a game (a bad anchor, no HQ candidate). The message says which.</summary>
public sealed class SetupException : Exception
{
    public SetupException(string message) : base(message) { }
}

/// <summary>A side's holdings the deal could not reach (the pool ran dry). Not expected on the shipped data (test AN12).</summary>
public sealed record DealShortfall(Side Side, int Missing, string Step);

/// <summary>The result of an Anchored deal: the day-one rows (every system, ordinal order), the HQ system and any shortfalls.</summary>
public sealed record AnchoredDeal(IReadOnlyList<DayOneRow> Rows, string Hq, IReadOnlyList<DealShortfall> Shortfalls);

/// <summary>
/// The Anchored dealer. rule: m5-setup-and-victory §Mode Anchored (steps 1-10); reference prototype setup-victory/data/deal_day_one.py (the sheet wins where they differ).
/// All draws come from the game's single <see cref="GameRng"/> in this fixed order: 1 (HQ), one j per Hand-pool system (name order), one J per Free system
/// (name order), the scatter shuffle, the Hand strong/weak shuffle, the Collective strong/weak shuffle, then the support draws of P2 in name order.
/// Integer arithmetic only; nothing depends on dictionary order (systems are indexed in ordinal name order, as <see cref="Galaxy.Systems"/> is).
/// </summary>
public static class AnchoredDealer
{
    private const string Core = "core", InnerRim = "inner_rim", OuterRim = "outer_rim"; // group names, m1-galaxy-data
    private const string Shoal = "The Shoal", Charter = "Charter"; // fixed by the original, m5-setup-and-victory §P1 (Traced: both looked up by id)

    /// <summary>The checks of the anchors file against the galaxy: every anchor exists, no duplicates, none is Charter or The Shoal. rule: m5-setup-and-victory §Mode Anchored step 1</summary>
    public static void Validate(Galaxy g, IReadOnlyList<AnchorRow> anchors)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var a in anchors)
        {
            if (!g.TryFindSystem(a.System, out _)) throw new SetupException($"{AnchorRow.FileName}: anchor '{a.System}' is not a system of the galaxy.");
            if (!seen.Add(a.System)) throw new SetupException($"{AnchorRow.FileName}: anchor '{a.System}' appears twice.");
            if (a.System == Charter || a.System == Shoal) throw new SetupException($"{AnchorRow.FileName}: '{a.System}' is fixed by the sheet and may not be an anchor.");
        }
    }

    public static AnchoredDeal Deal(Galaxy g, GameRng rng, IReadOnlyList<AnchorRow> anchors, BlocsTotals totals, DealParams p)
    {
        Validate(g, anchors);
        int n = g.Systems.Count;
        var shortfalls = new List<DealShortfall>();
        string Name(int i) => g.Systems[i].Name;
        string GroupOf(int i) => g.Systems[i].Group;
        bool IsRim(int i) => GroupOf(i) is InnerRim or OuterRim;
        string Affiliation(int i) => g.Systems[i].Carried.TryGetValue("affiliation", out var a) ? a.Trim() : "";
        bool InSectors(int i) => !g.Systems[i].IsOutsideSectors; // Glasswood and Frostholm are never dealt

        // ---- step 1: fixed
        g.TryFindSystem(Charter, out int charter);
        if (!g.TryFindSystem(Shoal, out int shoal)) throw new SetupException("The galaxy has no The Shoal.");
        var owner = new Side?[n];
        var locked = new bool[n];
        var anchor = new bool[n];
        var anchorSupport = new int?[n];
        var handAnchors = new List<int>(); var collectiveAnchors = new List<int>();
        owner[charter] = Side.Hand; owner[shoal] = Side.Collective;
        foreach (var a in anchors) // file order
        {
            g.TryFindSystem(a.System, out int i);
            anchor[i] = true; anchorSupport[i] = a.CollectiveSupport;
            if (a.Owner == Side.Hand) { owner[i] = Side.Hand; handAnchors.Add(i); }
            else if (a.Owner == Side.Collective) { owner[i] = Side.Collective; collectiveAnchors.Add(i); }
            else locked[i] = true; // locked neutral: never dealt, never scattered
        }

        // ---- step 2: HQ (1 draw). Candidates: rim, not The Shoal, not an anchor of any kind, far enough from Charter (straight line).
        var candidates = new List<int>();
        for (int i = 0; i < n; i++)
            if (IsRim(i) && i != shoal && !anchor[i] && MapDistance(g, i, charter) >= p.HqMinDistance) candidates.Add(i);
        if (candidates.Count == 0) throw new SetupException("No system satisfies the HQ candidate rule (rim, not an anchor, far enough from Charter).");
        candidates.Sort((a, b) => string.CompareOrdinal(Name(a), Name(b)));
        int hq = candidates[rng.NextInt(0, candidates.Count - 1)];
        owner[hq] = Side.Collective;

        // ---- step 3: counts (totals scaled to the galaxy's size)
        int inSectors = Enumerable.Range(0, n).Count(InSectors);
        int Scale(int total) => (int)(((long)total * inSectors * 2 + p.TotalsBaseSystems) / (2L * p.TotalsBaseSystems));
        int needE = Math.Max(0, Scale(totals.HandTotal) - 1 - handAnchors.Count);
        int needA = Math.Max(0, Scale(totals.CollectiveTotal) - 2 - collectiveAnchors.Count);
        int scatterE = needE / p.HandScatterDivisor, scatterA = needA / p.CollectiveScatterDivisor;
        needE -= scatterE; needA -= scatterA;

        bool Open(int i) => owner[i] == null && !locked[i] && InSectors(i);

        // ---- step 4: Hand bloc
        var seedsE = new List<int> { charter }; seedsE.AddRange(handAnchors);
        var dEA = LaneTimes(g, seedsE, p.LaneTimeDivisor);
        var pool = new List<int>();
        for (int i = 0; i < n; i++) if (Open(i)) pool.Add(i);
        var score = new long[n];
        foreach (int i in pool) // name order, one draw each
        {
            int j = rng.NextInt(0, p.HandJitterMax);
            int bias = Affiliation(i) == "Hand" ? p.HandAffiliationBias : Affiliation(i) == "Collective-friendly" ? p.CollectiveFriendlyBias : 0;
            score[i] = ((long)dEA[i] + bias) * 100 + j;
        }
        pool.Sort((a, b) => score[a] != score[b] ? score[a].CompareTo(score[b]) : a.CompareTo(b)); // index order = ordinal name order
        int takeE = Math.Min(needE, pool.Count);
        for (int k = 0; k < takeE; k++) owner[pool[k]] = Side.Hand;
        if (takeE < needE) shortfalls.Add(new DealShortfall(Side.Hand, needE - takeE, "bloc"));

        // ---- step 5: Collective seeds
        var allHand = new List<int>();
        for (int i = 0; i < n; i++) if (owner[i] == Side.Hand) allHand.Add(i);
        var dE = LaneTimes(g, allHand, p.LaneTimeDivisor);
        var dEmap = LaneTimes(g, allHand, 1);
        var free = new List<int>();
        for (int i = 0; i < n; i++) if (Open(i) && IsRim(i)) free.Add(i);
        var jitter = new int[n];
        foreach (int i in free) jitter[i] = rng.NextInt(0, p.CollectiveJitterMax); // always drawn, fixed count
        int wantExtra = Math.Min(p.CollectiveExtraSeeds, needA);
        var extras = new List<int>();
        var friendly = free.Where(i => Affiliation(i) == "Collective-friendly").ToList();
        friendly.Sort((a, b) => dE[a] != dE[b] ? dE[b].CompareTo(dE[a]) : a.CompareTo(b));
        foreach (int i in friendly) { if (extras.Count >= wantExtra) break; extras.Add(i); }
        if (extras.Count < wantExtra)
        {
            var rest = free.Where(i => !extras.Contains(i)).ToList();
            rest.Sort((a, b) =>
            {
                long ka = (long)dE[a] * 100 + jitter[a], kb = (long)dE[b] * 100 + jitter[b];
                return ka != kb ? kb.CompareTo(ka) : a.CompareTo(b);
            });
            foreach (int i in rest) { if (extras.Count >= wantExtra) break; extras.Add(i); }
        }
        foreach (int i in extras) owner[i] = Side.Collective;

        // ---- step 6: grow pockets (Blocs step 5), seeds in order The Shoal, the HQ, the Collective anchors, the extras
        var seeds = new List<int> { shoal, hq }; seeds.AddRange(collectiveAnchors); seeds.AddRange(extras);
        var pockets = seeds.Select(s => new List<int> { s }).ToList();
        int placed = extras.Count;
        while (placed < needA)
        {
            bool progressed = false;
            for (int si = 0; si < seeds.Count && placed < needA; si++)
            {
                int best = -1; long bestKey = 0;
                for (int v = 0; v < n; v++)
                {
                    if (!Open(v) || (!p.CollectivePocketsMayEnterCore && GroupOf(v) == Core)) continue;
                    int near = int.MaxValue;
                    foreach (int u in pockets[si]) near = Math.Min(near, MapDistance(g, u, v));
                    if (near > p.PocketReach) continue;
                    // minimise: distance to the pocket x 100 - PocketAwayPercent x distance to the Hand, then ordinal name
                    long key = (long)near * 100 - (long)p.PocketAwayPercent * dEmap[v];
                    if (best < 0 || key < bestKey) { best = v; bestKey = key; }
                }
                if (best < 0) continue;
                owner[best] = Side.Collective; pockets[si].Add(best); placed++; progressed = true;
            }
            if (!progressed) break; // a whole round placed nothing
        }
        if (placed < needA) shortfalls.Add(new DealShortfall(Side.Collective, needA - placed, "pockets"));

        // ---- step 7: scatter (Hand outposts anywhere, then Collective rim cells, continuing in the shuffled order)
        var loose = new List<int>();
        for (int i = 0; i < n; i++) if (Open(i)) loose.Add(i);
        Shuffle(loose, rng);
        int ePlaced = Math.Min(scatterE, loose.Count);
        for (int k = 0; k < ePlaced; k++) owner[loose[k]] = Side.Hand;
        if (ePlaced < scatterE) shortfalls.Add(new DealShortfall(Side.Hand, scatterE - ePlaced, "scatter"));
        int aPlaced = 0;
        for (int k = ePlaced; k < loose.Count && aPlaced < scatterA; k++)
            if (IsRim(loose[k])) { owner[loose[k]] = Side.Collective; aPlaced++; }
        if (aPlaced < scatterA) shortfalls.Add(new DealShortfall(Side.Collective, scatterA - aPlaced, "scatter"));

        // ---- step 8: strong and weak. Anchors, Charter, The Shoal and the HQ are strong; the shuffled lists decide the rest (Hand list first, then Collective).
        var weak = new bool[n];
        int handExceptCharter = 0;
        var ed = new List<int>(); var ad = new List<int>();
        for (int i = 0; i < n; i++)
        {
            if (owner[i] == Side.Hand && i != charter) { handExceptCharter++; if (!anchor[i]) ed.Add(i); }
            if (owner[i] == Side.Collective && i != shoal && i != hq && !anchor[i]) ad.Add(i);
        }
        int collectiveExceptShoalHq = ad.Count + collectiveAnchors.Count;
        int strongE = Math.Max(0, Split(handExceptCharter + 1, totals.HandStrong, totals.HandWeak) - 1);
        Shuffle(ed, rng);
        for (int k = 0; k < ed.Count; k++) weak[ed[k]] = k >= Math.Max(0, strongE - handAnchors.Count);
        int strongA = Split(collectiveExceptShoalHq, totals.CollectiveStrong, totals.CollectiveWeak);
        Shuffle(ad, rng);
        for (int k = 0; k < ad.Count; k++) weak[ad[k]] = k >= Math.Max(0, strongA - collectiveAnchors.Count);

        // ---- steps 9-10: support (P2 with the anchor change) and explored
        var rows = new List<DayOneRow>(n);
        for (int i = 0; i < n; i++)
        {
            int support;
            if (i == charter) support = 0;
            else if (i == shoal || i == hq) support = 100;
            else if (anchorSupport[i] is { } fixedSupport) support = fixedSupport; // no draw
            else if (owner[i] == Side.Collective) support = (weak[i] ? p.WeakBase : p.StrongBase) + rng.NextInt(0, p.BandSpread);
            else if (owner[i] == Side.Hand) support = 100 - ((weak[i] ? p.WeakBase : p.StrongBase) + rng.NextInt(0, p.BandSpread));
            else if (GroupOf(i) == Core) support = rng.NextInt(0, p.NeutralCoreSpread) + p.NeutralRimSupport - p.NeutralCoreSpread / 2;
            else support = p.NeutralRimSupport;
            bool exploredAll = GroupOf(i) == Core || i == charter || i == shoal;
            rows.Add(new DayOneRow(Name(i), owner[i], support, weak[i], exploredAll, owner[i] != null, i == hq));
        }
        return new AnchoredDeal(rows, Name(hq), shortfalls);
    }

    /// <summary>round half up share: n if w == 0, else (2*n*s + (s + w)) / (2*(s + w)). rule: m5-setup-and-victory §Mode Blocs step 6 (split)</summary>
    internal static int Split(int n, int s, int w) => w == 0 ? n : (2 * n * s + (s + w)) / (2 * (s + w));

    /// <summary>for i = len-1 down to 1: j = rand(0..i), swap [i] and [j]. rule: m5-setup-and-victory §Mode Anchored step 7</summary>
    private static void Shuffle(List<int> list, GameRng rng)
    {
        for (int i = list.Count - 1; i >= 1; i--)
        {
            int j = rng.NextInt(0, i);
            (list[i], list[j]) = (list[j], list[i]);
        }
    }

    /// <summary>Straight-line map distance (floor). rule: m5-setup-and-victory §P1 HQ pick (HqMinDistance)</summary>
    private static int MapDistance(Galaxy g, int a, int b)
    {
        long dx = g.Systems[a].MapX - g.Systems[b].MapX, dy = g.Systems[a].MapY - g.Systems[b].MapY;
        return ISqrt(dx * dx + dy * dy);
    }

    /// <summary>Distance to the nearest of a set of systems, isqrt(dx*dx + dy*dy) / divisor (free hyperspace: the straight line). rule: m5-setup-and-victory §Mode Anchored</summary>
    private static int[] LaneTimes(Galaxy g, IEnumerable<int> sources, int divisor)
    {
        int n = g.Systems.Count;
        var src = sources.ToList();
        var d = new int[n];
        for (int v = 0; v < n; v++)
        {
            int best = int.MaxValue;
            foreach (int s in src) best = Math.Min(best, MapDistance(g, s, v) / divisor);
            d[v] = best;
        }
        return d;
    }

    /// <summary>Integer square root (floor).</summary>
    private static int ISqrt(long v) => (int)GalaxyLoader.Isqrt(v);
}
