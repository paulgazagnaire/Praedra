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
}

public sealed partial class SimpleAi
{
    // ================================================================== campaign (AI trainer 2026-11-26)

    /// <summary>The side's campaign now, for logs and tests: its sector, whether it strikes yet, its number; null with none or the switch off.</summary>
    public (string Sector, bool Striking, int Number)? CampaignNow =>
        CampaignOn && _mem.Campaign is { } c && c.Sector.Length > 0 ? (c.Sector, c.Strike > 0, c.Number) : null;

    private bool CampaignOn => _p.Campaign && !_advisor && _side == Side.Collective; // the Collective only (SimpleAiParams.Campaign)

    /// <summary>The sector of the running campaign in its preparation (null otherwise).</summary>
    private string? PreparingSector => CampaignOn && _mem.Campaign is { Strike: 0 } c && c.Sector.Length > 0 ? c.Sector : null;

    /// <summary>The sector of the running campaign in its strike (null otherwise).</summary>
    private string? StrikingSector => CampaignOn && _mem.Campaign is { Strike: > 0 } c && c.Sector.Length > 0 ? c.Sector : null;

    private string? CampaignSector => CampaignOn && _mem.Campaign is { } c && c.Sector.Length > 0 ? c.Sector : null;

    /// <summary>
    /// The day's campaign review: end the campaign when the side holds CampaignHoldPercent of the sector's populated worlds it knows, when the
    /// picture shows no enemy world left there, or after CampaignMaxDays; then pick the next sector. A campaign prepares for CampaignPrepareDays
    /// (its agents and raids work the sector), then strikes (its invasions go there first); it strikes sooner, after half the preparation, once
    /// a fresh picture of every enemy world there shows no defence.
    /// </summary>
    private void PlanCampaign()
    {
        if (!CampaignOn) return;
        var c = _mem.Campaign;
        if (c != null && c.Sector.Length > 0)
        {
            var (enemy, held, pop, defences, stale) = SectorCounts(c.Sector);
            bool done = enemy == 0 || (pop > 0 && held * 100 >= pop * _p.CampaignHoldPercent) || Today - c.Started >= _p.CampaignMaxDays;
            if (!done)
            {
                int age = Today - c.Started;
                if (c.Strike == 0 && (age >= _p.CampaignPrepareDays || (age >= _p.CampaignPrepareDays / 2 && defences == 0 && stale == 0))) c.Strike = Today;
                return;
            }
        }
        string? pick = PickCampaignSector(c?.Sector);
        _mem.Campaign = pick == null
            ? new AiCampaign { Sector = "", Started = Today, Number = c?.Number ?? 0, Previous = c?.Sector }
            : new AiCampaign { Sector = pick, Started = Today, Number = (c?.Number ?? 0) + 1, Previous = c?.Sector };
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
    private string? PickCampaignSector(string? previous)
    {
        var g = _h.Data.Galaxy; var hops = Hops();
        int reach = Math.Max(_p.TargetHops, Math.Max(CellsOn ? _p.CellsHops : 0, GuerrillaOn ? _p.GuerrillaHops : 0));
        var score = new SortedDictionary<string, long>(StringComparer.Ordinal);
        foreach (var sys in g.Systems)
        {
            string s = sys.Name;
            if (sys.IsOutsideSectors || hops[sys.Index] == 0 || hops[sys.Index] > reach || Destroyed(s) || !IsEnemyHeld(s)) continue;
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

    /// <summary>The campaign's anchor for the rally: the enemy world seen in its sector with the most facilities (then the name); null with none.</summary>
    private string? CampaignAnchor()
    {
        if (CampaignSector is not { } sector) return null;
        return _h.Data.Galaxy.Systems.Where(x => x.Sector == sector && !Destroyed(x.Name) && IsEnemyHeld(x.Name)).Select(x => (s: x.Name, n: FacilitiesAt(x.Name)))
            .OrderByDescending(t => t.n).ThenBy(t => t.s, StringComparer.Ordinal).Select(t => t.s).FirstOrDefault();
    }

    /// <summary>CampaignAgentPercent: a mission's value at a system of the campaign's sector (any phase), else unchanged.</summary>
    private int CampaignAgentValue(string system, int value)
    {
        string? sector = CampaignSector;
        if (sector == null || !_h.Data.Galaxy.TryFindSystem(system, out int i) || _h.Data.Galaxy.Systems[i].Sector != sector) return value;
        return (int)Math.Min(int.MaxValue, (long)value * _p.CampaignAgentPercent / 100);
    }

    /// <summary>
    /// A target's value in PickTarget: raids in the preparing sector times CampaignRaidPercent; invasions in the striking sector times
    /// CampaignStrikePercent. A fresh invasion elsewhere is worth CampaignOffTargetPercent during the strike and CampaignPrepareOffTargetPercent
    /// during the preparation (the fleets gather for the sector instead of grabbing soft worlds anywhere); taking back a world the side lost
    /// (<paramref name="retake"/>) is never damped, a counter-attack makes sense on its own.
    /// </summary>
    private long CampaignTargetValue(Praedra.Core.Map.StarSystem sys, long value, bool raid, bool retake = false)
    {
        if (raid)
        {
            string? prep = PreparingSector;
            return prep != null && sys.Sector == prep ? value * _p.CampaignRaidPercent / 100 : value;
        }
        string? sector = CampaignSector;
        if (sector == null) return value;
        bool striking = StrikingSector != null;
        if (sys.Sector == sector) return striking ? value * _p.CampaignStrikePercent / 100 : value;
        if (retake) return value;
        return value * (striking ? _p.CampaignOffTargetPercent : _p.CampaignPrepareOffTargetPercent) / 100;
    }
}
