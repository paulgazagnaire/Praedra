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

    internal AiCampaign Copy() => new() { Sector = Sector, Started = Started, Strike = Strike, Number = Number, Previous = Previous };
}

public sealed partial class SimpleAi
{
    // ================================================================== campaign (AI trainer 2026-11-26)

    /// <summary>The side's campaign now, for logs and tests: its sector, whether it strikes yet, its number; null with none or the switch off.</summary>
    public (string Sector, bool Striking, int Number)? CampaignNow =>
        CampaignOn && _mem.Campaign is { } c && c.Sector.Length > 0 ? (c.Sector, c.Strike > 0, c.Number) : null;

    private bool CampaignOn => _p.Campaign && !_advisor && _side == Side.Collective; // the Collective only (SimpleAiParams.Campaign)

    /// <summary>Every running campaign (the first, then the others), for logs and tests; empty with none or the switch off.</summary>
    public IReadOnlyList<(string Sector, bool Striking, int Number)> CampaignsNow =>
        Campaigns().Select(c => (c.Sector, c.Strike > 0, c.Number)).ToList();

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
        if (_mem.MoreCampaigns is { Count: 0 }) _mem.MoreCampaigns = null; // one slot: none, as a game begun at Medium (QA-125: an empty list broke the Max below)
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
            int number = Math.Max(_mem.Campaign?.Number ?? 0, (_mem.MoreCampaigns is { Count: > 0 } mc ? mc.Max(x => x.Number) : 0)); // the game's count, every slot
            var others = Campaigns().Where(x => !ReferenceEquals(x, c)).Select(x => x.Sector).ToList();
            string? pick = PickCampaignSector(c?.Sector, others);
            var next = pick == null
                ? new AiCampaign { Sector = "", Started = Today, Number = c?.Number ?? 0, Previous = c?.Sector }
                : new AiCampaign { Sector = pick, Started = Today, Number = number + 1, Previous = c?.Sector };
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

    /// <summary>CampaignAgentPercent: a mission's value at a system of a running campaign's sector (any phase), else unchanged.</summary>
    private int CampaignAgentValue(string system, int value)
    {
        if (CampaignAt(system) == null) return value;
        return (int)Math.Min(int.MaxValue, (long)value * _p.CampaignAgentPercent / 100);
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
        if (raid) return here is { Strike: 0 } ? value * _p.CampaignRaidPercent / 100 : value;
        if (here != null) return here.Strike > 0 ? value * _p.CampaignStrikePercent / 100 : value;
        if (retake || CampaignSector == null) return value;
        bool striking = Campaigns().Any(c => c.Strike > 0);
        return value * (striking ? _p.CampaignOffTargetPercent : _p.CampaignPrepareOffTargetPercent) / 100;
    }
}
