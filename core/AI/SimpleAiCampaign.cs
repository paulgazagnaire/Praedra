using Praedra.Core.Clock;

namespace Praedra.Core.AI;

/// <summary>
/// The AI's campaign (AI trainer 2026-11-26; Paul 2026-11-26, "VERY important": the AI builds plans to fight, invade and harass that make sense;
/// the Collective weakens a sector's defences and threats before it invades, and that preparation is the player's hint of what is coming).
/// Saved in <see cref="AiMemory.Campaign"/>; an older save loads with none and the AI picks one on its next day. rule: ai tuning,
/// SimpleAiParams.Campaign.
/// </summary>
public sealed class AiCampaign
{
    /// <summary>The sector the campaign is for.</summary>
    public string Sector { get; set; } = "";
    /// <summary>The day the campaign began (its preparation).</summary>
    public int Started { get; set; }
    /// <summary>The day the strike began; 0 while preparing.</summary>
    public int Strike { get; set; }
    /// <summary>The campaign's number in this game, 1 for the first.</summary>
    public int Number { get; set; }
    /// <summary>The last campaign's sector, kept so the next one goes elsewhere when it can.</summary>
    public string? Previous { get; set; }
    /// <summary>The front's plan (SimpleAiParams.FrontPlans); Campaign for a save made before 2026-11-26 or with the switch off.</summary>
    public AiFrontPlan Plan { get; set; }
    /// <summary>The conditions the front started in (AiVerdict.Conditions; SimpleAiParams.FrontMemory).</summary>
    public int Conditions { get; set; }
    /// <summary>Enemy worlds the side saw in the sector when the front started (SimpleAiParams.FrontMemory).</summary>
    public int EnemyAtStart { get; set; }

    internal AiCampaign Copy() => new() { Sector = Sector, Started = Started, Strike = Strike, Number = Number, Previous = Previous, Plan = Plan, Conditions = Conditions, EnemyAtStart = EnemyAtStart };
}

/// <summary>
/// How an ended front went (SimpleAiParams.FrontMemory): its plan, the conditions it started in (bits: 1 = defences seen, 2 = two or more lightly
/// held worlds half on the Collective's side, 4 = an enemy fleet seen), the day it ended, and whether it worked (the enemy lost a quarter of its
/// worlds there or more). rule: ai tuning (AI trainer 2026-11-26, specs/engine/ai-notes/2026-11-26-adaptive-commander.md §4 Adapting).
/// </summary>
public sealed record AiVerdict
{
    public AiFrontPlan Plan { get; init; }
    public int Conditions { get; init; }
    public int Day { get; init; }
    public bool Worked { get; init; }
}

/// <summary>
/// What a front (a campaign's sector) works at: the commander's front plans (AI trainer 2026-11-26, specs/engine/ai-notes/2026-11-26-adaptive-commander.md
/// §3, the Collective's plans 9, 10 and 14). Every plan ends in the same strike; they differ in what the preparation hits. rule: ai tuning,
/// SimpleAiParams.FrontPlans.
/// </summary>
public enum AiFrontPlan
{
    /// <summary>Sabotage the defences, look, raid, then invade (the campaign before 2026-11-26).</summary>
    Campaign,
    /// <summary>Incite uprisings on the Hand's worlds there, so it must double its garrisons (m2-uprisings-and-garrison), then invade.</summary>
    Bleed,
    /// <summary>Raid and sabotage the mines and yards there (m3-bombardment, m5-missions §6), then invade.</summary>
    Starve,
}

public sealed partial class SimpleAi
{
    // ================================================================== campaign (AI trainer 2026-11-26)

    /// <summary>The side's campaign now, for logs and tests: its sector, whether it strikes yet, its number; null with none or the switch off.</summary>
    public (string Sector, bool Striking, int Number)? CampaignNow =>
        CampaignOn && _mem.Campaign is { } c && c.Sector.Length > 0 ? (c.Sector, c.Strike > 0, c.Number) : null;

    private bool CampaignOn => _p.Campaign && !_advisor && (_side == Side.Collective || _p.HandFronts); // the Collective, the Hand with HandFronts

    /// <summary>Every running campaign (the first, then the others), for logs and tests; empty with none or the switch off.</summary>
    public IReadOnlyList<(string Sector, bool Striking, int Number)> CampaignsNow =>
        Campaigns().Select(c => (c.Sector, c.Strike > 0, c.Number)).ToList();

    /// <summary>Every running front with its plan (SimpleAiParams.FrontPlans), for logs and tests.</summary>
    public IReadOnlyList<(string Sector, AiFrontPlan Plan, bool Striking, int Number)> FrontsNow =>
        Campaigns().Select(c => (c.Sector, c.Plan, c.Strike > 0, c.Number)).ToList();

    /// <summary>The running campaigns with a sector: the first (AiMemory.Campaign), then the others (MoreCampaigns), in slot order.</summary>
    private IEnumerable<AiCampaign> Campaigns()
    {
        if (!CampaignOn) yield break;
        if (_mem.Campaign is { } c && c.Sector.Length > 0) yield return c;
        if (_mem.MoreCampaigns != null) foreach (var m in _mem.MoreCampaigns) if (m.Sector.Length > 0) yield return m;
    }

    /// <summary>The running campaign whose sector this is; null with none.</summary>
    private AiCampaign? CampaignIn(string sector) => Campaigns().FirstOrDefault(c => c.Sector == sector);

    private AiCampaign? CampaignAt(string system) =>
        _h.Data.Galaxy.TryFindSystem(system, out int i) ? CampaignIn(_h.Data.Galaxy.Systems[i].Sector) : null;

    /// <summary>The first running campaign's sector (null with none): the agents' sabotage weighting and its rally read every campaign through CampaignIn.</summary>
    private string? CampaignSector => Campaigns().FirstOrDefault()?.Sector;

    /// <summary>
    /// The day's campaign review: end the campaign when the side holds CampaignHoldPercent of the sector's populated worlds it knows, when the
    /// picture shows no enemy world left there, or after CampaignMaxDays; then pick the next sector. A campaign prepares for CampaignPrepareDays
    /// (its agents and raids work the sector), then strikes (its invasions go there first); it strikes sooner, after half the preparation, once
    /// a fresh picture of every enemy world there shows no defence.
    /// </summary>
    private void PlanCampaign()
    {
        if (!CampaignOn) return;
        int slots = Math.Max(1, _p.CampaignsAtOnce);
        if (slots > 1) { _mem.MoreCampaigns ??= new(); while (_mem.MoreCampaigns.Count < slots - 1) _mem.MoreCampaigns.Add(new AiCampaign { Started = Today }); }
        // a save made at a level with more slots (Hard, then played at Medium): drop the extra campaigns, which this review would never end (Ponytail on #398)
        if (_mem.MoreCampaigns is { } more && more.Count > slots - 1) more.RemoveRange(slots - 1, more.Count - (slots - 1));
        for (int k = 0; k < slots; k++)
        {
            var c = k == 0 ? _mem.Campaign : _mem.MoreCampaigns![k - 1];
            if (c != null && c.Sector.Length > 0)
            {
                var (enemy, held, pop, defences, stale) = SectorCounts(c.Sector);
                bool done = enemy == 0 || (pop > 0 && held * 100 >= pop * _p.CampaignHoldPercent) || Today - c.Started >= _p.CampaignMaxDays;
                if (!done)
                {
                    int age = Today - c.Started;
                    if (c.Strike == 0 && (age >= _p.CampaignPrepareDays || (age >= _p.CampaignPrepareDays / 2 && defences == 0 && stale == 0))) c.Strike = Today;
                    continue;
                }
            }
            if (_p.FrontMemory && c is { Sector.Length: > 0 }) RecordVerdict(c);
            int number = Math.Max(_mem.Campaign?.Number ?? 0, _mem.MoreCampaigns?.Max(x => x.Number) ?? 0); // the game's count, every slot
            var others = Campaigns().Where(x => !ReferenceEquals(x, c)).Select(x => x.Sector).ToList();
            string? pick = PickCampaignSector(c?.Sector, others);
            var next = pick == null
                ? new AiCampaign { Sector = "", Started = Today, Number = c?.Number ?? 0, Previous = c?.Sector }
                : new AiCampaign { Sector = pick, Started = Today, Number = number + 1, Previous = c?.Sector,
                    Plan = _p.FrontPlans ? PickFrontPlan(pick, Campaigns().Where(x => !ReferenceEquals(x, c)).Select(x => x.Plan).ToList()) : AiFrontPlan.Campaign };
            if (pick != null && _p.FrontMemory) { var (cond, _, en) = FrontConditions(pick); next.Conditions = cond; next.EnemyAtStart = en; }
            if (k == 0) _mem.Campaign = next; else _mem.MoreCampaigns![k - 1] = next;
        }
    }

    /// <summary>
    /// Of a sector: enemy-held worlds the side sees, worlds it holds, populated worlds (seen), defences seen on the enemy worlds, and enemy worlds
    /// whose picture is older than CampaignLookDays (an unseen defence is no proof of none).
    /// </summary>
    private (int Enemy, int Held, int Populated, int Defences, int Stale) SectorCounts(string sector)
    {
        int enemy = 0, held = 0, pop = 0, def = 0, stale = 0;
        foreach (var sys in _h.Data.Galaxy.Systems)
        {
            if (sys.Sector != sector || Destroyed(sys.Name)) continue;
            string s = sys.Name;
            bool mine = Held(s);
            if (!(mine ? _h.Economy.SlotsOf(s).Populated : SlotsSeen(s).Populated)) continue;
            pop++;
            if (mine) held++;
            else if (IsEnemyHeld(s))
            {
                enemy++; def += DefencesSeenAt(s);
                if (_view != null && (!OwnerSeen(s, out _, out int age) || age > _p.CampaignLookDays)) stale++;
            }
        }
        return (enemy, held, pop, def, stale);
    }

    /// <summary>
    /// The next campaign's sector: a sector with an enemy world the side sees within its reach (TargetHops, or CellsHops / GuerrillaHops when
    /// those are on), scored by the worth of those worlds (slots and facilities, this game's liking) over their distance; one of the three best,
    /// drawn from the private generator (seeded by the game plan), and not the last one when another is there. Paul 2026-11-19: not predictable.
    /// </summary>
    private string? PickCampaignSector(string? previous, IReadOnlyList<string> others)
    {
        var g = _h.Data.Galaxy; var hops = Hops();
        var apart = others.Count == 0 ? null : LanesFromSectors(others);
        int reach = Math.Max(_p.TargetHops, Math.Max(CellsOn ? _p.CellsHops : 0, GuerrillaOn ? _p.GuerrillaHops : 0));
        var score = new SortedDictionary<string, long>(StringComparer.Ordinal);
        foreach (var sys in g.Systems)
        {
            string s = sys.Name;
            if (sys.IsOutsideSectors || hops[sys.Index] == 0 || hops[sys.Index] > reach || Destroyed(s) || !IsEnemyHeld(s)) continue;
            if (apart != null && (others.Contains(sys.Sector) || SectorLanes(apart, sys.Sector) < _p.CampaignApartHops)) continue; // CampaignsAtOnce: another angle
            var slots = SlotsSeen(s);
            if (!slots.Populated) continue;
            long worth = (10 + slots.Energy + 2L * slots.Raw + 2L * FacilitiesAt(s)) * PlanLike(sys) / 100;
            score[sys.Sector] = score.GetValueOrDefault(sys.Sector) + worth * 1000 / (20 + 10 * hops[sys.Index]);
        }
        if (score.Count == 0) return null;
        var ranked = score.Where(kv => kv.Key != previous || score.Count == 1).OrderByDescending(kv => kv.Value).ThenBy(kv => kv.Key, StringComparer.Ordinal)
            .Take(Math.Max(1, _p.CampaignChoices)).ToList();
        return ranked[_rng.NextInt(0, ranked.Count - 1)].Key;
    }

    /// <summary>The fewest lanes from any system of the sector to the given distances' sources (LanesFromSectors).</summary>
    private int SectorLanes(int[] dist, string sector)
    {
        int min = int.MaxValue;
        foreach (var sys in _h.Data.Galaxy.Systems) if (sys.Sector == sector) min = Math.Min(min, dist[sys.Index]);
        return min;
    }

    /// <summary>Lanes from the nearest world of the given sectors to every system (int.MaxValue = unreachable).</summary>
    private int[] LanesFromSectors(IReadOnlyList<string> sectors)
    {
        var g = _h.Data.Galaxy; var dist = new int[g.Systems.Count]; Array.Fill(dist, int.MaxValue);
        var q = new Queue<int>();
        foreach (var sys in g.Systems) if (sectors.Contains(sys.Sector)) { dist[sys.Index] = 0; q.Enqueue(sys.Index); }
        while (q.Count > 0)
        {
            int c = q.Dequeue();
            foreach (var n in g.NearOf(c)) if (dist[n] == int.MaxValue) { dist[n] = dist[c] + 1; q.Enqueue(n); }
        }
        return dist;
    }

    /// <summary>The campaign's anchor for the rally: the enemy world seen in its sector with the most facilities (then the name); null with none.</summary>
    private string? CampaignAnchor()
    {
        // several at once: the one striking longest, else the first (one rally point; the other campaigns get their agents, raids and invasions)
        var lead = Campaigns().Where(c => c.Strike > 0).OrderBy(c => c.Strike).FirstOrDefault() ?? Campaigns().FirstOrDefault();
        if (lead is not { Sector: var sector }) return null;
        return _h.Data.Galaxy.Systems.Where(x => x.Sector == sector && !Destroyed(x.Name) && IsEnemyHeld(x.Name)).Select(x => (s: x.Name, n: FacilitiesAt(x.Name)))
            .OrderByDescending(t => t.n).ThenBy(t => t.s, StringComparer.Ordinal).Select(t => t.s).FirstOrDefault();
    }

    /// <summary>
    /// CampaignAgentPercent: a mission's value at a system of a running campaign's sector (any phase), else unchanged; a Bleed front's incitement
    /// and a Starve front's sabotage are worth BleedIncitePercent and StarveSabotagePercent more again (SimpleAiParams.FrontPlans).
    /// </summary>
    private int CampaignAgentValue(string system, int value, Praedra.Core.Missions.MissionKind kind)
    {
        if (CampaignAt(system) is not { } c) return value;
        long v = (long)value * _p.CampaignAgentPercent / 100;
        if (c.Plan == AiFrontPlan.Bleed && kind == Praedra.Core.Missions.MissionKind.InciteUprising) v = v * _p.BleedIncitePercent / 100;
        if (c.Plan == AiFrontPlan.Starve && kind == Praedra.Core.Missions.MissionKind.Sabotage) v = v * _p.StarveSabotagePercent / 100;
        return (int)Math.Min(int.MaxValue, v);
    }

    /// <summary>
    /// The plan for a new front in <paramref name="sector"/> (SimpleAiParams.FrontPlans), from the side's picture of the enemy worlds there:
    /// Campaign fits worlds with defences to strip (and is the fallback), Bleed fits lightly held worlds whose people half side with the
    /// Collective, Starve fits many facilities with no enemy fleet seen. A plan another front runs is taken only when nothing else fits
    /// (Paul 2026-11-26: different strategies per front). A small draw from the game plan breaks near-ties, so two games differ.
    /// </summary>
    private AiFrontPlan PickFrontPlan(string sector, IReadOnlyList<AiFrontPlan> taken)
    {
        var (cond, f, _) = FrontConditions(sector);
        var fit = new SortedDictionary<AiFrontPlan, long>
        {
            [AiFrontPlan.Campaign] = 100 + 40 * f.Defences,
            [AiFrontPlan.Bleed] = 60 * f.Soft,
            [AiFrontPlan.Starve] = f.Fleets > 0 ? 0 : 12 * f.Facilities,
        };
        foreach (var k in fit.Keys.ToList())
        {
            fit[k] = fit[k] * (100 + _rng.NextInt(0, 20)) / 100; // the game plan's small tilt (Paul 2026-11-19)
            if (_p.FrontMemory) fit[k] = fit[k] * MemoryPercent(k, cond) / 100;
        }
        return fit.OrderBy(kv => taken.Contains(kv.Key) ? 1 : 0).ThenByDescending(kv => kv.Value).ThenBy(kv => kv.Key).First().Key;
    }

    /// <summary>
    /// A sector's conditions for a front, from the side's picture of the enemy worlds there: the condition bits (AiVerdict.Conditions), the
    /// counts the plans' fit reads, and the enemy worlds seen.
    /// </summary>
    private (int Bits, (long Defences, long Soft, long Facilities, long Fleets) Counts, int Enemy) FrontConditions(string sector)
    {
        long defences = 0, soft = 0, facilities = 0, fleets = 0; int enemy = 0;
        foreach (var sys in _h.Data.Galaxy.Systems)
        {
            if (sys.Sector != sector || Destroyed(sys.Name) || !IsEnemyHeld(sys.Name)) continue;
            string s = sys.Name;
            if (!SlotsSeen(s).Populated) continue;
            enemy++;
            int d = DefencesSeenAt(s);
            defences += d;
            facilities += Math.Max(0, FacilitiesAt(s) - d);
            fleets += TargetFleetsAt(s).Count;
            if (DefendersAt(s) <= 2 && SupportSeen(s) is int sup && sup >= 30) soft++;
        }
        int bits = (defences > 0 ? 1 : 0) | (soft >= 2 ? 2 : 0) | (fleets > 0 ? 4 : 0);
        return (bits, (defences, soft, facilities, fleets), enemy);
    }

    /// <summary>
    /// FrontMemory: a plan's fit in these conditions, as a percent, from the side's verdicts on fronts that ran the same plan in the same
    /// conditions: each success x 5/4, each failure x 3/5 (a verdict older than FrontMemoryFadeDays about half as much: x 1.12 and x 0.77) (the enemy's position
    /// changes, so an old failure never bans a plan). Other conditions do not count (Paul 2026-11-26).
    /// </summary>
    private long MemoryPercent(AiFrontPlan plan, int conditions)
    {
        long f = 100; // integer only: the same game on any machine picks the same plan
        foreach (var v in _mem.Verdicts ?? new())
        {
            if (v.Plan != plan || v.Conditions != conditions) continue;
            bool old = Today - v.Day > _p.FrontMemoryFadeDays;
            f = f * (v.Worked ? (old ? 112 : 125) : (old ? 77 : 60)) / 100; // 112 and 77 are about the square roots of 125 and 60: half weight
        }
        return Math.Clamp(f, 10, 400);
    }

    /// <summary>FrontMemory: the verdict on an ending front: it worked when the enemy lost a quarter or more of the worlds it held there at the start.</summary>
    private void RecordVerdict(AiCampaign c)
    {
        var (_, _, enemyNow) = FrontConditions(c.Sector);
        bool worked = c.EnemyAtStart > 0 && (c.EnemyAtStart - enemyNow) * 4 >= c.EnemyAtStart;
        (_mem.Verdicts ??= new()).Add(new AiVerdict { Plan = c.Plan, Conditions = c.Conditions, Day = Today, Worked = worked });
    }

    /// <summary>
    /// A target's value in PickTarget: raids in a preparing sector times CampaignRaidPercent; invasions in a striking sector times
    /// CampaignStrikePercent. A fresh invasion elsewhere is worth CampaignOffTargetPercent during the strike and CampaignPrepareOffTargetPercent
    /// during the preparation (the fleets gather for the sector instead of grabbing soft worlds anywhere); taking back a world the side lost
    /// (<paramref name="retake"/>) is never damped, a counter-attack makes sense on its own.
    /// </summary>
    private long CampaignTargetValue(Praedra.Core.Map.StarSystem sys, long value, bool raid, bool retake = false)
    {
        var here = CampaignIn(sys.Sector);
        if (raid && here is { Plan: AiFrontPlan.Starve }) return value * _p.StarveRaidPercent / 100; // FrontPlans: a Starve front raids all along
        if (raid) return here is { Strike: 0 } ? value * _p.CampaignRaidPercent / 100 : value;
        if (here != null) return here.Strike > 0 ? value * _p.CampaignStrikePercent / 100 : value;
        if (retake || CampaignSector == null) return value;
        bool striking = Campaigns().Any(c => c.Strike > 0);
        return value * (striking ? _p.CampaignOffTargetPercent : _p.CampaignPrepareOffTargetPercent) / 100;
    }
}
