using Rebellion.Core.Characters;
using Rebellion.Core.Clock;
using Rebellion.Core.Construction;
using Rebellion.Core.Economy;
using Rebellion.Core.Fleets;
using Rebellion.Core.Missions;
using Rebellion.Core.Research;

namespace Rebellion.Core.AI;

/// <summary>
/// The simple AI's characters and missions (<see cref="SimpleAiParams.UseCharactersAndMissions"/>, DEFAULT on; off = the AI of before, byte for
/// byte). Every few days (AgentsEvery) it:
/// <list type="number">
/// <item>cancels Diplomacy whose target has reached DiplomacyStopSupport, and takes the passengers (post None riders) off idle fleets;</item>
/// <item>Empire with fog: keeps its best spies (HqSpies) off the posts while the Rebel HQ is unknown or due a re-check;</item>
/// <item>posts the best leader as General of the system whose loss ends the game (Coruscant, the Rebel HQ), moving him or her there first;</item>
/// <item>posts leaders as Admiral (fleets that can bombard) and General (fleets that carry troops) of the strongest fleets (OfficerFleets), the
/// officers waiting at the rally system for their fleet (m5-characters-and-force §Posts; the bonuses are m3-bombardment and m3-assault's);</item>
/// <item>sends one-member missions by expected value: value x chance / (travel days + MissionTravelBias), chance = P(not detected) x P(success)
/// from the core's own tables (MissionModule.FoilChance, DetectionX, Lookup: m5-missions §3, §Mission tables) against the defenders the side
/// believes are there. Kinds: Empire HQ hunt (Espionage or Reconnaissance on the highest HQ-belief systems, then a re-check of a known HQ),
/// Subdue on own systems in uprising, Rescue of own prisoners, Abduction of enemy characters the side has seen, Diplomacy on own low-support
/// systems and neutral border systems, Alliance Incite and Sabotage on enemy systems where no defender is believed, and Research.</item>
/// </list>
/// It reads the other side only through the "what the AI may see" block of SimpleAi.cs (MissionDefendersAt, SupportSeen, OwnerSeen,
/// EnemyCharactersSeen, the belief map and the picture), so with UseFog it reads only its side's knowledge (m5-fog-and-hq §N8). Own characters,
/// own missions and own systems are the side's own state. Like the rest of the AI it keeps no memory between days and draws no random number.
/// Not used (refused by the core with RuleMissing, or impossible today): decoys, Recruitment, Jedi Training, Death Star Sabotage; and
/// Assassination (a kill of a major becomes an injury; the AI prefers Abduction).
/// </summary>
public sealed partial class SimpleAi
{
    private static readonly MissionKind[] NoKeyKinds = { MissionKind.Rescue, MissionKind.Abduction, MissionKind.Sabotage, MissionKind.InciteUprising };

    private sealed record Job(MissionKind Kind, string System, int? TargetCharacter, int Value, string Group);

    private void PlanAgents()
    {
        _spyTargets = null; _foiledLooks = null; _looksToday.Clear();
        var mine = _h.Characters.InPlay.Where(c => c.Side == _side).ToList(); // own characters, id order
        var busy = new HashSet<int>();
        PlanMissionCancels();
        DetachRiders(busy);
        EmperorHome(mine, busy);
        var spies = ReserveSpies(mine, busy);
        PlanSystemGeneral(mine, busy, spies);
        PlanFleetOfficers(mine, busy, spies);
        PlanMissions(mine, busy);
        if (_p.HuntDroids > 0) PlanDroidLooks();
    }

    /// <summary>Free to take an order today: placed, Free, not attached, no system post, not on a mission, standing on a system (m5-characters §Availability).</summary>
    private bool Standing(CharacterRecord c) => _h.Characters.IsAvailable(c) && c.Transit == null && c.System != null;
    private bool IsKey(CharacterRecord c) => _h.Characters.ClassOf(c).Kind == CharacterKind.Major;
    private int Skill(CharacterRecord c, Skill s) => c.EffectiveSkill(s); // m5-characters-and-force §Force (B2e §1: every reader takes the effective skill)

    private int CharTicks(string from, string to) => TicksCached(from, to, _h.Characters.Params.CharacterSpeed);

    // ================================================================== cancels and riders

    /// <summary>Diplomacy is cancelled once its target's own support reaches DiplomacyStopSupport (the diplomat is wanted elsewhere). m5-missions §Orders (CancelMission).</summary>
    private void PlanMissionCancels()
    {
        foreach (var m in OwnMissions())
        {
            if (m.Kind != MissionKind.Diplomacy || m.Phase != MissionPhase.Working) continue;
            if (SupportSeen(m.Target) is int s && s >= _p.DiplomacyStopSupport)
            {
                long seq = _h.Game.Submit(sq => new CancelMissionOrder(sq, _side, m.Id));
                Note(seq, "CancelMission", $"Diplomacy {m.Id} at {m.Target} (support {s})");
            }
        }
    }

    /// <summary>Passengers (post None) of own fleets standing at a system get off (they cannot take a post or a mission aboard). m5-characters §Posts (DetachCharacter).</summary>
    private void DetachRiders(HashSet<int> busy)
    {
        foreach (var f in S.Fleets.Values.Where(f => f.Side == _side && f.Transit == null && f.At != null).OrderBy(f => f.Id))
            foreach (var c in _h.Characters.AttachedTo(f.Id).Where(c => c.Post == CharacterPost.None && c.Side == _side).OrderBy(c => c.Id))
            {
                long seq = _h.Game.Submit(sq => new DetachCharacterOrder(sq, _side, c.Id));
                Note(seq, "DetachCharacter", $"{_h.Characters.ClassOf(c).Name} off fleet {f.Id} at {f.At}");
                busy.Add(c.Id);
            }
    }

    // ================================================================== the leaders and the Emperor (LeaderMarks; B6c §7)

    /// <summary>B6c §7 leaders (0x4024d0): Luke, Mon Mothma, the Emperor and Vader.</summary>
    private bool IsLeader(CharacterRecord c)
    {
        var cp = _h.Characters.Params;
        return c.Id == cp.Luke || c.Id == _p.MonMothmaId || c.Id == cp.Emperor || c.Id == cp.Vader;
    }

    /// <summary>B6c §7 story characters (0x4025f0): the four leaders plus Leia and Han. With LeaderMarks the AI never makes them fleet or army commanders.</summary>
    private bool NeverCommands(CharacterRecord c) =>
        _p.LeaderMarks && (IsLeader(c) || c.Id == _h.Characters.Params.Leia || c.Id == _h.Missions.Data.Numbers.HanCharacterId);

    /// <summary>
    /// Emperor home (B6c §7, planner kind 6, Empire only): while the Empire holds the Seat of Power system (Coruscant, which is in its own
    /// sector, so the "holds a system in Coruscant's sector" start test holds too), the Emperor is taken off every other job, brought there and
    /// kept there; the Seat of Power (m5-characters-and-force §Seat of Power) stays on. The 1998 planner also waits for the recruitment count
    /// to reach 0; this AI never recruits (Recruitment is refused by the core today), so that count is taken as 0. QA-8 measured the Emperor on
    /// Coruscant on 1-7% of days without it, and captured on missions.
    /// </summary>
    private void EmperorHome(List<CharacterRecord> mine, HashSet<int> busy)
    {
        var cp = _h.Characters.Params;
        if (!_p.LeaderMarks || _side != Side.Empire || !Held(cp.SeatOfPowerSystem)) return;
        var e = mine.FirstOrDefault(c => c.Id == cp.Emperor);
        if (e == null) return;
        busy.Add(e.Id);
        if (e.AttachedFleet != 0 && e.Post != CharacterPost.None && e.Transit == null)
        {
            long seq = _h.Game.Submit(sq => new DetachCharacterOrder(sq, _side, e.Id));
            Note(seq, "DetachCharacter", $"{_h.Characters.ClassOf(e).Name} off fleet {e.AttachedFleet} (Emperor home)");
            return;
        }
        if (Standing(e)) MoveCharacter(e, cp.SeatOfPowerSystem);
    }

    /// <summary>The Empire hunts the HQ (fog only) while it is unknown, or known with a picture older than HqRecheckDays.</summary>
    private bool HqHuntOn => _view != null && _side == Side.Empire && (KnownEnemyHq == null || HqRecheckDue);
    private bool HqRecheckDue => _view?.KnownHq is { } k && (Today - k.Stamp >= _p.HqRecheckDays || HqGroundDue);
    /// <summary>
    /// The known HQ's ground forces are unseen or older than HqRecheckDays: only Espionage writes them (group D, m5-fog-and-hq §F2), and
    /// without them the military assumes FogUnknownWinDefenders there.
    /// </summary>
    private bool HqGroundDue
    {
        get
        {
            if (_view == null || KnownEnemyHq == null) return false;
            var g = _view.Picture(KnownEnemyHq)?.Ground;
            return g == null || AgeOf(g) >= _p.HqRecheckDays;
        }
    }

    /// <summary>Empire with fog: the best spies (espionage first, keys last) are kept off the posts while the HQ hunt is on; spies already out count.</summary>
    private HashSet<int> ReserveSpies(List<CharacterRecord> mine, HashSet<int> busy)
    {
        var set = new HashSet<int>();
        if (!HqHuntOn) return set;
        int out_ = OwnMissions().Count(m => m.Kind is MissionKind.Espionage or MissionKind.Reconnaissance);
        foreach (var c in mine.Where(c => Standing(c) && !busy.Contains(c.Id) && Skill(c, Characters.Skill.Espionage) >= _p.SpyMinEspionage)
                     .OrderBy(c => IsKey(c) ? 1 : 0).ThenByDescending(c => Skill(c, Characters.Skill.Espionage)).ThenBy(c => c.Id))
        {
            if (set.Count + out_ >= _p.HqSpies) break;
            set.Add(c.Id);
        }
        // a spy far from the hunting ground first moves to a held system near it (missions return to the system they left from)
        string? base_ = SpyBase();
        if (base_ != null)
            foreach (var c in mine.Where(c => set.Contains(c.Id)))
            {
                int t = CharTicks(c.System!, base_);
                if (t != int.MaxValue && t > _p.SpyBaseSlackTicks && MoveCharacter(c, base_)) busy.Add(c.Id);
            }
        return set;
    }

    private List<(string System, long Score)>? _spyTargets; // per review (reset in PlanAgents)
    private readonly HashSet<string> _looksToday = new(StringComparer.Ordinal); // per review: Espionage / Reconnaissance targets ordered today (lookup only)
    private Dictionary<string, int>? _foiledLooks; // per review (reset in PlanAgents)

    /// <summary>The Empire's HQ search targets as they stand (fog only; empty otherwise), best first: for logs and tests. Rebuilt on each call.</summary>
    public IReadOnlyList<(string System, long Score)> HqSearchTargets()
    {
        _view ??= _p.UseFog ? _h.Fog.ViewFor(_side) : null;
        _spyTargets = null; _foiledLooks = null; _hops = null;
        if (_view == null || _side != Side.Empire) return Array.Empty<(string, long)>();
        return SpyTargets();
    }

    /// <summary>Rank (1 = best) of a system in the Empire's full HQ search list as it stands, 0 when not listed: for logs (sim --trace).</summary>
    public int HqSearchRank(string system)
    {
        HqSearchTargets();
        var all = SearchTargets(int.MaxValue);
        return all.FindIndex(t => t.System == system) + 1;
    }

    /// <summary>
    /// Empire with fog: where to look for the Rebel HQ. The belief map (SimpleAiFog) weighted by how the Alliance hides it (ai/07 §7: far from
    /// the Empire, never in a dead end): with r = min(lanes from the nearest held system, SpyRoomCap), score = belief x (100 + SpyRoomPercent
    /// x r x r) / 100, times SpyDeadEndPercent / 100 for a system with fewer than HqMinLanes lanes; a system where an own look was foiled within
    /// SpyFoilMemoryDays is left out (it is defended: the next look would very likely be foiled too). A known HQ due a re-check is the only
    /// target. Best first, at most HqSearchTargets.
    /// </summary>
    private List<(string System, long Score)> SpyTargets() => _spyTargets ??= SearchTargets(_p.HqSearchTargets);

    private List<(string System, long Score)> SearchTargets(int take)
    {
        var list = new List<(string, long)>();
        if (KnownEnemyHq != null) { if (HqRecheckDue && !FoiledWithin(KnownEnemyHq, _p.HqRecheckFoilDays)) list.Add((KnownEnemyHq, 1)); return list; }
        var g = _h.Data.Galaxy; var hops = Hops();
        foreach (var (s, w) in HqBelief())
        {
            if (!g.TryFindSystem(s, out var i) || FoiledWithin(s, _p.SpyFoilMemoryDays)) continue;
            long r = Math.Min(hops[i], _p.SpyRoomCap);
            // a system whose owner the side has never seen counts at least SpyUnexploredBelief (deep Alliance space is where nobody looked)
            long bw = _view!.Picture(s)?.Owner == null ? Math.Max(w, _p.SpyUnexploredBelief) : w;
            long sc = bw * (100 + _p.SpyRoomPercent * r * r) / 100;
            if (g.NearOf(i).Count < _p.HqMinLanes) sc = sc * _p.SpyDeadEndPercent / 100;
            if (sc > 0) list.Add((s, sc));
        }
        return list.OrderByDescending(t => t.Item2).ThenBy(t => t.Item1, StringComparer.Ordinal).Take(take).ToList();
    }

    /// <summary>
    /// True when an own Espionage or Reconnaissance at the system was foiled within the last <paramref name="days"/> days (the side's own
    /// MissionEnded events, m5-missions §Detection): it is defended, so the next look would very likely be foiled too. The last foiled day
    /// per system is gathered once a review (lookup only, never iterated).
    /// </summary>
    private bool FoiledWithin(string system, int days)
    {
        if (_foiledLooks == null)
        {
            _foiledLooks = new Dictionary<string, int>(StringComparer.Ordinal);
            foreach (var me in NotesSince(AiNoteKind.LookFoiled, Today - Math.Max(_p.SpyFoilMemoryDays, _p.HqRecheckFoilDays)))
                _foiledLooks.TryAdd(me.System!, me.Day); // newest first: the first seen is the last foiled day
        }
        return days > 0 && _foiledLooks.TryGetValue(system, out int d) && d >= Today - days;
    }

    /// <summary>The held system with the least travel (group speed) to the SpyBaseTargets best HQ search targets, ties by name; null when none.</summary>
    private string? SpyBase()
    {
        var targets = SpyTargets().Take(_p.SpyBaseTargets).Select(t => t.System).ToList();
        if (targets.Count == 0) return null;
        int speed = _h.Missions.Data.Numbers.GroupSpeed;
        string? best = null; long bestSum = long.MaxValue;
        foreach (var s in HeldSystems())
        {
            long sum = 0;
            foreach (var t in targets) { int k = TicksCached(s, t, speed); if (k == int.MaxValue) { sum = long.MaxValue; break; } sum += k; }
            if (sum < bestSum) { bestSum = sum; best = s; }
        }
        return best;
    }

    // ================================================================== posts (m5-characters-and-force §Posts)

    private bool MoveCharacter(CharacterRecord c, string dest)
    {
        if (c.System == null || c.System == dest || c.Transit != null) return false;
        if (!_h.Fleets.FindPath(c.System, dest, _h.Characters.Params.CharacterSpeed).Ok) return false;
        long seq = _h.Game.Submit(sq => new MoveCharacterOrder(sq, _side, c.Id, dest));
        Note(seq, "MoveCharacter", $"{_h.Characters.ClassOf(c).Name} {c.System} -> {dest}");
        return true;
    }

    /// <summary>A character of the side on the way to the system (a lone trip).</summary>
    private bool Heading(List<CharacterRecord> mine, string dest, Func<CharacterRecord, bool> ok) =>
        mine.Any(c => c.Transit is { } t && t.Path[^1] == dest && c.Status == CharacterStatus.Free && ok(c));

    /// <summary>
    /// The system whose loss ends the game gets the best General the side has (leadership, then nearness; the defenders' bonus L / 30 of
    /// m3-assault). One already on the way is waited for. When the post is empty, Generals left on other systems (the HQ moved) are released.
    /// </summary>
    private void PlanSystemGeneral(List<CharacterRecord> mine, HashSet<int> busy, HashSet<int> spies)
    {
        string? crit = GuardSystem();
        if (crit == null || _h.Characters.SystemGeneralOf(crit) != null) return;
        bool CanGen(CharacterRecord c) => _h.Characters.ClassOf(c).CanGeneral && Skill(c, Characters.Skill.Leadership) >= _p.OfficerMinLeadership && !NeverCommands(c);
        foreach (var s in HeldSystems())
            if (s != crit && _h.Characters.SystemGeneralOf(s) is { } g && g.Side == _side)
            {
                long seq = _h.Game.Submit(sq => new ReleaseSystemGeneralOrder(sq, _side, s));
                Note(seq, "ReleaseSystemGeneral", $"{_h.Characters.ClassOf(g).Name} at {s}");
            }
        if (Heading(mine, crit, CanGen)) return;
        CharacterRecord? best = null; long bestScore = long.MinValue;
        foreach (var c in mine)
        {
            if (!Standing(c) || busy.Contains(c.Id) || spies.Contains(c.Id) || !CanGen(c)) continue;
            int ticks = CharTicks(c.System!, crit);
            if (ticks == int.MaxValue) continue;
            long score = Skill(c, Characters.Skill.Leadership) * 1000L - ticks;
            if (score > bestScore) { bestScore = score; best = c; }
        }
        if (best == null) return;
        busy.Add(best.Id);
        if (best.System == crit)
        {
            long seq = _h.Game.Submit(sq => new AssignSystemGeneralOrder(sq, _side, best.Id, crit));
            Note(seq, "AssignSystemGeneral", $"{_h.Characters.ClassOf(best).Name} at {crit}");
        }
        else MoveCharacter(best, crit);
    }

    /// <summary>
    /// Admirals (fleets that can bombard: m3-bombardment's L / 40 + 1) and Generals (fleets with troop room: m3-assault's L / 30) for the
    /// OfficerFleets strongest fleets (not scouts, not the home guard). The best leaders first; one standing where such a fleet lacks the post
    /// boards it, the others wait at (or go to) the rally system, where the strike piles gather.
    /// </summary>
    private void PlanFleetOfficers(List<CharacterRecord> mine, HashSet<int> busy, HashSet<int> spies)
    {
        string? guard = GuardSystem(), rally = Rally();
        var fleets = S.Fleets.Values.Where(f => f.Side == _side && f.Ships.Count > 0 && !IsScoutShape(f) && Str(f) >= _p.OfficerFleetStrength
                                               && !(f.At != null && f.At == guard && guard != rally))
            .OrderByDescending(f => Str(f)).ThenBy(f => f.Id).Take(_p.OfficerFleets).ToList();
        var open = new List<(Fleet Fleet, CharacterPost Post)>();
        foreach (var f in fleets)
        {
            if (_h.Characters.HolderOf(f.Id, CharacterPost.Admiral) == null && _h.Bombardment.Power(f) > 0) open.Add((f, CharacterPost.Admiral));
            if (_h.Characters.HolderOf(f.Id, CharacterPost.General) == null && _h.Fleets.FreeTroopSlots(f) + _h.Troops.RegimentsAboard(f).Count > 0)
                open.Add((f, CharacterPost.General));
        }
        if (open.Count == 0) return;
        bool Fits(CharacterRecord c, CharacterPost p) => _h.Characters.ClassOf(c).Can(p) && !NeverCommands(c);
        int demand = open.Count;
        if (rally != null) demand -= mine.Count(c => c.Transit is { } t && t.Path[^1] == rally && c.Status == CharacterStatus.Free
                                                     && open.Any(o => Fits(c, o.Post)) && Skill(c, Characters.Skill.Leadership) >= _p.OfficerMinLeadership);
        var cands = mine.Where(c => Standing(c) && !busy.Contains(c.Id) && !spies.Contains(c.Id) && Skill(c, Characters.Skill.Leadership) >= _p.OfficerMinLeadership
                                    && open.Any(o => Fits(c, o.Post)))
            .OrderByDescending(c => Skill(c, Characters.Skill.Leadership)).ThenBy(c => c.Id).ToList();
        foreach (var c in cands)
        {
            if (demand <= 0) break;
            // a fleet standing here that lacks a post this character can hold (Admiral first)
            var here = open.Where(o => o.Fleet.Transit == null && o.Fleet.At == c.System && Fits(c, o.Post))
                .OrderBy(o => o.Post == CharacterPost.Admiral ? 0 : 1).ThenByDescending(o => Str(o.Fleet)).ThenBy(o => o.Fleet.Id).ToList();
            if (here.Count > 0)
            {
                var (f, post) = here[0];
                long seq = _h.Game.Submit(sq => new AttachCharacterOrder(sq, _side, c.Id, f.Id, post));
                Note(seq, "AttachCharacter", $"{_h.Characters.ClassOf(c).Name} as {post} of fleet {f.Id} at {f.At}");
                open.Remove(here[0]); busy.Add(c.Id); demand--;
                continue;
            }
            if (rally == null) continue;
            busy.Add(c.Id); demand--; // waits at the rally system for a strike pile
            if (c.System != rally) MoveCharacter(c, rally);
        }
    }

    // ================================================================== missions (m5-missions §Orders; odds: §3 and §Mission tables)

    private void PlanMissions(List<CharacterRecord> mine, HashSet<int> busy)
    {
        var pool = mine.Where(c => Standing(c) && !busy.Contains(c.Id)).ToList();
        if (pool.Count == 0) return;
        var own = OwnMissions().ToList();
        var jobs = Jobs(own);
        if (jobs.Count == 0) return;
        var pairs = new List<(long Ev, CharacterRecord C, Job J)>();
        foreach (var c in pool)
            foreach (var j in jobs)
                if (Expected(c, j) is long ev && ev > 0) pairs.Add((ev, c, j));
        // greedy: best expected value first; one mission per character, per job group (a target or a cap), within the caps
        var usedChar = new HashSet<int>(); var usedGroup = new HashSet<string>(StringComparer.Ordinal);
        int diplomats = own.Count(m => m.Kind == MissionKind.Diplomacy);
        int subversion = own.Count(m => m.Kind is MissionKind.InciteUprising or MissionKind.Sabotage);
        foreach (var (ev, c, j) in pairs.OrderByDescending(p => p.Ev).ThenBy(p => p.C.Id).ThenBy(p => p.J.Kind).ThenBy(p => p.J.System, StringComparer.Ordinal))
        {
            if (usedChar.Contains(c.Id) || usedGroup.Contains(j.Group)) continue;
            if (j.Kind == MissionKind.Diplomacy && diplomats >= _p.MaxDiplomats) continue;
            if (j.Kind is MissionKind.InciteUprising or MissionKind.Sabotage && subversion >= _p.MaxSubversion) continue;
            var job = j; var ch = c;
            // A key target behind the exposure gate (m5-missions §6a.1, TargetProtected) is skipped today instead of ordered again every day.
            if (job.TargetCharacter != null && _h.Missions.CheckStart(new StartMissionOrder(0, _side, job.Kind, new List<int> { ch.Id }, new List<int>(), new List<int>(),
                    null, job.TargetCharacter)).Reason == MissionReject.TargetProtected) continue;
            long seq = _h.Game.Submit(sq => new StartMissionOrder(sq, _side, job.Kind, new List<int> { ch.Id }, new List<int>(), new List<int>(),
                job.TargetCharacter == null ? job.System : null, job.TargetCharacter));
            Note(seq, "Mission" + j.Kind, $"{_h.Characters.ClassOf(c).Name} from {c.System} to {j.System}{(j.TargetCharacter is int t ? $" (character {t})" : "")} ev {ev}");
            usedChar.Add(c.Id); usedGroup.Add(j.Group);
            if (j.Kind is MissionKind.Espionage or MissionKind.Reconnaissance) _looksToday.Add(j.System);
            if (j.Kind == MissionKind.Diplomacy) diplomats++;
            if (j.Kind is MissionKind.InciteUprising or MissionKind.Sabotage) subversion++;
        }
    }

    private bool KindUsable(MissionKind k) => _h.Missions.Data.TryGet(k, out var r) && !r.Hidden && r.AvailableTo(_side);

    /// <summary>Every mission the side wants today, with its value. Targets already worked by an own mission of the same kind are left out.</summary>
    private List<Job> Jobs(List<MissionGroup> own)
    {
        var jobs = new List<Job>();
        var taken = new HashSet<(MissionKind, string)>();
        var takenChar = new HashSet<int>();
        foreach (var m in own)
        {
            taken.Add((m.Kind, m.Target));
            if (m.TargetCharacter is int tc) takenChar.Add(tc);
        }
        bool Free(MissionKind k, string s) => KindUsable(k) && !taken.Contains((k, s));

        // 1. the Empire's HQ hunt (fog): the highest HQ-belief systems while the HQ is unknown; a known HQ again when its picture is old
        if (_view != null && _side == Side.Empire)
        {
            bool recon = _h.Fog.Params.ReconRevealsFacilities; // m5-fog-and-hq §F2: a recon success writes HqHere only with this switch on
            var targets = new List<(string Sys, int Value)>();
            if (KnownEnemyHq == null)
            {
                var st = SpyTargets(); long max = st.Count > 0 ? st[0].Score : 0;
                foreach (var (s, sc) in st) targets.Add((s, (int)(_p.HqSearchValue * sc / Math.Max(1, max))));
            }
            else if (HqRecheckDue && !FoiledWithin(KnownEnemyHq, _p.HqRecheckFoilDays)) targets.Add((KnownEnemyHq, _p.HqRecheckValue));
            foreach (var (s, v) in targets)
            {
                if (taken.Contains((MissionKind.Espionage, s)) || taken.Contains((MissionKind.Reconnaissance, s))) continue;
                if (KindUsable(MissionKind.Espionage)) jobs.Add(new Job(MissionKind.Espionage, s, null, v * _p.EspionageOverReconPercent / 100, "look " + s));
                if (recon && !(s == KnownEnemyHq && HqGroundDue) && KindUsable(MissionKind.Reconnaissance)) jobs.Add(new Job(MissionKind.Reconnaissance, s, null, v, "look " + s));
            }
        }
        // 2. Subdue own systems in uprising
        foreach (var s in HeldSystems())
            if (OwnUprising(s) && Free(MissionKind.SubdueUprising, s)) jobs.Add(new Job(MissionKind.SubdueUprising, s, null, _p.SubdueValue, "subdue " + s));
        // 3. Rescue own prisoners (own characters: the side knows where they are held)
        foreach (var c in _h.Characters.InPlay)
            if (c.Side == _side && c.Status == CharacterStatus.Captured && c.HeldAt != null && !takenChar.Contains(c.Id) && KindUsable(MissionKind.Rescue))
                jobs.Add(new Job(MissionKind.Rescue, c.HeldAt, c.Id, _p.RescueValue * (IsKey(c) ? 2 : 1), "char " + c.Id));
        // 4. Abduction of enemy characters the side has seen
        if (KindUsable(MissionKind.Abduction))
            foreach (var (id, s) in EnemyCharactersSeen())
                if (!takenChar.Contains(id))
                    jobs.Add(new Job(MissionKind.Abduction, s, id, _p.AbductionValue * (_h.Characters.Data.Get(id).Kind == CharacterKind.Major ? 2 : 1), "char " + id));
        // 5. Diplomacy: own systems of low support, neutral systems on the border (picture's owner fresh enough)
        if (KindUsable(MissionKind.Diplomacy))
        {
            var hops = Hops(); var g = _h.Data.Galaxy;
            int stop = Math.Max(1, _p.DiplomacyStopSupport);
            foreach (var s in HeldSystems())
                if (!OwnUprising(s) && SupportSeen(s) is int sup && sup < _p.DiplomacyOwnBelow && Free(MissionKind.Diplomacy, s) && _h.Economy.SlotsOf(s).Populated)
                    jobs.Add(new Job(MissionKind.Diplomacy, s, null, _p.DiplomacyValue * (stop - sup) / stop, "diplomacy " + s));
            foreach (var sys in g.Systems)
            {
                string s = sys.Name;
                if (hops[sys.Index] == 0 || hops[sys.Index] > _p.DiplomacyHops || !Free(MissionKind.Diplomacy, s) || Destroyed(s)) continue;
                if (!OwnerSeen(s, out var owner, out int age) || owner != null || age > _p.MissionOwnerTrustDays) continue;
                if (!SlotsSeen(s).Populated || SupportSeen(s) is not int sup || sup >= stop) continue;
                jobs.Add(new Job(MissionKind.Diplomacy, s, null, _p.DiplomacyValue * (stop - sup) / stop * _p.NeutralDiplomacyPercent / 100, "diplomacy " + s));
            }
        }
        // 6. Alliance subversion: Incite and Sabotage on enemy systems near the border where no defender is believed
        if (_side == Side.Alliance)
        {
            var hops = Hops();
            foreach (var sys in _h.Data.Galaxy.Systems)
            {
                string s = sys.Name;
                if (hops[sys.Index] == 0 || hops[sys.Index] > _p.TargetHops || Destroyed(s)) continue;
                if (!OwnerSeen(s, out var owner, out int age) || owner != Enemy || age > _p.MissionOwnerTrustDays) continue;
                if (MissionDefendersAt(s).Count > 0) continue;
                if (SlotsSeen(s).Populated && Free(MissionKind.InciteUprising, s)) jobs.Add(new Job(MissionKind.InciteUprising, s, null, _p.InciteValue, "subvert " + s));
                int fac = FacilitiesAt(s);
                if (fac > 0 && Free(MissionKind.Sabotage, s)) jobs.Add(new Job(MissionKind.Sabotage, s, null, _p.SabotageValuePerFacility * fac, "subvert " + s));
            }
        }
        // 7. Research: one mission per track, at a held system with a yard of the track's family (m5-research CanResearch)
        foreach (var (kind, track) in new[] { (MissionKind.ShipResearch, ResearchKind.Ship), (MissionKind.TroopResearch, ResearchKind.Troop), (MissionKind.FacilityResearch, ResearchKind.Facility) })
        {
            if (!KindUsable(kind) || own.Any(m => m.Kind == kind)) continue;
            string? site = HeldSystems().FirstOrDefault(s => _h.Research.CanResearch(_side, track, s) == null);
            if (site != null) jobs.Add(new Job(kind, site, null, _p.ResearchValue, "research " + kind));
        }
        return jobs;
    }

    private static bool Repeats(MissionKind k) => k is MissionKind.Diplomacy or MissionKind.SubdueUprising or MissionKind.InciteUprising
        or MissionKind.ShipResearch or MissionKind.TroopResearch or MissionKind.FacilityResearch;

    private static Skill? ResearchSkillOf(MissionKind k) => k switch
    {
        MissionKind.ShipResearch => Characters.Skill.ShipDesign, MissionKind.FacilityResearch => Characters.Skill.FacilityDesign,
        MissionKind.TroopResearch => Characters.Skill.TroopTraining, _ => null,
    };

    /// <summary>
    /// The success chance (percent) of one member, from the core's table for the kind (m5-missions §Mission records: the table input). Recon
    /// succeeds by itself; Incite counts as a success while it works (its incident term, m5-missions §7, needs no roll); Research is the skill.
    /// </summary>
    private int SuccessPercent(CharacterRecord c, Job j)
    {
        var mi = _h.Missions; var rec = mi.Data.Get(j.Kind);
        int Table(int x) => rec.Table == null ? 0 : mi.Lookup(mi.Data.Table(rec.Table), x);
        int sup = SupportSeen(j.System) ?? 50;
        switch (j.Kind)
        {
            case MissionKind.Reconnaissance: case MissionKind.InciteUprising: return 100;
            case MissionKind.Espionage: return Table(Skill(c, Characters.Skill.Espionage));
            case MissionKind.Diplomacy: return Table(sup - (100 - sup) + Skill(c, Characters.Skill.Diplomacy));
            case MissionKind.SubdueUprising: return Table((100 - sup) - sup + Skill(c, Characters.Skill.Leadership));
            case MissionKind.Rescue: return Table(Skill(c, Characters.Skill.Combat));
            case MissionKind.Sabotage:
                {
                    int com = Skill(c, Characters.Skill.Combat), esp = Skill(c, Characters.Skill.Espionage);
                    return Table(mi.Params.SabotageSkill == SabotageSkill.Combat ? com : (com + esp) / 2);
                }
            case MissionKind.Abduction:
                {
                    // the target's combat is not known: its record's base plus half its variance (static character data)
                    var k = _h.Characters.Data.Get(j.TargetCharacter!.Value);
                    int est = k.SkillBase[(int)Characters.Skill.Combat] + k.SkillVariance[(int)Characters.Skill.Combat] / 2;
                    return Table(Skill(c, Characters.Skill.Combat) - est);
                }
            default: return ResearchSkillOf(j.Kind) is Skill rs ? Math.Min(100, Skill(c, rs)) : 0;
        }
    }

    /// <summary>
    /// P(not detected) in basis points (10000 = sure) for a one-member team of this espionage against the defenders the side believes are at
    /// the system: the product over the defenders of (100 - FOILTB(x)) / 100, x = DetectionX (m5-missions §3; no decoys, no special forces).
    /// </summary>
    private int UnseenBasisPoints(int espionage, string system)
    {
        long p = 10000;
        foreach (var d in MissionDefendersAt(system))
        {
            int chance = _h.Missions.FoilChance(_h.Missions.DetectionX(espionage, d.Detection, 0, d.OfficerEspionage));
            p = p * Math.Max(0, 100 - chance) / 100;
            if (p == 0) break;
        }
        return (int)p;
    }

    /// <summary>The planner's expected value of sending the character on the job (null = not allowed or too poor a chance).</summary>
    private long? Expected(CharacterRecord c, Job j)
    {
        var mi = _h.Missions;
        if (!mi.CanDo(c, j.Kind)) return null;
        int floor = j.Kind switch
        {
            MissionKind.Diplomacy => _p.DiplomacyMinSkill,
            MissionKind.SubdueUprising or MissionKind.InciteUprising => _p.LeadershipMinSkill,
            MissionKind.Rescue or MissionKind.Abduction or MissionKind.Sabotage => _p.CombatMinSkill,
            MissionKind.Espionage or MissionKind.Reconnaissance => _p.SpyMinEspionage,
            _ => _p.ResearchMinSkill,
        };
        int skill = j.Kind switch
        {
            MissionKind.Diplomacy => Skill(c, Characters.Skill.Diplomacy),
            MissionKind.SubdueUprising or MissionKind.InciteUprising => Skill(c, Characters.Skill.Leadership),
            MissionKind.Rescue or MissionKind.Abduction or MissionKind.Sabotage => Skill(c, Characters.Skill.Combat),
            MissionKind.Espionage or MissionKind.Reconnaissance => Skill(c, Characters.Skill.Espionage),
            _ => ResearchSkillOf(j.Kind) is Skill rs ? Skill(c, rs) : 0,
        };
        if (skill < floor) return null;
        bool key = IsKey(c);
        var rec = mi.Data.Get(j.Kind);
        int defenders = rec.Foilable ? MissionDefendersAt(j.System).Count : 0;
        if (key && (NoKeyKinds.Contains(j.Kind) || (_h.Characters.Params.MajorsCanDie && defenders > 0))) return null;
        if (_p.LeaderMarks && j.Kind == MissionKind.Diplomacy && c.Id == _h.Characters.Params.Emperor) return null; // B6c §7: the Emperor is never a diplomat
        int unseen = rec.Foilable ? UnseenBasisPoints(Skill(c, Characters.Skill.Espionage), j.System) : 10000;
        int chance = (int)((long)unseen * SuccessPercent(c, j) / 10000); // percent
        if (chance < (Repeats(j.Kind) ? _p.RepeatMinOddsPercent : _p.MissionMinOddsPercent)) return null;
        var n = mi.Data.Numbers;
        int speed = c.Id == n.HanCharacterId ? n.HanMissionSpeed : n.GroupSpeed; // m5-missions §1 group speed (a lone Han flies at 50)
        int ticks = TicksCached(c.System!, j.System, speed);
        if (ticks == int.MaxValue) return null;
        long value = j.Value * (key ? _p.KeyCharacterValuePercent : 100) / 100;
        return value * chance * 100 / (ticks + _p.MissionTravelBias);
    }

    // ================================================================== probe droids on the HQ hunt (HuntDroids)

    /// <summary>The side's completed droids of HuntDroidClass, standing landed on a held system and not in a mission (object id order).</summary>
    private List<EconObject> FreeDroids() => _h.Economy.Objects
        .Where(o => o.Category == ObjectCategory.SpecialForce && o.Side == _side && o.Completed && o.Class == _p.HuntDroidClass && o.AboardShip == 0
                    && o.System != null && Held(o.System) && !_h.Missions.IsSpecialForceBusy(o.Id))
        .OrderBy(o => o.Id).ToList();

    private bool DroidHuntOn => HqHuntOn && KnownEnemyHq == null && KindUsable(MissionKind.Reconnaissance) && _h.Fog.Params.ReconRevealsFacilities;

    /// <summary>
    /// Every free droid goes alone on a Reconnaissance (m5-missions §Orders StartMission: a special force whose bit is in the record's mask is a
    /// team on its own) of an HQ search target of the deeper list (DroidSearchTargets) that no own Espionage or Reconnaissance works yet: best
    /// search score per day of travel first. Targets the characters were sent to today are taken (their orders come first in the queue).
    /// </summary>
    private void PlanDroidLooks()
    {
        if (!DroidHuntOn) return;
        var droids = FreeDroids();
        if (droids.Count == 0) return;
        var taken = new HashSet<string>(OwnMissions().Where(m => m.Kind is MissionKind.Espionage or MissionKind.Reconnaissance).Select(m => m.Target), StringComparer.Ordinal);
        taken.UnionWith(_looksToday);
        var targets = SearchTargets(_p.DroidSearchTargets);
        int speed = _h.Missions.Data.Numbers.GroupSpeed;
        foreach (var d in droids)
        {
            string? best = null; long bestScore = 0;
            foreach (var (s, sc) in targets)
            {
                if (taken.Contains(s)) continue;
                int ticks = TicksCached(d.System!, s, speed);
                if (ticks == int.MaxValue) continue;
                long score = sc * 1000 / (ticks + _p.MissionTravelBias);
                if (score > bestScore) { bestScore = score; best = s; }
            }
            if (best == null) return;
            var target = best; var id = d.Id;
            long seq = _h.Game.Submit(sq => new StartMissionOrder(sq, _side, MissionKind.Reconnaissance, new List<int>(), new List<int> { id }, new List<int>(), target));
            Note(seq, "MissionReconnaissance", $"droid {id} from {d.System} to {target} ev {bestScore}");
            taken.Add(target);
        }
    }

    /// <summary>Production: keep HuntDroids droids (built or queued) while the droid hunt is on, built at the training yard nearest the spy base.</summary>
    private void PlanDroidBuild()
    {
        if (_p.HuntDroids <= 0 || !DroidHuntOn || !Available(_p.HuntDroidClass)) return;
        int have = _h.Economy.Objects.Count(o => o.Category == ObjectCategory.SpecialForce && o.Side == _side && o.Class == _p.HuntDroidClass);
        int want = _p.HuntDroids - have;
        if (want <= 0 || Budget < want * Maint(_p.HuntDroidClass)) return;
        string dest = SpyBase() ?? Capital() ?? "";
        if (dest == "") return;
        var yard = TrainingYardNear(dest);
        if (yard == null) return;
        long seq = _h.Construction.SubmitBuild(_side, yard, YardFamily.Training, _p.HuntDroidClass, want, dest == yard ? null : dest);
        Note(seq, "Build", $"{want} x {_p.HuntDroidClass} at {yard} for {dest} (HQ hunt)");
        _busy.Add((yard, YardFamily.Training));
        _plannedLoad += want * Maint(_p.HuntDroidClass);
    }
}
