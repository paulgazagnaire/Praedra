using Praedra.Core.Characters;
using Praedra.Core.Clock;
using Praedra.Core.Economy;
using Praedra.Core.Fog;
using Praedra.Core.Support;

namespace Praedra.Core.Missions;

/// <summary>Missions in slot 5.3: the daily iterations, Talent sensing, betrayal, mission effects and uprisings (moved unchanged from MissionModule.cs).</summary>
public sealed partial class MissionModule
{
    // ================================================================== slot 5.3: iterations

    // rule: §2 (slot 5.3 of the Step in which K + 1 == due), missions in id order.
    private void Resolve(StepContext ctx)
    {
        int t = ctx.Day + 1;
        foreach (var m in S.Missions.Values.Where(m => m.Phase == MissionPhase.Working && m.Due == t).ToList())
            if (S.Missions.ContainsKey(m.Id) && m.Phase == MissionPhase.Working) Iterate(ctx, m, t);
    }

    private void Iterate(StepContext ctx, MissionGroup m, int t)
    {
        Prune(m);
        // A team emptied by captures elsewhere (the sheet has no rule; DEFAULT ours, listed): the job is dropped and the rest goes home.
        if (!m.HasTeam) { End(ctx, m, MissionOutcome.Cancelled, t); return; }
        // 0. legality
        if (TargetReason(m) != null || KeyGate(m.Kind, m.TargetCharacter) != null) { End(ctx, m, MissionOutcome.TargetInvalid, t); return; } // rule: m5-missions §6a.1
        var rec = Data.Get(m.Kind);
        bool key = IsKeyTarget(m.Kind, m.TargetCharacter, out int keyCap);
        // 1. the job check (phase 9, next to the target): Talent sensing, betrayal, decoys, detection, foiled handling. rule: m5-missions §1, §3
        // (Traced B2b: 0x589970; B2d §1, §2)
        if (RunCheck(ctx, m, t, CheckPoint.Job, rec)) return;
        if (!m.HasTeam) { End(ctx, m, MissionOutcome.Cancelled, t); return; }

        // 2. success rolls, one draw per non-decoy member (characters by id, then special forces by id); Research draws its points right after a success
        bool recon = m.Kind == MissionKind.Reconnaissance;
        var research = ResearchSkill(m.Kind);
        int successes = 0, points = 0;
        if (recon) successes = 1; // automatic success, no draw (DEFAULT, Missing: no table)
        else if (key)
        {
            // rule: m5-missions §6a.3: ONE roll per iteration; input = the best member's (combat minus target combat); chance = min(table, cap_percent).
            var table = Data.Table(rec.Table!);
            var best = Team(m).Select(a => (a, x: Input(m, a))).OrderByDescending(p => p.x).First(); // stable: ties keep the first (characters by id, then special forces)
            int chance = Math.Min(Lookup(table, best.x), keyCap);
            int r = ctx.Rng.NextInt(0, Data.Numbers.RollDie - 1);
            bool ok = r < chance;
            int id = m.Id, member = best.a.Id, input = best.x; var side = m.Side; bool isChar = best.a.IsCharacter;
            ctx.Emit((i, _) => new MissionMemberRoll(i, t, id, side, member, isChar, input, chance, r, ok));
            if (ok) successes = 1;
        }
        else
        {
            var table = rec.Table != null ? Data.Table(rec.Table) : null;
            foreach (var a in Team(m))
            {
                int input = research is Skill rs ? (a.C is { } ac ? _ch.EffectiveSkill(ac, rs) : 0) : Input(m, a);
                int chance = research != null ? input : Lookup(table!, input);
                int r = ctx.Rng.NextInt(0, Data.Numbers.RollDie - 1);
                bool ok = r < chance;
                int id = m.Id, member = a.Id; var side = m.Side; bool isChar = a.IsCharacter;
                ctx.Emit((i, _) => new MissionMemberRoll(i, t, id, side, member, isChar, input, chance, r, ok));
                if (!ok) continue;
                successes++;
                if (research is Skill skill && a.C is { } c) // rule: §6 Research (m5-research: RollMissionPoints right after the roll; +1 skill, GNPRTB 6158)
                {
                    points += _research.RollMissionPoints(ctx);
                    _ch.GainSkill(c, skill, Data.Numbers.ResearchSkillGain);
                    int nv = c.BaseSkill(skill);
                    ctx.Emit((i, _) => new MissionSkillGained(i, t, id, side, member, skill, nv));
                }
            }
        }

        // 3. result and 4. effects
        bool success = successes > 0;
        if (success)
        {
            int effects = recon || research != null || key ? 1 : Params.SuccessRule == SuccessRule.EachMember ? successes : 1;
            int id = m.Id; var side = m.Side; var kind = m.Kind; string target = m.Target;
            ctx.Emit((i, _) => new MissionSucceeded(i, t, id, side, kind, target, research != null ? successes : effects));
            if (research != null) { if (points > 0) _research.AddMissionPoints(ctx, m.Side, ResearchTrack(m.Kind)!.Value, points); }
            else for (int e = 0; e < effects; e++) Effect(ctx, m, t);
        }
        m.Iterations++;

        // 5. after the iteration
        if (rec.Repeats)
        {
            if (!S.Missions.ContainsKey(m.Id) || m.Phase != MissionPhase.Working) return;
            m.Due += Timer(ctx, rec);
            int id = m.Id, due = m.Due; var side = m.Side; string target = m.Target;
            ctx.Emit((i, _) => new MissionIterationDone(i, t, id, side, target, success, due));
            return;
        }
        if (success) { End(ctx, m, MissionOutcome.Succeeded, t); return; }
        {
            int id = m.Id; var side = m.Side; var kind = m.Kind; string target = m.Target;
            ctx.Emit((i, _) => new MissionFailed(i, t, id, side, kind, target));
        }
        Expose(ctx, m, ExposureCause.Failed);
        End(ctx, m, MissionOutcome.Failed, t);
    }

    /// <summary>
    /// The table input of one member. rule: §Mission records (skill fed to the table), §Mission records note on support: "support" is the mission
    /// side's share at the target, "rival support" 100 - it, "controller support" the holder's.
    /// </summary>
    private int Input(MissionGroup m, Agent a)
    {
        int Support() => _eco.SupportOf(m.Target, m.Side);
        int TargetCombat() => m.TargetCharacter is int tid && _ch.Get(tid) is { } tc ? _ch.EffectiveSkill(tc, Skill.Combat) : 0;
        switch (m.Kind)
        {
            case MissionKind.Diplomacy: { int s = Support(); return (s - (100 - s)) + (a.C is { } ac ? _ch.EffectiveSkill(ac, Skill.Diplomacy) : 0); }
            case MissionKind.Rescue: return a.Combat;
            case MissionKind.Sabotage: return SabotageSkillOf(a.Combat, a.Espionage);
            case MissionKind.Espionage: return a.Espionage;
            case MissionKind.Recruitment: return a.Leadership - Params.RecruitSystemTerm;
            case MissionKind.Abduction or MissionKind.Assassination: return a.Combat - TargetCombat();
            case MissionKind.InciteUprising:
                if (Params.InciteInputMode == InciteInputMode.Documented) { int s = Support(); return (a.Leadership - s) - (100 - s); }
                return a.Leadership - (_fleets.OwnerOf(m.Target) is Side h ? _eco.SupportOf(m.Target, h) : 0);
            case MissionKind.SpikeTheHarvester: return (a.Combat + a.Espionage) / 2;
            case MissionKind.SubdueUprising: { int s = Support(); return ((100 - s) - s) + a.Leadership; }
            default: return 0;
        }
    }

    /// <summary>
    /// One mission check at a check point (0x5898f0): skipped when MISSNSD word 14 is 0 or the point's kind is 0; run at the origin for phases 2 and 3,
    /// at the target for 7 and 9 (0x520ad0). Kinds 1, 2 and 4 act in pass 1 only; kind 3 is orbit in pass 1 and planet in pass 2. Returns true when the
    /// check ended the mission. rule: m5-missions §1 (Traced B2d §1, §2).
    /// </summary>
    private bool RunCheck(StepContext ctx, MissionGroup m, int t, CheckPoint point, MissionRecord rec)
    {
        if (!rec.GuardChecks) return false;
        var kind = KindAt(KindWord(m.Kind), point);
        if (kind == CheckKind.None) return false;
        bool atOrigin = point <= CheckPoint.LeaveOrbit;
        string place = atOrigin ? m.Home : m.Target;
        foreach (var k in kind == CheckKind.OrbitThenPlanet ? new[] { CheckKind.Orbit, CheckKind.Planet } : new[] { kind })
            if (CheckPass(ctx, m, t, point, k, place, atOrigin, rec)) return true;
        return false;
    }

    /// <summary>
    /// One pass (0x589970): scopes, Talent sensing, betrayal (the job check only: 0x592500), then if not foiled the decoy pass and the detection walk
    /// over the guards in scope, then the foiled handling. rule: §3, §3a, §4 (Traced B2b, B2d).
    /// </summary>
    private bool CheckPass(StepContext ctx, MissionGroup m, int t, CheckPoint point, CheckKind kind, string place, bool atOrigin, MissionRecord rec)
    {
        var sc = ScopeOf(m, kind, place);
        var guards = CheckGuards(m, kind, place, sc); // rule: §6a (Key-target protections, 2026-10-18: KeyTargetGuards)
        var foil = Sense(ctx, m, t, place, sc);
        if (foil == null && point == CheckPoint.Job && BetrayalRuns(rec)) foil = Betray(ctx, m, t);
        var decoyed = new HashSet<int>(); // guard ids; lookup only, never iterated
        if (foil == null && DecoyAndWalk(ctx, m, t, guards, sc, atOrigin, decoyed, place, rec) is { } d) foil = new FoilBy(MissionFoilCause.Detected, d, 0);
        if (foil == null) return false;
        return Foiled(ctx, m, t, foil, point, guards, decoyed, sc, atOrigin, place, rec);
    }

    /// <summary>
    /// The member counts for a check (0x5883b0): a member standing on the planet counts when the planet-members flag is on, one aboard (or, at the
    /// target, belonging to the mission) when the orbit-members flag is on. Our groups have no aboard state: at the origin a character attached to a
    /// fleet or a special force aboard a ship is "orbit", the others "planet"; at the target every member is "orbit". rule: §3 (B2d §3, Work 3).
    /// </summary>
    private bool Counts(Agent a, bool atOrigin, Scope sc)
    {
        bool orbit = !atOrigin || (a.IsCharacter ? a.C!.AttachedFleet != 0 : E.Objects.TryGetValue(a.Id, out var o) && o.AboardShip != 0);
        return orbit ? sc.OrbitMembers : sc.PlanetMembers;
    }

    /// <summary>
    /// The decoy pass then the detection walk; returns the guard that detected the team, or null. rule: §3 (Traced, B2a: 0x58a020, 0x58a130; B2d §6).
    /// Decoy pass, per guard in order: pick one counted decoy k = rand(0..n-1) (no draw when n = 1; none left, no draw), one roll against the decoy
    /// table; a decoy that distracted a guard stays in the pick (0x587b30); a failed decoy fights the escape duel against that guard right after its
    /// roll and leaves the group whatever the result (0x588b90 -> 0x5888f0), so the next guard cannot pick it. Walk: one draw per guard not
    /// decoyed, up to and including the first detection; no walk (no draw) when no counted non-decoy member is left (0x5896e0). No guards: no draw.
    /// </summary>
    private Defender? DecoyAndWalk(StepContext ctx, MissionGroup m, int t, List<Defender> guards, Scope sc, bool atOrigin, HashSet<int> decoyed,
        string place, MissionRecord rec)
    {
        // rule: B2e §4 (0x587fa0 -> 0x5892a0): before the decoy pass, each decoy with a mutual Talent discovery meets that talented character
        foreach (var a in Decoys(m).Where(a => Counts(a, atOrigin, sc)).ToList()) Encounter(ctx, m, t, a, place, rec, decoy: true);
        if (guards.Count == 0) return null;
        var decoys = Decoys(m).Where(a => Counts(a, atOrigin, sc)).ToList();
        if (decoys.Count > 0 && Data.DecoyTable == null) throw new RuleMissingException("DecoyTable", "m5-missions");
        int id = m.Id; var side = m.Side;
        foreach (var d in guards)
        {
            if (decoys.Count == 0) break;
            // rule: §3 decoy pass step 1 (0x5f5700 returns 0 without a draw for a bound <= 1)
            var dc = decoys.Count == 1 ? decoys[0] : decoys[ctx.Rng.NextInt(0, decoys.Count - 1)];
            int x = DecoyX(KeyEsp(m, dc), d.Detection, d.OfficerEspionage), p = Lookup(Data.DecoyTable!, x);
            int r = ctx.Rng.NextInt(0, Data.Numbers.RollDie - 1);
            bool ok = r < p; int def = d.Unit.Id, did = dc.Id;
            ctx.Emit((i, _) => new MissionDecoyRoll(i, t, id, side, def, did, x, p, r, ok));
            if (ok) { decoyed.Add(d.Unit.Id); continue; }
            Duel(ctx, m, t, dc, d, place, rec, decoy: true); // rule: B2d §6 (failed decoy)
            decoys.Remove(dc);
        }
        var team = Team(m).Where(a => Counts(a, atOrigin, sc)).ToList();
        if (team.Count == 0) return null; // rule: §3 detection walk (0x5896e0: count 0 skips the walk; Traced B2a)
        int avg = team.Sum(a => KeyEsp(m, a)) / team.Count; // C integer division (0x5887a0 idiv; Traced B2a); §6a KeyTargetBaseSkills
        int sfs = team.Count(a => !a.IsCharacter);
        foreach (var d in guards)
        {
            if (decoyed.Contains(d.Unit.Id)) continue;
            int x = DetectionX(avg, d.Detection, sfs, d.OfficerEspionage), p = FoilChance(x);
            int r = ctx.Rng.NextInt(0, Data.Numbers.RollDie - 1);
            bool hit = r < p;
            int def = d.Unit.Id;
            ctx.Emit((i, _) => new MissionDetectionRoll(i, t, id, side, def, x, p, r, hit));
            if (hit) return d;
        }
        return null;
    }

    /// <summary>
    /// The escape duel (0x5888f0): x = member combat - combat of the guard's commander (0 when none); one draw, escaped if rand(0..99) &lt; RLEVADTB(x)
    /// (the common step lookup, the first row below it). See <see cref="DuelWith"/> for what follows. rule: §4 (Traced B2d §5).
    /// </summary>
    // The captor unit of a guard duel is the guard's commander, else the guard itself (B2d §5 step 4, corrected 2026-10-18): a ship's Admiral rides
    // its fleet and a regiment's General the fleet it is aboard, so both put the prisoner aboard that fleet; a special force on the planet keeps
    // him on the planet. rule: §4 (QA-7).
    private void Duel(StepContext ctx, MissionGroup m, int t, Agent a, Defender g, string place, MissionRecord rec, bool decoy) =>
        DuelWith(ctx, m, t, a, g.OfficerCombat, g.Unit.Id, g.Unit.Side ?? Other(m.Side), place, rec, decoy, new CaptorUnit(g.Fleet, place, 0, MissionInvolved: true, Ship: g.Ship));

    /// <summary>
    /// The duel against a captor of combat <paramref name="captorCombat"/> (a guard's commander, or the enemy talented character of an encounter). Escaped:
    /// the member leaves the group if its record lets it resign (MISSNSD word 15), then a character rolls Hurt (0x4ef450). Caught: a character rolls
    /// Hurt and, if he did not die of it, is captured by <paramref name="captor"/> at the place of the check (0x4ef480; Capture refuses an own-side
    /// captor: the member just leaves; a key character under KeyDuelCapture.Capped is captured only if his cap roll passes, see
    /// <see cref="KeyDuelCaptureRoll"/>); a special force is destroyed (UnitDestroyed, reason MissionDuel), no Hurt. A member removed leaves the
    /// group. Returns (escaped, the member's injury went up). rule: §4 (Traced B2d §5; B2e §2 "Who calls Hurt", §8).
    /// </summary>
    private (bool Escaped, bool Hurt) DuelWith(StepContext ctx, MissionGroup m, int t, Agent a, int captorCombat, int guard, Side captor, string place,
        MissionRecord rec, bool decoy, CaptorUnit unit)
    {
        var table = Data.Evade ?? throw new RuleMissingException(MissionData.EvadeTable, "m5-missions");
        int x = a.Combat - captorCombat;
        int p = Lookup(table, x, MstbLookupMode.Step, MstbBelowFirstRow.FirstRowValue);
        int r = ctx.Rng.NextInt(0, Data.Numbers.RollDie - 1);
        bool escaped = r < p;
        int id = m.Id, member = a.Id; var side = m.Side; bool isChar = a.IsCharacter;
        ctx.Emit((i, _) => new MissionDuel(i, t, id, side, place, member, isChar, decoy, guard, x, p, r, escaped));
        bool removed = false, hurt = false;
        if (escaped)
        {
            if (rec.Foilable) Leave(m, a, place, false); // rule: B2d §5 step 5 (resign when word 15 is set)
            if (isChar) hurt = _ch.Hurt(ctx, a.C!, CauseMissionDuel) > 0; // rule: B2e §2 (0x4ef450: resign, then Hurt)
            return (true, hurt);
        }
        if (isChar)
        {
            hurt = _ch.Hurt(ctx, a.C!, CauseMissionDuel) > 0; // rule: B2e §2 (0x4ef480: Hurt, then captured if alive)
            removed = a.C!.Status == CharacterStatus.Dead
                || (KeyDuelCaptureRoll(ctx, m, t, a.C!, captor, place) && _ch.Capture(ctx, a.C!, captor, place, CauseMissionDuel, unit: unit)); // QA-7: moved to the captor
        }
        else if (E.Objects.TryGetValue(a.Id, out var o))
        {
            ctx.Emit((i, _) => new Battle.UnitDestroyed(i, t, place, member, 0, Battle.UnitLossReason.MissionDuel));
            _eco.DestroyUnit(ctx, t, o);
            removed = true;
        }
        if (removed || rec.Foilable) Leave(m, a, place, removed);
        return (false, hurt);
    }

    /// <summary>
    /// A duel loser who survived his Hurt roll: may the capture go ahead? Under <see cref="KeyDuelCapture.Capped"/> (DEFAULT) with
    /// <see cref="MissionParams.KeyTargetRules"/> on, a character listed in the key-target file whom the capture would take (Free or Injured, the
    /// captor of the other side) makes one extra roll <c>rand(0..99) &lt; cap_percent</c>: captured only if it passes; otherwise he is not captured
    /// and goes on as a loser whom Capture refuses (he keeps the duel's Hurt, no second injury; he leaves the group if word 15 lets him). Every
    /// other character, and <see cref="KeyDuelCapture.Classic"/>, keep the Classic duel (no draw). rule: m5-missions §4, §6a (KeyDuelCapture,
    /// 2026-10-18; Paul's caps apply to duel captures).
    /// </summary>
    private bool KeyDuelCaptureRoll(StepContext ctx, MissionGroup m, int t, CharacterRecord c, Side captor, string place)
    {
        if (Params.KeyDuelCapture != KeyDuelCapture.Capped || !Params.KeyTargetRules || !Data.KeyTargets.TryGetValue(c.Id, out int cap)) return true;
        if (!c.Placed || c.Status is not (CharacterStatus.Free or CharacterStatus.Injured) || captor == c.Side) return true; // Capture refuses: no draw
        int r = ctx.Rng.NextInt(0, Data.Numbers.RollDie - 1);
        bool ok = r < cap;
        int id = m.Id, member = c.Id; var side = m.Side;
        ctx.Emit((i, _) => new MissionKeyDuelCaptureRoll(i, t, id, side, place, member, captor, cap, r, ok));
        return ok;
    }

    /// <summary>
    /// The Talent encounter in place of a guard duel (0x5892a0 -> 0x588d30 -> 0x560200): a character member whose discovered talented character names
    /// him back with the same pair id duels that character (his location is not checked) and the character module does the rest (hurt rolls,
    /// combat gain, keys cleared, Talent effects). Returns false, with no draw, when the precondition fails. rule: §4 (Traced B2e §4).
    /// </summary>
    private bool Encounter(StepContext ctx, MissionGroup m, int t, Agent a, string place, MissionRecord rec, bool decoy)
    {
        if (!a.IsCharacter || !_ch.MutualDiscovery(a.C!, out var x) || x == null) return false;
        int xm = MissionOf(x.Id); // QA-7: the captor is X himself: his fleet, else where he stands; his mission takes the prisoner as a captive
        var unit = new CaptorUnit(x.AttachedFleet, _ch.LocationOf(x), xm, MissionInvolved: true);
        var (escaped, hurt) = DuelWith(ctx, m, t, a, _ch.EffectiveSkill(x, Skill.Combat), x.Id, x.Side, place, rec, decoy, unit);
        _ch.TalentEncounter(ctx, t, m.Id, a.C!, x, hurt, escaped);
        return true;
    }

    /// <summary>
    /// A member's injury went up (the character module's InjuredResign): if his mission's record lets him resign (MISSNSD word 15), he leaves the
    /// group and stands where it is (MissionResignRequest, 0x534640). rule: m5-missions §4 (Traced B2e §2).
    /// </summary>
    public void OnInjured(StepContext? ctx, CharacterRecord c)
    {
        foreach (var m in S.Missions.Values)
        {
            if (!m.Characters.Contains(c.Id) && !m.DecoyCharacters.Contains(c.Id)) continue;
            if (!Data.Get(m.Kind).Foilable) return;
            m.Characters.Remove(c.Id); m.DecoyCharacters.Remove(c.Id);
            c.System ??= m.Phase == MissionPhase.Outbound && !m.Departed ? m.Home : m.Target;
            return;
        }
    }

    // The member leaves the group and, unless captured or destroyed, stands free at the place of the check.
    private void Leave(MissionGroup m, Agent a, string place, bool removed)
    {
        if (a.IsCharacter) { m.Characters.Remove(a.Id); m.DecoyCharacters.Remove(a.Id); if (!removed && a.C != null) a.C.System = place; }
        else { m.SpecialForces.Remove(a.Id); m.DecoySpecialForces.Remove(a.Id); if (!removed && E.Objects.TryGetValue(a.Id, out var o)) o.System = place; }
    }

    /// <summary>
    /// The foiled handling (0x58a1c0): if the record's word 15 is set the mission ends with code 3 (caught at the origin) or 4 (at the target), event
    /// MissionFoiled; then each counted non-decoy member, characters first, then special forces, picks one guard among those in scope and not decoyed
    /// (k = rand(0..n-1), no draw when n = 1; no guard left, no duel) and fights the escape duel. With word 15 = 0 the mission goes on. A member
    /// with a mutual Talent discovery meets that talented character instead (no guard pick; B2e §4). rule: §4 (Traced B2d §6, B2e §4).
    /// </summary>
    private bool Foiled(StepContext ctx, MissionGroup m, int t, FoilBy f, CheckPoint point, List<Defender> guards, HashSet<int> decoyed, Scope sc,
        bool atOrigin, string place, MissionRecord rec)
    {
        int code = point <= CheckPoint.LeaveOrbit ? FoilCodeAtOrigin : FoilCodeAtTarget;
        if (rec.Foilable)
        {
            int id = m.Id, def = f.Defender?.Unit.Id ?? 0, who = f.Character; var side = m.Side; string cls = f.Defender?.Unit.Class ?? "";
            var cause = f.Cause;
            ctx.Emit((i, _) => new MissionFoiled(i, t, id, side, place, def, cls, cause, who, code));
            _fog.OnMissionDetected(ctx, f.Defender?.Unit.Side ?? Other(m.Side), place);
        }
        var pool = guards.Where(g => !decoyed.Contains(g.Unit.Id)).ToList();
        foreach (var a in Team(m).Where(a => Counts(a, atOrigin, sc)).ToList())
        {
            if (Encounter(ctx, m, t, a, place, rec, decoy: false)) continue; // rule: B2e §4 (0x589360: a mutual discovery meets his talented character)
            if (pool.Count == 0) continue;
            var g = pool.Count == 1 ? pool[0] : pool[ctx.Rng.NextInt(0, pool.Count - 1)]; // 0x588650
            Duel(ctx, m, t, a, g, place, rec, decoy: false);
        }
        if (!rec.Foilable) return false;
        if (atOrigin) EndAtOrigin(ctx, m, t);
        else End(ctx, m, MissionOutcome.Foiled, t);
        return true;
    }

    /// <summary>The end codes of a foiled mission: 3 + (furthest phase &gt; 4). rule: §4 (Traced B2d §6, 0x58a1c0).</summary>
    public const int FoilCodeAtOrigin = 3, FoilCodeAtTarget = 4;

    // A mission foiled before it left: MissionEnded, then whoever is left of the group stands free at home (no trip). rule: §5 (B2d: code 3).
    private void EndAtOrigin(StepContext ctx, MissionGroup m, int t)
    {
        m.Outcome = MissionOutcome.Foiled;
        int id = m.Id; var side = m.Side; var kind = m.Kind; string target = m.Target;
        ctx.Emit((i, _) => new MissionEnded(i, t, id, side, kind, target, MissionOutcome.Foiled));
        Prune(m);
        if (Empty(m)) RemoveGroup(ctx, m, m.Home);
        else Release(ctx, m, m.Home, t);
    }

    /// <summary>The job check's decoy pass and walk on a group as it stands (tests: B2a-4 hands it a group whose team is gone).</summary>
    internal Defender? Walk(StepContext ctx, MissionGroup m, int t)
    {
        var sc = ScopeOf(m, CheckKind.NextToTarget, m.Target);
        return DecoyAndWalk(ctx, m, t, CheckGuards(m, CheckKind.NextToTarget, m.Target, sc), sc, false, new HashSet<int>(), m.Target, Data.Get(m.Kind));
    }
    /// <summary>What foiled the mission: the detecting defender (Detected), or the senser or traitor (Character).</summary>
    private sealed record FoilBy(MissionFoilCause Cause, Defender? Defender, int Character);

    // ================================================================== Talent sensing (B2b)

    /// <summary>
    /// <c>p = Talent(member) + Talent(senser) + GNPRTB 3587</c>, 0 when either Talent is 0. A draw is made only when p &gt; 0. rule: §3 Talent sensing
    /// step 3 (Traced B2b: 0x55e4d0).
    /// </summary>
    public int SenseChance(int memberTalent, int senserTalent) =>
        memberTalent == 0 || senserTalent == 0 ? 0 : memberTalent + senserTalent + Params.SenseConstant;

    /// <summary>
    /// The sensers of a check: characters of the other side, placed and not captured or dead, with Talent at least GNPRTB 3586, standing at the place
    /// of the check, by id. TalentSenseScope CheckScope (DEFAULT): on the planet when the pass guards the planet, aboard a fleet there when it guards
    /// the orbit (B2b §2 "Where" with the scopes of B2d §3); Both, Planet, Fleets ignore the pass's scopes. rule: §3 Talent sensing, Sensers
    /// (Traced B2b: 0x588840).
    /// </summary>
    public List<CharacterRecord> Sensers(MissionGroup m, string place, Scope sc) =>
        _ch.InPlay.Where(c => c.Side != m.Side && c.Status is not (CharacterStatus.Captured or CharacterStatus.Dead)
            && (c.TalentHolder ? c.EffectiveTalent : 0) >= Params.SenserMinTalent && _ch.LocationOf(c) == place
            && Params.TalentSenseScope switch
            {
                TalentSenseScope.CheckScope => c.AttachedFleet == 0 ? sc.PlanetGuards : sc.OrbitGuards,
                TalentSenseScope.Planet => c.AttachedFleet == 0, TalentSenseScope.Fleets => c.AttachedFleet != 0, _ => true,
            })
        .ToList();

    /// <summary>The sensers of the job check at the mission's target. rule: §3 Talent sensing.</summary>
    public List<CharacterRecord> Sensers(MissionGroup m) => Sensers(m, m.Target, ScopeOf(m, CheckKind.NextToTarget, m.Target));

    // Nobody first, then Ravilla, then the rest one at a time, each picked k = rand(0..n-1) among those left (no draw when one is left). Lazy: a pick
    // is drawn only when the next one is taken. rule: §3 Talent sensing steps 1 and 2 (Traced B2b: 0x58a9e0, 0x58ac50, 0x58aca0).
    private IEnumerable<T> SenseOrder<T>(StepContext ctx, List<T> items, Func<T, int> id)
    {
        var rest = new List<T>(items);
        foreach (int first in new[] { Params.SenseFirstCharacter, Params.SenseSecondCharacter })
        {
            int at = rest.FindIndex(x => id(x) == first);
            if (at < 0) continue;
            var x = rest[at]; rest.RemoveAt(at);
            yield return x;
        }
        while (rest.Count > 0)
        {
            int k = rest.Count == 1 ? 0 : ctx.Rng.NextInt(0, rest.Count - 1);
            var x = rest[k]; rest.RemoveAt(k);
            yield return x;
        }
    }

    /// <summary>
    /// Talent sensing (0x589e40): for each senser in order, the group's talented characters (team and decoys, Talent at least GNPRTB 3585) in order, one draw
    /// per pair with p &gt; 0. A hit on a team member foils the mission (a sensed decoy foils nothing); every hit gives the discovered-talented
    /// notice. A senser takes members until his own discovered talented character is set, so he stops at his first hit, and one who already has one gets a
    /// single try per check. Every senser is processed; sensing never stops at a foil. Returns the first foiling hit, or null. rule: §3 Talent sensing
    /// (Traced B2b).
    /// </summary>
    private FoilBy? Sense(StepContext ctx, MissionGroup m, int t, string place, Scope sc)
    {
        if (!Params.TalentSensing) return null;
        var members = Team(m).Select(a => (a, decoy: false)).Concat(Decoys(m).Select(a => (a, decoy: true)))
            .Where(x => x.a.IsCharacter && x.a.Talent >= Params.SensedMinTalent && x.a.C!.Status != CharacterStatus.Captured).ToList();
        if (members.Count == 0) return null;
        var sensers = Sensers(m, place, sc);
        FoilBy? foil = null;
        int id = m.Id; var side = m.Side; string target = m.Target;
        foreach (var s in SenseOrder(ctx, sensers, c => c.Id))
        {
            foreach (var (a, decoy) in SenseOrder(ctx, members, x => x.a.Id))
            {
                int p = SenseChance(a.Talent, s.TalentHolder ? s.EffectiveTalent : 0);
                if (p > 0)
                {
                    int r = ctx.Rng.NextInt(0, Data.Numbers.RollDie - 1);
                    bool hit = r < p;
                    int mid = a.Id, sid = s.Id;
                    ctx.Emit((i, _) => new MissionTalentSenseRoll(i, t, id, side, target, mid, sid, p, r, hit));
                    if (hit)
                    {
                        bool foils = !decoy;
                        if (foils && foil == null) foil = new FoilBy(MissionFoilCause.TalentSensed, null, s.Id);
                        ctx.Emit((i, _) => new MissionTalentSensed(i, t, id, side, target, mid, decoy, sid, foils));
                        DiscoverEachOther(ctx, m, a.C!, s, t);
                    }
                }
                if (s.DiscoveredTalentHolder != 0) break; // rule: §3 Talent sensing step 5 (0x58a370)
            }
        }
        return foil;
    }

    // rule: §3 Talent sensing step 4 (0x55ff60 -> 0x4ee500): only if neither already has a discovered talented character (B2e §8 corrects B2b's "unless
    // both"), each records the other, both get the same new pair id (the game-wide counter, B2e §4) and both sides are told.
    private void DiscoverEachOther(StepContext ctx, MissionGroup m, CharacterRecord member, CharacterRecord senser, int t)
    {
        if (member.DiscoveredTalentHolder != 0 || senser.DiscoveredTalentHolder != 0) return;
        member.DiscoveredTalentHolder = senser.Id; senser.DiscoveredTalentHolder = member.Id;
        member.PairId = senser.PairId = _ch.NewPairId();
        int id = m.Id, mid = member.Id, sid = senser.Id; var ms = member.Side; var ss = senser.Side;
        ctx.Emit((i, _) => new TalentHolderDiscovered(i, t, id, mid, ms, sid, ss));
    }

    // ================================================================== betrayal (B2b)

    // rule: §3 Betrayal (the mission's own test, vtable +0x1c8, not read: BetrayalKinds DEFAULT Foilable).
    private bool BetrayalRuns(MissionRecord rec) => Params.Betrayal && (Params.BetrayalKinds == BetrayalKinds.All || rec.Foilable);

    /// <summary>
    /// Betrayal (0x589f10): every member, team then decoys (B2a's order), draws once even at chance 0: betrays if rand(0..99) &lt; 100 - loyalty; the
    /// walk stops at the first traitor, who foils the mission. Then every other member draws once even at Talent 0: identifies the traitor if
    /// rand(0..99) &lt; his Talent; that walk stops at the first identification. rule: §3 Betrayal (Traced B2b: 0x55e520, 0x55e550, 0x4ee5c0).
    /// </summary>
    private FoilBy? Betray(StepContext ctx, MissionGroup m, int t)
    {
        var all = Team(m).Concat(Decoys(m)).ToList();
        int id = m.Id; var side = m.Side; string target = m.Target;
        Agent? traitor = null;
        foreach (var a in all)
        {
            int p = Data.Numbers.RollDie - a.Loyalty, r = ctx.Rng.NextInt(0, Data.Numbers.RollDie - 1);
            bool betrays = r < p;
            int mid = a.Id; bool isChar = a.IsCharacter;
            ctx.Emit((i, _) => new MissionBetrayalRoll(i, t, id, side, target, mid, isChar, p, r, betrays));
            if (betrays) { traitor = a; break; }
        }
        if (traitor is not { } tr) return null;
        {
            int mid = tr.Id; bool isChar = tr.IsCharacter;
            ctx.Emit((i, _) => new MissionBetrayed(i, t, id, side, target, mid, isChar));
        }
        foreach (var o in all)
        {
            if (o.IsCharacter == tr.IsCharacter && o.Id == tr.Id) continue;
            int r = ctx.Rng.NextInt(0, Data.Numbers.RollDie - 1);
            if (r >= o.Talent) continue;
            if (o.C != null && tr.IsCharacter) o.C.IdentifiedTraitor = tr.Id;
            int oid = o.Id, tid = tr.Id; bool tc = tr.IsCharacter;
            ctx.Emit((i, _) => new TraitorIdentified(i, t, id, oid, tid, tc, side));
            break;
        }
        return new FoilBy(MissionFoilCause.Betrayed, null, tr.Id);
    }

    // rule: §4: OnTeamExposed(member, otherSide, Failed) for every character team member in ascending id (switch ExposureHooks; decoys never). A
    // foiled team fights the escape duel instead (B2d §6).
    private void Expose(StepContext ctx, MissionGroup m, ExposureCause cause)
    {
        if (!Params.ExposureHooks || OnTeamExposed == null) return;
        foreach (var id in m.Characters.ToList())
            if (_ch.Get(id) is { } c) OnTeamExposed(ctx, c, Other(m.Side), cause);
    }

    // rule: §5: MissionEnded; the rest of the group becomes the Return group (it leaves in the next step 2; a Cancel's group leaves in this Step's step 2).
    private void End(StepContext ctx, MissionGroup m, MissionOutcome outcome, int day)
    {
        m.Outcome = outcome; m.Phase = MissionPhase.ReturnPending; m.Departed = false;
        int id = m.Id; var side = m.Side; var kind = m.Kind; string target = m.Target;
        ctx.Emit((i, _) => new MissionEnded(i, day, id, side, kind, target, outcome));
        Prune(m);
        if (Empty(m)) RemoveGroup(ctx, m, m.Target);
    }

    // ================================================================== effects (§6), applied in slot 5.3

    private void Effect(StepContext ctx, MissionGroup m, int t)
    {
        var n = Data.Numbers;
        int id = m.Id; var side = m.Side; string target = m.Target;
        CharacterRecord? tc = m.TargetCharacter is int tid ? _ch.Get(tid) : null;
        switch (m.Kind)
        {
            case MissionKind.Diplomacy: // own-held +1..+20, neutral +1..+10, for the mission side
                {
                    bool own = _fleets.OwnerOf(target) == side;
                    int lo = own ? n.DiplomacyOwnLow : n.DiplomacyNeutralLow, hi = own ? n.DiplomacyOwnHigh : n.DiplomacyNeutralHigh;
                    _support.ChangeSupport(ctx, target, side, lo + ctx.Rng.NextInt(0, hi - lo), SupportCause.Diplomacy);
                    break;
                }
            case MissionKind.SubdueUprising:
                _support.ChangeSupport(ctx, target, side, n.SubdueLow + ctx.Rng.NextInt(0, n.SubdueHigh - n.SubdueLow), SupportCause.Subdue);
                if (Params.SubdueEndsUprising) _support.EndUprisingNow(ctx, target, CauseSubdue);
                break;
            case MissionKind.InciteUprising:
                if (Params.InciteSupportTiming == InciteSupportTiming.PerIteration && _fleets.OwnerOf(target) is Side holder)
                    _support.ChangeSupport(ctx, target, holder, n.InciteSupport, SupportCause.IncitedUprising);
                break;
            case MissionKind.Sabotage:
                {
                    EconObject? victim = null;
                    if (Params.SabotageTargetMode == SabotageTargetMode.Chosen)
                    {
                        if (m.Facility is int fid && E.Objects.TryGetValue(fid, out var f) && f.Completed && f.System == target) victim = f;
                    }
                    else
                    {
                        var facs = E.Objects.Values.Where(o => o.Category == ObjectCategory.Facility && o.Completed && o.System == target).ToList(); // id order
                        if (facs.Count > 0) victim = facs[ctx.Rng.NextInt(0, facs.Count - 1)];
                    }
                    if (victim != null) _eco.Destroy(ctx, t, victim, DestroyReason.Sabotage);
                    else ctx.Emit((i, _) => new SabotageNoTarget(i, t, id, side, target));
                    break;
                }
            case MissionKind.Espionage:
                _fog.EspionageSuccess(ctx, side, target);
                ctx.Emit((i, _) => new SystemRevealed(i, t, id, side, target, MissionKind.Espionage));
                break;
            case MissionKind.Reconnaissance:
                _fog.Reveal(ctx, side, target, RevealKind.Recon);
                ctx.Emit((i, _) => new SystemRevealed(i, t, id, side, target, MissionKind.Reconnaissance));
                break;
            case MissionKind.Recruitment:
                // rule: §6 Recruitment (Recruitment pick, 2026-10-18): the pick is the setup extras pick at the target system; the recruit is recorded on the mission
                if (Recruit?.Invoke(ctx, side, target) is { } rc) { int cid = rc.Id; m.RecruitedCharacterKey = cid; ctx.Emit((i, _) => new CharacterRecruited(i, t, id, side, cid, target)); }
                break;
            case MissionKind.Rescue: if (tc != null) _ch.Free(ctx, tc, CauseRescue); break;
            case MissionKind.Abduction: if (tc != null) _ch.Capture(ctx, tc, side, null, CauseAbduction, unit: TeamCaptor(m)); break; // QA-7: the team's captive
            case MissionKind.Assassination:
                if (tc == null) break;
                if (!IsKeyTarget(m.Kind, m.TargetCharacter, out _)) { _ch.Kill(ctx, tc, CauseAssassination); break; }
                // rule: m5-missions §6a.4: a qualified team (one character with combat and espionage both at the minimum) draws k: kidnap or kill
                // (death allowed for this target whatever MajorsCanDie says); an unqualified team injures the target, no k draw.
                // (Key-target protections, 2026-10-18: KeyTargetBaseSkills reads the skills without Talent term)
                if (!Team(m).Any(a => a.IsCharacter && KeyCom(m, a) >= Params.KeyTeamMinCombat && KeyEsp(m, a) >= Params.KeyTeamMinEspionage))
                    _ch.Injure(ctx, tc, CauseAssassination);
                else if (ctx.Rng.NextInt(0, Data.Numbers.RollDie - 1) < Params.KeyKidnapPercent) _ch.Capture(ctx, tc, side, null, CauseKidnap, unit: TeamCaptor(m));
                else _ch.Kill(ctx, tc, CauseAssassination, allowDeath: true);
                break;
            case MissionKind.SpikeTheHarvester: DestroyHarvester?.Invoke(ctx, target, Other(side)); break;
        }
    }

    // The team as captor (Abduction, the kidnap): a member on this mission at the target. rule: §6 (B2e §3 step 4, B2e-22; QA-7).
    private static CaptorUnit TeamCaptor(MissionGroup m) => new(0, m.Target, m.Id, true);

    // ================================================================== uprisings (§7) and per-incident Incite (§6)

    /// <summary>incite_term + subdue_term of the incident score: ± sum of (member leadership / 10) over the non-decoy members of working Incite and Subdue missions. rule: §7.</summary>
    public int IncidentTerm(string system) => InciteTerm(system) + SubdueTerm(system);

    public int InciteTerm(string system) => AgentTerm(system, MissionKind.InciteUprising);
    public int SubdueTerm(string system) => -AgentTerm(system, MissionKind.SubdueUprising);

    private int AgentTerm(string system, MissionKind kind)
    {
        int sum = 0, div = Data.Numbers.AgentLeadershipDivisor;
        foreach (var m in S.Missions.Values)
            if (m.Kind == kind && m.Phase == MissionPhase.Working && m.Target == system)
                foreach (var a in Team(m)) sum += a.Leadership / div;
        return sum;
    }

    // InciteSupportTiming = PerIncident: in slot 6.3, every incident of a system with a working Incite mission costs its holder 2 support. rule: §6 Incite.
    private void OnIncident(StepContext ctx, string system, Side holder)
    {
        if (Params.InciteSupportTiming != InciteSupportTiming.PerIncident) return;
        if (!S.Missions.Values.Any(m => m.Kind == MissionKind.InciteUprising && m.Phase == MissionPhase.Working && m.Target == system)) return;
        _support.ChangeSupport(ctx, system, holder, Data.Numbers.InciteSupport, SupportCause.IncitedUprising);
    }
}
