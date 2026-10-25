using Praedra.Core.Battle;
using Praedra.Core.Clock;
using Praedra.Core.Control;
using Praedra.Core.Economy;
using Praedra.Core.Fleets;
using Praedra.Core.Fog;
using Praedra.Core.Support;
using Praedra.Core.Troops;

namespace Praedra.Core.Characters;

/// <summary>
/// What moves loyalty (B2c): the four game events that call <see cref="CharacterModule.ShiftLoyalty"/>, reacting to the event at the moment it is
/// emitted (<see cref="Game.ObserveEvents"/>), and the per-system loss tallies they read (<see cref="LoyaltyState"/>, saved and hashed).
/// 1. A holder change to Collective or Hand (HolderChangeAftermath, the queued 0x31b; B5a): one draw k = rand(0..SDPRTB 2561) + 2562, ShiftLoyalty(new owner, k); to neutral: nothing.
/// 2. HqDestroyed (the Collective HQ, any cause): one draw, ShiftLoyalty(Collective, -k), k in 0..5.
/// 3. BattleEnded with exactly one side's fleets or regiments left at the system: ShiftLoyalty(winner, loss shift of the battle tallies).
/// 4. BlockadeLifted: ShiftLoyalty(gaining side, loss shift of the assault and bombardment tallies).
/// Switch <see cref="CharacterParams.LoyaltyTriggers"/> (DEFAULT on). rule: m5-characters-and-force §Loyalty and betrayal (Traced, B2c).
/// </summary>
public sealed class LoyaltyModule
{
    /// <summary>The original's side numbers. rule: B2c §Rules.</summary>
    public const int SideNumberCollective = CharacterModule.SideNumberCollective, SideNumberHand = CharacterModule.SideNumberHand, SideNumberNeutral = 3;

    private readonly Game _game;
    private readonly CharacterModule _ch;
    private readonly FleetModule _fleets;
    private readonly EconomyModule _eco;
    private readonly TroopData _troops;
    private CharacterParams P => _ch.Params;

    private LoyaltyModule(Game game, CharacterModule ch, FleetModule fleets, EconomyModule eco, TroopData troops)
    {
        _game = game; _ch = ch; _fleets = fleets; _eco = eco; _troops = troops;
    }

    /// <summary>Register the observer. Attach after the characters. rule: B2c §Work 1.</summary>
    public static LoyaltyModule Attach(Game game, CharacterModule ch, FleetModule fleets, EconomyModule eco, TroopData troops)
    {
        var m = new LoyaltyModule(game, ch, fleets, eco, troops);
        game.ObserveEvents(m.OnEvent);
        return m;
    }

    private LoyaltyState S => _game.State.Loyalty;

    /// <summary>The tallies of a system (null = none yet, all 0).</summary>
    public SystemLossTally? TallyOf(string system) => S.Systems.TryGetValue(system, out var t) ? t : null;

    private SystemLossTally Tally(string system)
    {
        if (!S.Systems.TryGetValue(system, out var t)) S.Systems[system] = t = new SystemLossTally();
        return t;
    }

    public static int SideNumber(Side? s) => s switch { Side.Collective => SideNumberCollective, Side.Hand => SideNumberHand, _ => SideNumberNeutral };
    private static int Index(int sideNumber) => sideNumber - 1;

    /// <summary>
    /// <c>0x53ec10(W, T1, T2)</c>: W = Collective: (T2 - T1) / d + T2 / d; W = Hand: (T1 - T2) / d + T1 / d; C division truncating toward zero
    /// (SDPRTB 2560 = 50). rule: B2c §4.
    /// </summary>
    public int LossShift(int winner, int collectiveLoss, int handLoss)
    {
        int d = P.LossShiftDivisor;
        int enemy = winner == SideNumberCollective ? handLoss : collectiveLoss, own = winner == SideNumberCollective ? collectiveLoss : handLoss;
        return (enemy - own) / d + enemy / d;
    }

    /// <summary>The loss value of a regiment, capital ship or fighter squadron (record word 12, uprising_defense); 0 for anything else. rule: B2c §3.</summary>
    public int LossValue(EconObject o) => o.Category switch
    {
        ObjectCategory.Regiment => _troops.Regiment(o.Class)?.UprisingDefense ?? 0,
        ObjectCategory.CapitalShip or ObjectCategory.Fighter => _fleets.ClassOf(o)?.LossValue ?? 0,
        _ => 0,
    };

    private void OnEvent(StepContext ctx, GameEvent e)
    {
        if (!P.LoyaltyTriggers) return;
        switch (e)
        {
            // rule: B2c §1 (0x5616a0): the new owner's shift, one draw; the old side's amount is 0 (no draw); neutral: nothing. It runs in the
            // handler of the queued event 0x31b, after the flip's own draws (B5a section 7), so it reacts to HolderChangeAftermath.
            case HolderChangeAftermath c when c.To is Side.Collective or Side.Hand:
                {
                    int k = ctx.Rng.NextInt(0, P.CaptureShiftSpread) + P.CaptureShiftBase;
                    _ch.ShiftLoyalty(ctx, SideNumber(c.To.Value), k);
                    break;
                }
            // rule: B2c §2 (0x561780): the Collective HQ stops existing; a negative bound draws 0..|bound| and negates (0x53e290)
            case HqDestroyed:
                {
                    int b = P.HqShiftBound;
                    int k = (b < 0 ? -ctx.Rng.NextInt(0, -b) : ctx.Rng.NextInt(0, b)) + P.HqShiftBase;
                    _ch.ShiftLoyalty(ctx, SideNumberCollective, k);
                    break;
                }
            // rule: B2c §3 resets: a battle starting resets the battle tallies of all three sides (0x511080)
            case BattleStarted bs:
                Array.Clear(Tally(bs.System).Battle);
                break;
            // rule: B2c §3: a unit lost in battle (reason 12; DEFAULT also NoHyperdrive, NoBerth, Stalemate) adds its loss value to its own side's tally
            case UnitDestroyed u when _eco.Find(u.Unit) is { } o:
                {
                    int v = LossValue(o);
                    if (v != 0) Tally(u.System).Battle[Index(SideNumber(o.Side))] += v;
                    break;
                }
            // rule: B2c §3: regiments killed in ground combat (reason 14) and by bombardment (reason 15)
            case RegimentDestroyed r when r.Reason is "AssaultBattery" or "AssaultDuel" or "Bombardment":
                {
                    int v = _troops.Regiment(r.Class)?.UprisingDefense ?? 0;
                    if (v == 0) break;
                    var t = Tally(r.System);
                    (r.Reason == "Bombardment" ? t.Bombardment : t.Assault)[Index(SideNumber(r.Side))] += v;
                    break;
                }
            // rule: B2c §4 (0x517270): exactly one side's fleets or regiments left at the system wins; no draw
            case BattleEnded be:
                {
                    string sys = be.Result.System;
                    bool a = Present(sys, Side.Collective), m = Present(sys, Side.Hand);
                    if (a == m) break;
                    int w = a ? SideNumberCollective : SideNumberHand;
                    var t = TallyOf(sys);
                    int shift = LossShift(w, t?.Battle[0] ?? 0, t?.Battle[1] ?? 0);
                    _ch.ShiftLoyalty(ctx, w, shift);
                    break;
                }
            // rule: B2c §3, §5 (0x511300): a blockade starting resets the assault and bombardment tallies and records the blockader
            case BlockadeStarted bs:
                {
                    var t = Tally(bs.System);
                    Array.Clear(t.Assault); Array.Clear(t.Bombardment);
                    t.Blockader = SideNumber(bs.Blockader);
                    break;
                }
            // rule: B2c §5 (0x561280): the blockade ended; the blockader gains if the system is destroyed or now his, else the defender
            case BlockadeLifted bl:
                {
                    var t = TallyOf(bl.System);
                    int b = t?.Blockader ?? SideNumber(bl.Blockader);
                    if (b == SideNumberNeutral) break;
                    bool blockaderGains = _eco.IsDestroyed(bl.System) || SideNumber(_fleets.OwnerOf(bl.System)) == b;
                    int g = blockaderGains ? b : (b == SideNumberCollective ? SideNumberHand : SideNumberCollective);
                    int X(int s) => t == null ? 0 : t.Assault[Index(s)] + t.Bombardment[Index(s)]
                        + (s != b ? t.Assault[Index(SideNumberNeutral)] + t.Bombardment[Index(SideNumberNeutral)] : 0); // 0x509a20
                    _ch.ShiftLoyalty(ctx, g, LossShift(g, X(SideNumberCollective), X(SideNumberHand)));
                    break;
                }
        }
    }

    // rule: B2c §4 (0x4ffef0, 0x503a50): the side still has a fleet or a landed regiment at the system
    private bool Present(string system, Side side) =>
        _game.State.Fleets.Values.Any(f => f.At == system && f.Side == side && f.Ships.Count + f.Squadrons.Count > 0)
        || _eco.Objects.Any(o => o.Category == ObjectCategory.Regiment && o.Side == side && o.AboardShip == 0 && o.System == system);
}
