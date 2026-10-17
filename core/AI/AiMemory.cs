using Rebellion.Core.Battle;
using Rebellion.Core.Characters;
using Rebellion.Core.Clock;
using Rebellion.Core.Control;
using Rebellion.Core.Defenses;
using Rebellion.Core.Fog;
using Rebellion.Core.Missions;

namespace Rebellion.Core.AI;

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

    public int For(AiNoteKind k) => k switch
    {
        AiNoteKind.Battle => Battle, AiNoteKind.SystemLost => SystemLost, AiNoteKind.Bombardment => Bombardment,
        AiNoteKind.LookFoiled => LookFoiled, AiNoteKind.HqMoved => HqMoved, _ => Character,
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

    // not saved: how far into the feed the notes go (a loaded game's feed starts empty), and the BattleStarted events of the current run of
    // same-day events by system (a battle starts and ends in one Step, and a save is made between Steps).
    private int _feedSeen;
    private int _runDay = int.MinValue;
    private readonly Dictionary<string, BattleStarted> _runStarts = new(StringComparer.Ordinal); // lookup only

    /// <summary>Copy the feed's new events into notes. <paramref name="armedCapital"/>: is this unit an armed capital ship now.</summary>
    public void Ingest(IReadOnlyList<GameEvent> feed, Func<int, bool> armedCapital)
    {
        if (_feedSeen >= feed.Count) return;
        Side enemy = Side == Side.Empire ? Side.Alliance : Side.Empire;
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
                SystemCaptured c when c.From == Side && c.To == enemy => new AiNote { Kind = AiNoteKind.SystemLost, System = c.System },
                BombardmentResolved b => new AiNote { Kind = AiNoteKind.Bombardment, System = b.System, Value = b.Regiments },
                MissionEnded me when me.Side == Side && me.Outcome == MissionOutcome.Foiled && me.MissionKind is MissionKind.Espionage or MissionKind.Reconnaissance
                    => new AiNote { Kind = AiNoteKind.LookFoiled, System = me.Target },
                HqArrived or HqDeparted => new AiNote { Kind = AiNoteKind.HqMoved },
                CharacterArrived a when a.Side == enemy => At(a.Character, a.System),
                CharacterDetached d when d.Side == enemy => At(d.Character, d.System),
                SystemGeneralAssigned s when s.Side == enemy => At(s.Character, s.System),
                CharacterFreed f when f.Side == enemy => At(f.Character, f.System),
                CharacterDeparted x when x.Side == enemy => Gone(x.Character),
                CharacterCaptured x when x.Side == enemy => Gone(x.Character),
                CharacterInjured x when x.Side == enemy => Gone(x.Character),
                CharacterKilled x when x.Side == enemy => Gone(x.Character),
                _ => null,
            };
            if (n == null) continue;
            n.Day = n.Floor = e.Day;
            Notes.Add(n); sources.Add(i);
        }
        // Floors: older notes see the whole batch after them; a new note sees its own day and the batch events after it.
        for (int k = first - 1; k >= 0 && Notes[k].Floor > batchMin; k--) Notes[k].Floor = batchMin;
        int after = int.MaxValue, next = sources.Count - 1;
        for (int i = feed.Count - 1; i >= _feedSeen && next >= 0; i--)
        {
            if (sources[next] == i) { var n = Notes[first + next]; n.Floor = Math.Min(n.Day, after); next--; }
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
            armed = (Side == Side.Alliance ? bs.AllianceUnits : bs.EmpireUnits).Items.Where(armedCapital).ToList();
        return new AiNote
        {
            Kind = AiNoteKind.Battle, System = r.System, Armed = armed,
            Own = Side == Side.Alliance ? r.Alliance : r.Empire, Other = Side == Side.Alliance ? r.Empire : r.Alliance,
        };
    }

    private static AiNote At(int character, string? system) => new() { Kind = AiNoteKind.EnemyCharacterAt, Value = character, System = system };
    private static AiNote Gone(int character) => new() { Kind = AiNoteKind.EnemyCharacterGone, Value = character };

    /// <summary>Keep each kind of note in a save for at least these days.</summary>
    public void KeepAtLeast(int battle, int systemLost, int bombardment, int lookFoiled, int hqMoved, int character)
    {
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
        Side = Side, Fog = Fog, Keep = Keep,
        Notes = Notes.Where(n => (long)n.Floor >= (long)today - Keep.For(n.Kind)
            || (Fog && n.Kind == AiNoteKind.Bombardment && groundStamp(Side, n.System!) is int s && n.Floor >= s + 1)).ToList(),
    };
}
