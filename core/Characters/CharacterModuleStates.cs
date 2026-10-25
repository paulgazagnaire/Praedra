using Praedra.Core.Clock;
using Praedra.Core.Fleets;

namespace Praedra.Core.Characters;

/// <summary>Characters: the state machine of capture, injury, death and freeing (moved unchanged from CharacterModule.cs).</summary>
public sealed partial class CharacterModule
{
    // ================================================================== the state machine (§Capture, injury, death, freeing)

    // Every post the character holds ends (fleet post, system General), then it is detached; events PostVacated and CharacterDetached.
    private void VacateAll(StepContext? ctx, int day, CharacterRecord c, string cause)
    {
        if (HeldSystemPost(c) is { } sys)
        {
            S.SystemGenerals.Remove(sys);
            Emit(ctx, (i, _) => new PostVacated(i, day, c.Id, c.Side, PostKind.SystemGeneral, cause));
        }
        Detach(ctx, day, c, cause);
    }

    private void Detach(StepContext? ctx, int day, CharacterRecord c, string cause)
    {
        if (c.AttachedFleet == 0) return;
        string? sys = State.Fleets.TryGetValue(c.AttachedFleet, out var f) ? PlaceOfFleet(f) : c.System;
        if (c.Post != CharacterPost.None)
        {
            var post = (PostKind)((int)c.Post - 1);
            Emit(ctx, (i, _) => new PostVacated(i, day, c.Id, c.Side, post, cause));
            c.Post = CharacterPost.None;
        }
        int fid = c.AttachedFleet;
        c.AttachedFleet = 0; c.System = sys;
        Emit(ctx, (i, _) => new CharacterDetached(i, day, c.Id, c.Side, fid, sys, cause));
    }

    private static void Emit(StepContext? ctx, Func<int, int, GameEvent> make) => ctx?.Emit(make);

    /// <summary>Where the character stands now for the state functions (a rider in transit: the system its fleet last left; a lone traveller: the system it last left).</summary>
    private string? StandingPlace(CharacterRecord c)
    {
        if (c.Status == CharacterStatus.Captured)
            return c.HeldAboard != 0 ? (State.Fleets.TryGetValue(c.HeldAboard, out var hf) ? PlaceOfFleet(hf) : null) : c.HeldAt;
        if (c.AttachedFleet != 0 && State.Fleets.TryGetValue(c.AttachedFleet, out var f)) return PlaceOfFleet(f);
        return c.System ?? (c.Transit is { } t ? t.Path[t.HopIndex] : null);
    }

    // The command post is lost (CommandKind := 0, 0x4eeed0): a fleet post becomes None (the character keeps riding), a system General post ends.
    // rule: §Capture, injury, death, freeing (B2e §2 "When Injury changes").
    private void VacatePosts(StepContext? ctx, int day, CharacterRecord c, string cause)
    {
        if (HeldSystemPost(c) is { } sys)
        {
            S.SystemGenerals.Remove(sys);
            Emit(ctx, (i, _) => new PostVacated(i, day, c.Id, c.Side, PostKind.SystemGeneral, cause));
        }
        if (c.Post != CharacterPost.None)
        {
            var post = (PostKind)((int)c.Post - 1);
            Emit(ctx, (i, _) => new PostVacated(i, day, c.Id, c.Side, post, cause));
            c.Post = CharacterPost.None;
        }
    }

    /// <summary>
    /// The death test after an injury (+0x2e0): majors none and no draw (0x6158b0) unless <see cref="CharacterParams.MajorsCanDie"/>; minors one
    /// draw, die if rand(0..99) &lt; GNPRTB 2577 (25) while <see cref="CharacterParams.MinorsCanDie"/> (DEFAULT on); never for a death refused by
    /// <see cref="DeathRefused"/> (no draw). rule: §Capture, injury, death, freeing (Traced B2e §2, 0x56f160 -> 0x53eb30).
    /// </summary>
    private bool TakesDeathTest(CharacterRecord c) =>
        (ClassOf(c).Kind == CharacterKind.Major ? Params.MajorsCanDie : Params.MinorsCanDie) && DeathRefused?.Invoke(c) != true;

    /// <summary>
    /// <c>AddInjury(c, n, reason)</c> (vtable +0x2f0 = 0x4ef6d0): only if n &gt; 0 and the character is placed and alive. Injury += n (kept in
    /// 0..32767). Then, as the Injury notifier (0x4f1720): a Free character becomes Injured (event CharacterInjured), the command post is lost, a mission
    /// member who may resign leaves (<see cref="InjuredResign"/>). Then the death test (<see cref="TakesDeathTest"/>): death is Kill with the same
    /// cause. Returns true when points were added. rule: §Capture, injury, death, freeing (Traced B2e §2).
    /// </summary>
    public bool AddInjury(StepContext? ctx, CharacterRecord c, int n, string cause)
    {
        if (n <= 0 || !c.Placed || c.Status == CharacterStatus.Dead) return false;
        int day = (ctx?.Day ?? _game.Day) + 1;
        c.Injury = Math.Min(MaxInjury, c.Injury + n);
        string? sys = StandingPlace(c);
        if (c.Status == CharacterStatus.Free) c.Status = CharacterStatus.Injured;
        int id = c.Id, pts = c.Injury; var side = c.Side;
        Emit(ctx, (i, _) => new CharacterInjured(i, day, id, side, sys, cause, n, pts));
        VacatePosts(ctx, day, c, CharacterCause.Injured);
        InjuredResign?.Invoke(ctx, c);
        if (TakesDeathTest(c) && (ctx?.Rng ?? _game.Rng).NextInt(0, PercentDieMax) < Params.MinorDeathPercent) Kill(ctx, c, cause);
        return true;
    }

    /// <summary>The Injury setter keeps 0..32767 (0x4ee2d0). Traced B2e §2.</summary>
    public const int MaxInjury = 32767;
    /// <summary>The percent roll rand(0..99) (0x53e2f0). Traced B2a, B2e.</summary>
    private const int PercentDieMax = 99;

    /// <summary>
    /// One injury roll (0x53e990 family): one draw <c>rand(0..99) &lt; p</c>; on a hit <c>n = rand(0..p) + rand(0..spread) + base</c> (two more
    /// draws). Returns n (0 = no injury). rule: §Capture, injury, death, freeing (Traced B2e §2, the injury rolls).
    /// </summary>
    private static int InjuryRoll(GameRng rng, int p, int sizeBase, int sizeSpread)
    {
        if (rng.NextInt(0, PercentDieMax) >= p) return 0;
        int a = rng.NextInt(0, p);
        return a + rng.NextInt(0, sizeSpread) + sizeBase;
    }

    /// <summary>Hurt(v): p = max(GNPRTB 2565, 100 - v), size GNPRTB 2566 + rand(0..2567). 0x53e990. rule: B2e §2.</summary>
    public int HurtRoll(GameRng rng, int v) => InjuryRoll(rng, Math.Max(Params.HurtMinChance, PercentDieMax + 1 - v), Params.HurtSizeBase, Params.HurtSizeSpread);
    /// <summary>HurtBy(a, b): p = max(GNPRTB 2571, b - a), GNPRTB 2572, 2573. 0x53ea70 (the Talent encounter). rule: B2e §2, §4.</summary>
    public int HurtByRoll(GameRng rng, int a, int b) => InjuryRoll(rng, Math.Max(Params.HurtByMinChance, b - a), Params.HurtBySizeBase, Params.HurtBySizeSpread);

    /// <summary>
    /// The Hurt roll on a character with his own effective combat (vtable +0x2e4, reason 10), then AddInjury. Called by both outcomes of the
    /// mission escape duel. Returns the points added (0 = none). rule: §Capture, injury, death, freeing (Traced B2e §2 "Who calls Hurt").
    /// </summary>
    public int Hurt(StepContext? ctx, CharacterRecord c, string cause)
    {
        if (!c.Placed || c.Status == CharacterStatus.Dead) return 0;
        int n = HurtRoll(ctx?.Rng ?? _game.Rng, EffectiveSkill(c, Skill.Combat));
        return AddInjury(ctx, c, n, cause) ? n : 0;
    }

    /// <summary>
    /// A caller that passes no amount (uprising code 3, the harvester's hit, a major's Kill fallback, a key target's injury): only a Free or Injured
    /// character; the Hurt roll on his own combat (DEFAULT: Classic's system routines call that slot, not read), then AddInjury. Returns true
    /// when points were added. rule: §Capture, injury, death, freeing 1 (B2e Work 2); CH22.
    /// </summary>
    public bool Injure(StepContext? ctx, CharacterRecord c, string cause)
    {
        if (!c.Placed || c.Status is not (CharacterStatus.Free or CharacterStatus.Injured)) return false;
        return Hurt(ctx, c, cause) > 0;
    }

    /// <summary>
    /// Only a Free or Injured character of the other side: status Captured, held at <paramref name="heldAt"/> (DEFAULT: where it stood), posts end,
    /// detached, a trip ends, mission membership ends; the injury points stay. A capture that lets him escape (<paramref name="canEscape"/>; story
    /// captures pass false) sets the escape timer <c>100 + rand(0..1000)</c> days (one draw; none with <see cref="CharacterParams.CaptiveEscape"/>
    /// off). Event CharacterCaptured. rule: §Capture, injury, death, freeing 2; §Escape (B2e §3); CH21.
    /// With a captor <paramref name="unit"/> (QA-7, B2e §3 "Where a new prisoner is held", 0x546370), in the same step: when he or the captor is on
    /// a mission he is moved to the captor's place (aboard the captor's fleet, else onto its planet; placed there at once, DEFAULT), his own
    /// mission membership ends, a captor on a mission takes him into its team as a captive (<see cref="JoinAsCaptive"/>), and then, unless he is
    /// such a captive, the liberation test runs on him at once: on a planet not held by the captor's side he is freed there, no draw. A capture
    /// without a unit (the harvester's fallback, story and test captures) keeps him where he stood and makes no liberation test (DEFAULT).
    /// </summary>
    public bool Capture(StepContext? ctx, CharacterRecord c, Side captor, string? heldAt, string cause, bool? canEscape = null, CaptorUnit? unit = null)
    {
        if (!c.Placed || c.Status is not (CharacterStatus.Free or CharacterStatus.Injured) || captor == c.Side) return false;
        int day = (ctx?.Day ?? _game.Day) + 1;
        bool wasOnMission = IsOnMission(c.Id);
        // QA-9 (B2e Work 13): the return place of the mission this capture ends is saved for a later Autoroute (step 2)
        if (wasOnMission && MissionReturnPlace?.Invoke(c.Id) is { } rp) c.ReturnPlace = rp;
        c.AutoroutePending = false; c.JoinFleet = 0;
        string? at = heldAt ?? StandingPlace(c);
        VacateAll(ctx, day, c, CharacterCause.Captured);
        c.Transit = null;
        c.Status = CharacterStatus.Captured; c.Captor = captor; c.HeldAt = at; c.HeldAboard = 0; c.HeldAboardShip = 0; c.System = at; c.NextHealDay = 0;
        // rule: §Escape (B2e §3 "Can escape", "When attempts happen"): the timer is set as the State changes, before the state-change rule moves
        // him; Classic's story capture passes 0 (0x54b040), so a story capture never tries
        bool escapes = canEscape ?? cause != CharacterCause.Story;
        c.NextEscapeDay = escapes && Params.CaptiveEscape ? (ctx?.Day ?? _game.Day) + EscapeDelay(ctx?.Rng ?? _game.Rng) : 0;
        // rule: §Capture 2 (B2e §3 "Where a new prisoner is held" steps 1-2; QA-7): to the captor's place when he or the captor is on a mission
        if (unit is CaptorUnit u && (wasOnMission || u.MissionInvolved))
        {
            if (u.Fleet != 0 && State.Fleets.TryGetValue(u.Fleet, out var hf)) { c.HeldAboard = hf.Id; c.HeldAboardShip = u.Ship; c.HeldAt = null; c.System = null; }
            else if (u.System != null) { c.HeldAt = u.System; c.System = u.System; }
        }
        MissionMembershipEnded?.Invoke(ctx, c); // step 3: out of his own mission
        bool captive = unit is CaptorUnit cu && cu.Mission != 0 && JoinAsCaptive?.Invoke(ctx, c, cu.Mission) == true; // step 4
        if (captive) { c.HeldAt = null; c.HeldAboard = 0; c.HeldAboardShip = 0; c.System = null; }
        string? shown = captive ? at : LocationOf(c) ?? StandingPlace(c);
        int aboard = c.HeldAboard, mission = captive ? unit!.Value.Mission : 0;
        Emit(ctx, (i, _) => new CharacterCaptured(i, day, c.Id, c.Side, captor, shown, cause, aboard, mission));
        RefreshSeatOfPower();
        // step 5: the liberation test on him at once (the check skips a captive of a mission; aboard, his place is the captor's fleet)
        if (unit != null && !captive && c.HeldAboard == 0 && c.HeldAt is { } held && _fleets.OwnerOf(held) != captor)
            Free(ctx, c, CharacterCause.Liberation);
        return true;
    }

    // rule: §Escape (B2e §3, §7): base GNPRTB 2584 + rand(0..2585), one draw
    private int EscapeDelay(GameRng rng) => Params.EscapeDelayBase + (Params.EscapeDelaySpread > 0 ? rng.NextInt(0, Params.EscapeDelaySpread) : 0);

    /// <summary>
    /// Not if already Dead. A major with <see cref="CharacterParams.MajorsCanDie"/> off (DEFAULT), a minor with <see cref="CharacterParams.MinorsCanDie"/>
    /// off (unless <paramref name="allowDeath"/>), or a death refused by <see cref="DeathRefused"/>: the call becomes <see cref="Injure"/>. Else status Dead (never removed), posts end,
    /// detached, a trip ends, mission membership ends, captor cleared. Event CharacterKilled. rule: §Capture, injury, death, freeing 3; CH20.
    /// </summary>
    public bool Kill(StepContext? ctx, CharacterRecord c, string cause, bool allowDeath = false)
    {
        if (!c.Placed || c.Status == CharacterStatus.Dead) return false;
        // allowDeath: this call may kill whatever the class switches say (m5-missions §6a key targets); DeathRefused (Immortal) still applies.
        bool canDie = allowDeath || (ClassOf(c).Kind == CharacterKind.Major ? Params.MajorsCanDie : Params.MinorsCanDie);
        if (!canDie || DeathRefused?.Invoke(c) == true) return Injure(ctx, c, cause);
        int day = (ctx?.Day ?? _game.Day) + 1;
        string? at = StandingPlace(c);
        VacateAll(ctx, day, c, CharacterCause.Killed);
        c.Transit = null;
        c.Status = CharacterStatus.Dead; c.Captor = null; c.HeldAt = null; c.HeldAboard = 0; c.HeldAboardShip = 0; c.System = at; c.NextEscapeDay = 0; c.NextHealDay = 0; c.NextAwakenDay = 0;
        MissionMembershipEnded?.Invoke(ctx, c);
        Emit(ctx, (i, _) => new CharacterKilled(i, day, c.Id, c.Side, at, cause));
        RefreshSeatOfPower();
        return true;
    }

    /// <summary>
    /// A prisoner freed (Rescue, an uprising, liberation, escape): status Free (Injured while he has injury points) at held_at, captor and escape
    /// timer cleared. Event CharacterFreed (CharacterEscaped for cause Escape). rule: §Capture, injury, death, freeing 4, 6; B2e §3.
    /// A prisoner held aboard is freed at the system his fleet stands at (in transit: the one it last left; QA-7 DEFAULT). Then, standing on a
    /// system that is not his side's (or destroyed), he sets off at once (<see cref="Autoroute"/>; QA-9).
    /// </summary>
    public bool Free(StepContext? ctx, CharacterRecord c, string cause)
    {
        if (c.Status != CharacterStatus.Captured) return false;
        int day = (ctx?.Day ?? _game.Day) + 1;
        string? at = StandingPlace(c);
        c.Status = c.Injury > 0 ? CharacterStatus.Injured : CharacterStatus.Free; c.Captor = null; c.System = at; c.HeldAt = null; c.HeldAboard = 0; c.HeldAboardShip = 0; c.NextEscapeDay = 0;
        if (cause == CharacterCause.Escape) Emit(ctx, (i, _) => new CharacterEscaped(i, day, c.Id, c.Side, at)); // Classic text "has escaped from imprisonment at"
        else Emit(ctx, (i, _) => new CharacterFreed(i, day, c.Id, c.Side, at, cause));
        RefreshSeatOfPower();
        Autoroute(ctx, c); // QA-9: a freed or escaped character on ground not of his side sets off the same step (B2e §3)
        return true;
    }

    /// <summary>
    /// Autorouting (B2e §3 "Where a freed, escaped or displaced character goes", Work 13; QA-9, 2026-10-18; rule event 0x307 -> 0x549230), run in
    /// the same step: only a placed character who is not captured or dead, stands on a system (riding no fleet, not travelling, not on a mission)
    /// that is destroyed or not of his side, moves. On his own side's ground he stays. While a battle is pending at his system he waits and is
    /// re-checked at slot 6.10 of each later day (DEFAULT timing). The destination is the first step that yields a place:
    /// 1. where he is (acceptable only when his side's and not destroyed); 2. his last mission's return place (<see cref="CharacterRecord.ReturnPlace"/>,
    /// same test); 3. the nearest system his side holds, not blockaded; 4. the nearest system with a fleet of his side (he joins it on arrival if it
    /// is still there); 5. the nearest system his side holds, blockaded; 6. the nearest neutral system; 7. the nearest enemy system. "Nearest" = the
    /// fewest ticks by the m1-travel path at character speed from his system, ties by system index (DEFAULT for the game's precomputed order); a
    /// destroyed system never counts. He leaves now (CharacterDeparted) at normal character travel; with no place found he stays. No draw.
    /// Returns true when a trip was set. rule: m5-characters-and-talent §Capture, injury, death, freeing (Autoroute; QA-9).
    /// </summary>
    public bool Autoroute(StepContext? ctx, CharacterRecord c)
    {
        c.AutoroutePending = false;
        if (!c.Placed || c.Status is not (CharacterStatus.Free or CharacterStatus.Injured) || c.AttachedFleet != 0 || c.Transit != null
            || c.System is not { } sys || IsOnMission(c.Id)) return false;
        bool Destroyed(string n) => State.Harvester.IsDestroyed(n);
        bool Acceptable(string? n) => n != null && _fleets.OwnerOf(n) == c.Side && !Destroyed(n);
        if (Acceptable(sys)) return false; // step 1 (0x54c7d0: on his own side's ground he just stays)
        if (BattlePending(sys)) { c.AutoroutePending = true; return false; } // flag 0x1000 (BattlePending, inferred)
        string? dest = null; int join = 0;
        if (c.ReturnPlace is { } rp && rp != sys && Acceptable(rp)) dest = rp; // step 2
        dest ??= Nearest(sys, n => _fleets.OwnerOf(n) == c.Side && !State.Blockades.ContainsKey(n)); // step 3
        if (dest == null)
        {
            var fleetSystems = State.Fleets.Values.Where(f => f.Side == c.Side && f.At != null).Select(f => f.At!).ToHashSet(StringComparer.Ordinal);
            dest = Nearest(sys, fleetSystems.Contains); // step 4
            if (dest != null) join = State.Fleets.Values.Where(f => f.Side == c.Side && f.At == dest).Min(f => f.Id); // lowest fleet id there (DEFAULT)
        }
        dest ??= Nearest(sys, n => _fleets.OwnerOf(n) == c.Side && State.Blockades.ContainsKey(n)); // step 5
        dest ??= Nearest(sys, n => _fleets.OwnerOf(n) == null); // step 6
        dest ??= Nearest(sys, n => _fleets.OwnerOf(n) is Side o && o != c.Side); // step 7
        if (dest == null) return false;
        var path = _fleets.FindPath(sys, dest, Params.CharacterSpeed);
        if (!path.Ok) return false;
        int k = ctx?.Day ?? _game.Day, first = path.HopTicks[0], day = k + 1;
        c.System = null; c.JoinFleet = join;
        c.Transit = new FleetTransit { Path = path.Names(_fleets.Galaxy).ToList(), HopTicks = path.HopTicks.ToList(), HopIndex = 0, DueDay = k + 1 + first };
        string from = sys; var side = c.Side;
        Emit(ctx, (i, _) => new CharacterDeparted(i, day, c.Id, side, from, first));
        return true;
    }

    /// <summary>The system matching <paramref name="ok"/> fewest ticks away by the character travel path (ties by system index); never the start or a destroyed system.</summary>
    private string? Nearest(string from, Func<string, bool> ok)
    {
        string? best = null; int bestTicks = int.MaxValue;
        foreach (var sys in _fleets.Galaxy.Systems) // system index order: the first of equal ticks wins
        {
            if (sys.Name == from || State.Harvester.IsDestroyed(sys.Name) || !ok(sys.Name)) continue;
            var p = _fleets.FindPath(from, sys.Name, Params.CharacterSpeed);
            if (p.Ok && p.TotalTicks < bestTicks) { best = sys.Name; bestTicks = p.TotalTicks; }
        }
        return best;
    }

    /// <summary>A battle is pending at a system (flag 0x1000, read as BattlePending, inferred): fleets of both sides there (DEFAULT). QA-9.</summary>
    private bool BattlePending(string system)
    {
        bool a = false, e = false;
        foreach (var f in State.Fleets.Values)
            if (f.At == system && f.Ships.Count + f.Squadrons.Count > 0) { if (f.Side == Side.Collective) a = true; else if (f.Side == Side.Hand) e = true; }
        return a && e;
    }

    /// <summary>
    /// A mission team's captive is put down where the group ends (m5-missions calls it as the group leaves the state; inferred, B2e §3 step 4): held
    /// on that planet, then the liberation test (not the captor side's: freed there, no draw). rule: §Capture (B2e §3; QA-7).
    /// </summary>
    public void HoldCaptive(StepContext? ctx, CharacterRecord c, string? at)
    {
        if (c.Status != CharacterStatus.Captured) return;
        c.HeldAt = at; c.HeldAboard = 0; c.HeldAboardShip = 0; c.System = at;
        if (at != null && _fleets.OwnerOf(at) != c.Captor && !IsOnMission(c.Id)) Free(ctx, c, CharacterCause.Liberation);
    }

    /// <summary>Escape = Free with cause Escape. rule: §Capture, injury, death, freeing 6.</summary>
    public bool Escape(StepContext? ctx, CharacterRecord c) => Free(ctx, c, CharacterCause.Escape);

    /// <summary>
    /// The injury points reach 0 (healing, or a test): Injury 0 and an Injured character becomes Free. Event CharacterHealed (Classic text "has
    /// been healed of all injuries"). rule: §Capture, injury, death, freeing 5 (B2e §2).
    /// </summary>
    public bool Heal(StepContext? ctx, CharacterRecord c)
    {
        if (c.Status == CharacterStatus.Dead || (c.Injury == 0 && c.Status != CharacterStatus.Injured)) return false;
        int day = (ctx?.Day ?? _game.Day) + 1;
        c.Injury = 0; c.NextHealDay = 0;
        if (c.Status == CharacterStatus.Injured) c.Status = CharacterStatus.Free;
        Emit(ctx, (i, _) => new CharacterHealed(i, day, c.Id, c.Side));
        return true;
    }
}
