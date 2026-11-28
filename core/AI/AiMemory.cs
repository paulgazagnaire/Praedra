using Praedra.Core.Battle;
using Praedra.Core.Characters;
using Praedra.Core.Clock;
using Praedra.Core.Construction;
using Praedra.Core.Control;
using Praedra.Core.Defenses;
using Praedra.Core.Fleets;
using Praedra.Core.Fog;
using Praedra.Core.Harvester;
using Praedra.Core.Missions;

namespace Praedra.Core.AI;

/// <summary>What one <see cref="AiNote"/> remembers.</summary>
public enum AiNoteKind
{
    /// <summary>A battle in the feed (BattleEnded): System, Own and Other outcome, Armed.</summary>
    Battle,
    /// <summary>The side lost System to the enemy (SystemCaptured From = side, To = enemy).</summary>
    SystemLost,
    /// <summary>A bombardment in the feed (BombardmentResolved): System, Value = regiments killed.</summary>
    Bombardment,
    /// <summary>An own Espionage or Reconnaissance at System was foiled (MissionEnded).</summary>
    LookFoiled,
    /// <summary>The HQ left or arrived (HqDeparted, HqArrived).</summary>
    HqMoved,
    /// <summary>The feed placed enemy character Value at System (arrived, detached, made General, freed).</summary>
    EnemyCharacterAt,
    /// <summary>The feed took enemy character Value off the map (departed, captured, injured, killed).</summary>
    EnemyCharacterGone,
    /// <summary>
    /// An enemy shipyard project at System ended (ProjectCancelled) or its unfinished harvester was spiked (HarvesterDestroyed SabotageInYard):
    /// a HarvesterQueued sighting there from before is void (QA-61). Not saved before 2026-10-28: an older save loads with none.
    /// </summary>
    HarvesterYardGone,
    /// <summary>An own Burn the Books ended without success (MissionEnded Failed or Foiled): the AI waits before trying again. Not saved before 2026-11-04.</summary>
    BooksFailed,
    /// <summary>
    /// ShieldsFelt (SimpleAiParams.ShieldsFeltDays): an own shelling at System met shields of Value (BombardmentStarted S when hits got
    /// through; with no hits the shelling fleets' power when noted, at least the strike's P: fleets that strong do nothing there),
    /// or Value -1: an own Sabotage there succeeded (MissionSucceeded), so a shield may have fallen since. Noted only while kept (off: none).
    /// Not saved before 2026-11-27: an older save loads with none.
    /// </summary>
    ShieldsFelt,
    /// <summary>
    /// FighterGrounded (SimpleAiParams.FighterWaitMax): own squadron Value was set on the ground at System, built there (UnitCompleted) or
    /// unloaded (SquadronsUnloaded). Noted only while kept (off: none). Not saved before 2026-11-28: an older save loads with none.
    /// </summary>
    FighterGrounded,
    /// <summary>
    /// WallsRefused (SimpleAiParams.WinWallsForgetDays): an own assault at System was refused because the world is fully shielded
    /// (OrderRejected FullyShielded, m3-assault check 4): the walls stand, whatever an old look says. Noted only while kept (off: none).
    /// Not saved before 2026-11-28: an older save loads with none.
    /// </summary>
    WallsRefused,
}

/// <summary>
/// One event of the side's feed the AI reads again on later days, cut down to what it reads. <see cref="Floor"/> is the lowest day of this event
/// and every later event of the feed: the AI's look-backs walk the feed newest first and stop at the first event older than their window, so a
/// note counts for a window starting at day D exactly when Floor &gt;= D.
/// </summary>
public sealed class AiNote
{
    public AiNoteKind Kind { get; set; }
    public int Day { get; set; }
    public int Floor { get; set; }
    public string? System { get; set; }
    public int Value { get; set; }
    public BattleOutcome Own { get; set; }
    public BattleOutcome Other { get; set; }
    /// <summary>Battle: the side's units in the battle's BattleStarted that were armed capital ships when noted; null = no start in the feed.</summary>
    public List<int>? Armed { get; set; }
}

/// <summary>Days each kind of note is kept in a save (the longest window any attached AI reads it with). int.MaxValue = all.</summary>
public sealed class AiKeepDays
{
    public int Battle { get; set; }
    public int SystemLost { get; set; }
    public int Bombardment { get; set; }
    public int LookFoiled { get; set; }
    public int HqMoved { get; set; }
    public int Character { get; set; }
    public int HarvesterYardGone { get; set; }
    public int BooksFailed { get; set; }
    public int ShieldsFelt { get; set; }
    public int FighterGrounded { get; set; }
    public int WallsRefused { get; set; }

    public int For(AiNoteKind k) => k switch
    {
        AiNoteKind.Battle => Battle, AiNoteKind.SystemLost => SystemLost, AiNoteKind.Bombardment => Bombardment,
        AiNoteKind.LookFoiled => LookFoiled, AiNoteKind.HqMoved => HqMoved, AiNoteKind.HarvesterYardGone => HarvesterYardGone, AiNoteKind.BooksFailed => BooksFailed, AiNoteKind.ShieldsFelt => ShieldsFelt, AiNoteKind.FighterGrounded => FighterGrounded, AiNoteKind.WallsRefused => WallsRefused, _ => Character,
    };
}

/// <summary>
/// The simple AI's memory of its side's event feed (QA-1). The AI decides from the game state and from the events of its feed of the last few
/// weeks (lost battles, retreats, captures, bombardments, foiled looks, HQ moves, enemy characters). A save holds no events
/// (rule: m1-clock-and-orders §DEFAULT: events are not saved), so the AI copies the few events it reads into these notes and the save carries
/// them (<see cref="Setup.GameHost.SaveWithSettings"/>): a loaded game then plans exactly as the uninterrupted one. One memory per side and
/// fog setting, kept by the <see cref="Setup.GameHost"/>, so a new <see cref="SimpleAi"/> on the same game (or on a loaded one) reads the same.
/// Notes are never dropped during play (any window of any AI still reads them); a save keeps only those inside <see cref="Keep"/>.
/// </summary>
public sealed class AiMemory
{
    public Side Side { get; set; }
    public bool Fog { get; set; }
    public List<AiNote> Notes { get; set; } = new();
    public AiKeepDays Keep { get; set; } = new();
    /// <summary>
    /// The side's game plan number: drawn once per game (the first day the AI plans, from the game generator's state, which it only reads) and
    /// saved, so a loaded game keeps its plan. 0 = not drawn yet (an older save draws it on its first day after loading). rule: ai tuning,
    /// SimpleAiParams.PlanSectorSpreadPercent (AI trainer 2026-11-19).
    /// </summary>
    public ulong Plan { get; set; }
    /// <summary>The side's campaign (SimpleAiParams.Campaign, AI trainer 2026-11-26); null = none yet (an older save picks one on its next day).</summary>
    public AiCampaign? Campaign { get; set; }
    /// <summary>
    /// The side's other campaigns at once (SimpleAiParams.CampaignsAtOnce above 1, AI trainer 2026-11-26); null = none (an older save, or one
    /// campaign at a time).
    /// </summary>
    public List<AiCampaign>? MoreCampaigns { get; set; }
    /// <summary>
    /// How the side's ended fronts went, with the conditions they ran in (SimpleAiParams.FrontMemory, AI trainer 2026-11-26; Paul 2026-11-26: a
    /// plan that failed in one setting may work in another). null = none yet.
    /// </summary>
    public List<AiVerdict>? Verdicts { get; set; }

    // not saved: how far into the feed the notes go (a loaded game's feed starts empty), and the BattleStarted events of the current run of
    // same-day events by system (a battle starts and ends in one Step, and a save is made between Steps).
    private int _feedSeen;

    /// <summary>
    /// Not saved: copies the feed's events not yet noted (set by the AI that reads this memory). The AI notes new events at the start of its
    /// next PlanDay, but a save comes between a Step and that PlanDay, so the save calls this first: otherwise the events of the last Step before
    /// the save (a bombardment on the save day, say) would be missing after a load. Ingesting them at save time gives the same notes the
    /// uninterrupted game makes at its next PlanDay (same events, same state, one batch). rule: m1-clock-and-orders K12 (QA-1).
    /// </summary>
    [System.Text.Json.Serialization.JsonIgnore] internal Action? CatchUp { get; set; }
    private int _runDay = int.MinValue;
    private readonly Dictionary<string, BattleStarted> _runStarts = new(StringComparer.Ordinal); // lookup only

    /// <summary>Copy the feed's new events into notes. <paramref name="armedCapital"/>: is this unit an armed capital ship now.
    /// <paramref name="shellPower"/>: the bombardment power of these own fleets now (ShieldsFelt; null = the strike's own P).
    /// <paramref name="ownFighter"/>: is this object one of the side's squadrons (FighterGrounded; null = none noted).
    /// <paramref name="assaultAt"/>: the system of the side's own assault order of this sequence, or null (WallsRefused; null = none noted).</summary>
    public void Ingest(IReadOnlyList<GameEvent> feed, Func<int, bool> armedCapital, Func<IReadOnlyList<int>, int>? shellPower = null, Func<int, bool>? ownFighter = null,
        Func<long, string?>? assaultAt = null)
    {
        if (_feedSeen >= feed.Count) return;
        Side enemy = Side == Side.Hand ? Side.Collective : Side.Hand;
        int first = Notes.Count;
        int batchMin = int.MaxValue;
        var sources = new List<int>(); // feed index of each new note
        for (int i = _feedSeen; i < feed.Count; i++)
        {
            var e = feed[i];
            batchMin = Math.Min(batchMin, e.Day);
            if (e.Day != _runDay) { _runDay = e.Day; _runStarts.Clear(); }
            AiNote? n = e switch
            {
                BattleStarted bs => Remember(bs),
                BattleEnded be => BattleNote(be, armedCapital),
                SystemCaptured c when c.From == Side && c.To != Side => new AiNote { Kind = AiNoteKind.SystemLost, System = c.System }, // to the enemy or to neutral (B5a)
                BombardmentResolved b => new AiNote { Kind = AiNoteKind.Bombardment, System = b.System, Value = b.Regiments },
                MissionEnded me when me.Side == Side && me.Outcome == MissionOutcome.Foiled && me.MissionKind is MissionKind.Espionage or MissionKind.Reconnaissance
                    => new AiNote { Kind = AiNoteKind.LookFoiled, System = me.Target },
                MissionEnded me when me.Side == Side && me.MissionKind == MissionKind.BurnTheBooks && me.Outcome is MissionOutcome.Failed or MissionOutcome.Foiled
                    => new AiNote { Kind = AiNoteKind.BooksFailed, System = me.Target, Value = me.Outcome == MissionOutcome.Foiled ? 1 : 0 }, // 1: foiled (betrayed or sensed), 0: the roll failed
                // what the shelling side can read (Ponytail #426): S when hits got through; with none, only that fleets of this power do nothing
                // there (the batteries ground ships before the power is counted, m3-bombardment §Resolution 4 and 5.1, so P alone reads too low)
                BombardmentStarted bs when bs.Side == Side && Keep.ShieldsFelt > 0
                    => new AiNote { Kind = AiNoteKind.ShieldsFelt, System = bs.System, Value = bs.Hits > 0 ? bs.S : Math.Max(bs.P, shellPower?.Invoke(bs.Fleets.Items) ?? 0) },
                MissionSucceeded ms when ms.Side == Side && ms.MissionKind == MissionKind.Sabotage && Keep.ShieldsFelt > 0
                    => new AiNote { Kind = AiNoteKind.ShieldsFelt, System = ms.Target, Value = -1 },
                OrderRejected rj when Keep.WallsRefused > 0 && rj.Reason == Praedra.Core.Assault.AssaultReject.FullyShielded && assaultAt?.Invoke(rj.Sequence) is { } ws
                    => new AiNote { Kind = AiNoteKind.WallsRefused, System = ws },
                HqArrived or HqDeparted => new AiNote { Kind = AiNoteKind.HqMoved },
                CharacterArrived a when a.Side == enemy => At(a.Character, a.System),
                CharacterDetached d when d.Side == enemy => At(d.Character, d.System),
                SystemGeneralAssigned s when s.Side == enemy => At(s.Character, s.System),
                CharacterFreed f when f.Side == enemy => At(f.Character, f.System),
                CharacterDeparted x when x.Side == enemy => Gone(x.Character),
                CharacterCaptured x when x.Side == enemy => Gone(x.Character),
                CharacterInjured x when x.Side == enemy => Gone(x.Character),
                CharacterKilled x when x.Side == enemy => Gone(x.Character),
                ProjectCancelled pc when pc.Side == enemy && pc.Family == YardFamily.Shipyard => new AiNote { Kind = AiNoteKind.HarvesterYardGone, System = pc.System },
                HarvesterDestroyed hd when hd.Side == enemy && hd.Cause == "SabotageInYard" && hd.System != null
                    => new AiNote { Kind = AiNoteKind.HarvesterYardGone, System = hd.System },
                _ => null,
            };
            if (n == null && Keep.FighterGrounded > 0 && ownFighter != null)
            {
                // FighterGrounded: one note per squadron set down (an unload can set down several; they share the event's day and feed index)
                if (e is UnitCompleted uc && uc.Side == Side && ownFighter(uc.Object))
                    n = new AiNote { Kind = AiNoteKind.FighterGrounded, System = uc.Destination, Value = uc.Object };
                else if (e is SquadronsUnloaded su)
                    foreach (int q in su.Squadrons.Items)
                    {
                        if (!ownFighter(q)) continue;
                        if (n != null) { n.Day = n.Floor = e.Day; Notes.Add(n); sources.Add(i); }
                        n = new AiNote { Kind = AiNoteKind.FighterGrounded, System = su.System, Value = q };
                    }
            }
            if (n == null) continue;
            n.Day = n.Floor = e.Day;
            Notes.Add(n); sources.Add(i);
        }
        // Floors: older notes see the whole batch after them; a new note sees its own day and the batch events after it.
        for (int k = first - 1; k >= 0 && Notes[k].Floor > batchMin; k--) Notes[k].Floor = batchMin;
        int after = int.MaxValue, next = sources.Count - 1;
        for (int i = feed.Count - 1; i >= _feedSeen && next >= 0; i--)
        {
            while (next >= 0 && sources[next] == i) { var n = Notes[first + next]; n.Floor = Math.Min(n.Day, after); next--; } // FighterGrounded: several notes may share an event
            after = Math.Min(after, feed[i].Day);
        }
        _feedSeen = feed.Count;
    }

    private AiNote? Remember(BattleStarted bs) { _runStarts[bs.System] = bs; return null; }

    private AiNote BattleNote(BattleEnded be, Func<int, bool> armedCapital)
    {
        var r = be.Result;
        List<int>? armed = null;
        if (_runStarts.TryGetValue(r.System, out var bs))
            armed = (Side == Side.Collective ? bs.CollectiveUnits : bs.HandUnits).Items.Where(armedCapital).ToList();
        return new AiNote
        {
            Kind = AiNoteKind.Battle, System = r.System, Armed = armed,
            Own = Side == Side.Collective ? r.Collective : r.Hand, Other = Side == Side.Collective ? r.Hand : r.Collective,
        };
    }

    private static AiNote At(int character, string? system) => new() { Kind = AiNoteKind.EnemyCharacterAt, Value = character, System = system };
    private static AiNote Gone(int character) => new() { Kind = AiNoteKind.EnemyCharacterGone, Value = character };

    /// <summary>Keep each kind of note in a save for at least these days.</summary>
    public void KeepAtLeast(int battle, int systemLost, int bombardment, int lookFoiled, int hqMoved, int character, int harvesterYardGone = 0, int booksFailed = 0, int shieldsFelt = 0, int fighterGrounded = 0, int wallsRefused = 0)
    {
        Keep.WallsRefused = Math.Max(Keep.WallsRefused, wallsRefused);
        Keep.FighterGrounded = Math.Max(Keep.FighterGrounded, fighterGrounded);
        Keep.ShieldsFelt = Math.Max(Keep.ShieldsFelt, shieldsFelt);
        Keep.HarvesterYardGone = Math.Max(Keep.HarvesterYardGone, harvesterYardGone);
        Keep.BooksFailed = Math.Max(Keep.BooksFailed, booksFailed);
        Keep.Battle = Math.Max(Keep.Battle, battle); Keep.SystemLost = Math.Max(Keep.SystemLost, systemLost);
        Keep.Bombardment = Math.Max(Keep.Bombardment, bombardment); Keep.LookFoiled = Math.Max(Keep.LookFoiled, lookFoiled);
        Keep.HqMoved = Math.Max(Keep.HqMoved, hqMoved); Keep.Character = Math.Max(Keep.Character, character);
    }

    /// <summary>
    /// The notes a save keeps on <paramref name="today"/>: those a look-back can still reach. A look-back of Keep days reaches Floor &gt;= today -
    /// Keep. With fog a bombardment is also read back to the side's last look at the garrison (SimpleAi.KillsSince from the ground stamp + 1),
    /// and that stamp only grows: so a bombardment is kept while Floor &gt; the side's <paramref name="groundStamp"/> there.
    /// </summary>
    internal AiMemory ForSave(int today, Func<Side, string, int?> groundStamp) => new()
    {
        Side = Side, Fog = Fog, Keep = Keep, Plan = Plan, Campaign = Campaign?.Copy(), MoreCampaigns = MoreCampaigns?.Select(c => c.Copy()).ToList(),
        Verdicts = Verdicts?.Select(v => v with { }).ToList(),
        Notes = Notes.Where(n => (long)n.Floor >= (long)today - Keep.For(n.Kind)
            || (Fog && n.Kind == AiNoteKind.Bombardment && groundStamp(Side, n.System!) is int s && n.Floor >= s + 1)).ToList(),
    };
}
