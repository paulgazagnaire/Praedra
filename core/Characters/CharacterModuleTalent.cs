using Praedra.Core.Clock;
using Praedra.Core.Fleets;

namespace Praedra.Core.Characters;

/// <summary>Characters: Talent (moved unchanged from CharacterModule.cs).</summary>
public sealed partial class CharacterModule
{
    // ================================================================== Talent (§Talent)

    /// <summary>
    /// One Train Talent round (the mission calls it at the end of its timer): for each student in id order, gap = trainer - student (effective);
    /// draw r = rand(0..99) always; success if gap &gt; 0 and r &lt; min(gap, 100) * TalentTrainingChancePercent / 100; on success draw
    /// g = rand(0..gap * TalentTrainingGainPercent / 100) (New DEFAULTs 50 and 25; Classic 100 and 50) and add it to the
    /// student's acquired Talent. Events TalentTrainingResult, then TalentRankChanged if the rank changed. No student reaches the trainer.
    /// rule: §Talent (Train Talent: the roll), Traced 0x55c7c0, 0x53e2f0, 0x55ca20 (chance reading [inferred]); CH3 to CH6.
    /// </summary>
    public void TalentTrainingRound(StepContext ctx, CharacterRecord trainer, IEnumerable<CharacterRecord> students)
    {
        int day = ctx.Day + 1;
        foreach (var s in students.OrderBy(x => x.Id).ToList())
        {
            var before = RankOf(s);
            int gap = trainer.EffectiveTalent - s.EffectiveTalent;
            int r = ctx.Rng.NextInt(0, 99);
            // rule: m5-characters-and-talent §Talent (Train Talent: the roll; New, Paul 2026-10-18; Classic = 100 / 50)
            int chance = Math.Min(gap, Params.TrainingChanceCap) * Params.TalentTrainingChancePercent / CharacterRecord.PercentDivisor;
            bool success = gap > 0 && r < chance;
            int gain = 0;
            if (success)
            {
                gain = ctx.Rng.NextInt(0, gap * Params.TalentTrainingGainPercent / CharacterRecord.PercentDivisor);
                s.TalentTraining += gain;
            }
            int sid = s.Id, tid = trainer.Id, nf = s.EffectiveTalent, g2 = gain; var side = s.Side;
            ctx.Emit((i, _) => new TalentTrainingResult(i, day, tid, sid, side, gap, r, success, g2, nf));
            var after = RankOf(s);
            if (after != before) ctx.Emit((i, _) => new TalentRankChanged(i, day, sid, side, before, after));
        }
    }

    /// <summary>
    /// A story discovery (Classic's film story missions called it; they are cut, data/design/cut-classes.csv, so only tests do now): a hidden talented character becomes discovered. Event TalentDiscovered.
    /// With <see cref="TalentDiscoveryMode.Story"/> (DEFAULT) this and the known Talent at creation are the only discoveries. rule: §Talent (Talent discovery).
    /// </summary>
    public bool DiscoverTalent(StepContext? ctx, CharacterRecord c, string cause)
    {
        if (!c.TalentHolder || c.TalentDiscovered) return false;
        var before = RankOf(c);
        c.TalentDiscovered = true;
        RollTalentExperience(c, ctx?.Rng ?? _game.Rng); // rule: §Talent (B2e §5: Talent is rolled when he becomes aware)
        int day = (ctx?.Day ?? _game.Day) + 1;
        Emit(ctx, (i, _) => new TalentDiscovered(i, day, c.Id, c.Side, cause));
        RankEvent(ctx, day, c, before);
        return true;
    }

    private void RankEvent(StepContext? ctx, int day, CharacterRecord c, TalentRank? before)
    {
        var after = RankOf(c);
        if (after == before) return;
        int id = c.Id; var side = c.Side;
        Emit(ctx, (i, _) => new TalentRankChanged(i, day, id, side, before, after));
    }

    /// <summary>The next pair id from the game-wide counter (0x55ff60 stamps both characters of a discovery). rule: §Talent (B2e §4).</summary>
    public int NewPairId() => ++S.NextPairId;

    /// <summary>
    /// Do <paramref name="m"/> and <paramref name="x"/> meet in a Talent encounter: each names the other as discovered talented character and both
    /// carry the same non-zero pair id. Else nothing happens and nothing is drawn. rule: §Talent (Traced B2e §4 "Preconditions").
    /// </summary>
    public bool MutualDiscovery(CharacterRecord m, out CharacterRecord? x)
    {
        x = m.DiscoveredTalentHolder != 0 ? Get(m.DiscoveredTalentHolder) : null;
        return x != null && x.DiscoveredTalentHolder == m.Id && m.PairId != 0 && m.PairId == x.PairId;
    }

    /// <summary>
    /// Steps 2 to 7 of the Talent encounter (0x560200), after the mission module's duel (step 1) with <paramref name="x"/> as the captor:
    /// read both effective combats; if <paramref name="m"/> was not hurt in the duel, HurtBy(m, x) on m (p = max(1, x - m)); HurtBy(x, m) on x;
    /// whoever hurt the other gains GNPRTB 2578 base combat; both keys and pair ids cleared, both sides told; then the Talent effects by key (Nobody
    /// and Ravilla: Nobody learns his heritage, the hand injury below Talent 80, Talent +max(1, gap x 25%); Nobody and The Chair +20%; Ledger, once aware,
    /// with Ravilla or The Chair +20%). "Free" = not captured after the duel. Draws in order: m's HurtBy (unless hurt in the duel), x's HurtBy,
    /// Nobody's hand draw. rule: §Talent (Traced B2e §4, Work 11).
    /// </summary>
    public void TalentEncounter(StepContext ctx, int day, int mission, CharacterRecord m, CharacterRecord x, bool mHurtInDuel, bool mEscaped)
    {
        int mc = EffectiveSkill(m, Skill.Combat), xc = EffectiveSkill(x, Skill.Combat); // step 2
        bool mHurt = mHurtInDuel, xHurt;
        if (!mHurtInDuel) mHurt = AddInjury(ctx, m, HurtByRoll(ctx.Rng, mc, xc), CharacterCause.TalentEncounter); // step 3
        xHurt = AddInjury(ctx, x, HurtByRoll(ctx.Rng, xc, mc), CharacterCause.TalentEncounter); // step 4
        if (xHurt) GainSkill(m, Skill.Combat, Params.EncounterCombatGain); // step 5
        if (mHurt) GainSkill(x, Skill.Combat, Params.EncounterCombatGain);
        m.DiscoveredTalentHolder = 0; x.DiscoveredTalentHolder = 0; m.PairId = 0; x.PairId = 0; // step 6 (0x4ee500, 0x4ef1c0)
        {
            int mid = m.Id, xid = x.Id; var ms = m.Side; var xs = x.Side; bool mh = mHurt, xh = xHurt, esc = mEscaped;
            ctx.Emit((i, _) => new TalentEncounterResolved(i, day, mission, mid, ms, xid, xs, esc, mh, xh));
        }
        // step 7: the Talent effects, by the keys of m and x
        bool Is(CharacterRecord c, int id) => c.Id == id;
        bool Free(CharacterRecord c) => c.Status is not (CharacterStatus.Captured or CharacterStatus.Dead);
        CharacterRecord? Pair(int a, int b) => Is(m, a) && Is(x, b) ? m : Is(x, a) && Is(m, b) ? x : null;
        var p = Params;
        if (Pair(p.Nobody, p.Ravilla) is { } nobody)
        {
            var ravilla = nobody == m ? x : m;
            if (!nobody.KnowsHeritage) { nobody.KnowsHeritage = true; Heritage(ctx, day, nobody); }
            if (Free(nobody) && nobody.EffectiveTalent < p.NobodyHandMaxTalent) // the lost hand (0x561b60)
                AddInjury(ctx, nobody, ctx.Rng.NextInt(0, p.NobodyHandSpread) + p.NobodyHandBase, CharacterCause.TalentEncounter);
            if (Free(nobody)) GainTalent(ctx, day, nobody, ravilla, p.NobodyRavillaGainPercent, p.NobodyRavillaMinGain);
        }
        else if (Pair(p.Nobody, p.Chair) is { } nobody2)
        {
            if (Free(nobody2)) GainTalent(ctx, day, nobody2, nobody2 == m ? x : m, p.NobodyChairGainPercent, p.NobodyChairMinGain);
        }
        else if (Pair(p.Ledger, p.Ravilla) is { } ledger)
        {
            if (Free(ledger) && ledger.TalentDiscovered) GainTalent(ctx, day, ledger, ledger == m ? x : m, p.LedgerRavillaGainPercent, p.LedgerRavillaMinGain);
        }
        else if (Pair(p.Ledger, p.Chair) is { } ledger2)
        {
            if (Free(ledger2) && ledger2.TalentDiscovered) GainTalent(ctx, day, ledger2, ledger2 == m ? x : m, p.LedgerChairGainPercent, p.LedgerChairMinGain);
        }
    }

    // TalentExperience += g, g = max(min, gap * percent / 100) when the gap is positive, else min (GNPRTB 3072-3075, 3084-3087). rule: B2e §4 step 7.
    private void GainTalent(StepContext ctx, int day, CharacterRecord talented, CharacterRecord other, int percent, int min)
    {
        int gap = other.EffectiveTalent - talented.EffectiveTalent;
        int g = gap > 0 ? Math.Max(min, gap * percent / CharacterRecord.PercentDivisor) : min;
        var before = RankOf(talented);
        talented.TalentExperience += g;
        int id = talented.Id, nf = talented.EffectiveTalent; var side = talented.Side;
        ctx.Emit((i, _) => new TalentGained(i, day, id, side, g, nf, CharacterCause.TalentEncounter));
        RankEvent(ctx, day, talented, before);
    }

    private static void Heritage(StepContext ctx, int day, CharacterRecord c)
    {
        int id = c.Id; var side = c.Side;
        ctx.Emit((i, _) => new HeritageLearned(i, day, id, side));
    }

    /// <summary>The original's side numbers (1 = Collective, 2 = Hand, 3 = neutral). rule: m5-characters-and-talent §Loyalty (B2b §4).</summary>
    public const int SideNumberCollective = 1, SideNumberHand = 2;
    /// <summary>Loyalty bounds (0x53e0d0 with 0x661a84 = 0 and 0x661a88 = 100). Traced B2b.</summary>
    public const int LoyaltyMin = 0, LoyaltyMax = 100;

    /// <summary>
    /// <c>ShiftLoyalty(side, amount)</c> (0x561910): nothing when the amount is 0 or the side number is not 1 or 2. Otherwise every character
    /// (pool included, id order; no draws) whose record is not "unable to betray" (word 35) gains the amount if it serves that side and loses it
    /// otherwise, clamped to 0..100. Event LoyaltyChanged when the value moves. No caller yet (B2c). rule: m5-characters-and-talent §Loyalty (Traced B2b).
    /// </summary>
    public void ShiftLoyalty(StepContext? ctx, int sideNumber, int amount)
    {
        if (amount == 0) return;
        Side side;
        if (sideNumber == SideNumberCollective) side = Side.Collective;
        else if (sideNumber == SideNumberHand) side = Side.Hand;
        else return;
        int day = ctx?.Day ?? _game.Day;
        foreach (var c in S.Characters.Values)
        {
            if (!Data.TryGet(c.Id, out var cls) || cls.UnableToBetray) continue;
            int from = c.Loyalty;
            int to = Math.Clamp(c.Side == side ? from + amount : from - amount, LoyaltyMin, LoyaltyMax);
            if (to == from) continue;
            c.Loyalty = to;
            int id = c.Id; var cs = c.Side;
            Emit(ctx, (i, _) => new LoyaltyChanged(i, day, id, cs, from, to));
        }
    }

    /// <summary>
    /// Raise one skill (m5-missions: a successful research member gains GNPRTB 6158 = 1 in that research skill; Classic's character-side check
    /// before the gain is Missing, so no cap). Not the refused SkillGrowth switch (growth outside research missions). rule: m5-missions §6 Research.
    /// </summary>
    public void GainSkill(CharacterRecord c, Skill skill, int amount) => c.Skills[(int)skill] += amount;
}
