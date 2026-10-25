using System;
using System.Collections.Generic;
using System.Linq;
using Praedra.Core.Characters;
using Praedra.Core.Clock;
using Praedra.Core.Economy;
using Praedra.Core.Fog;
using Praedra.Core.Missions;
using Praedra.Core.Support;

using Side = Praedra.Core.Clock.Side;

namespace PraedraViewer;

/// <summary>
/// Missions as the screens see them (rule: m5-missions): the player's own mission groups (own state), the words for the kinds, phases and
/// refusals, and the mission events of the fog-filtered feed turned into plain messages. It decides no rule: what a team may do is asked of the
/// core (<see cref="MissionModule.CheckStart"/>), and the orders are judged by the core when the next day starts.
/// </summary>
public sealed partial class GameSession
{
    public MissionModule Missions => Host.Missions;

    // Queued StartMission / CancelMission orders: their refusals use WhyMission; the queued starts are listed as "waiting" in the Missions list.
    readonly HashSet<long> _missionOrders = new();
    readonly Dictionary<long, PendingMission> _pendingMissions = new();
    // What each of the player's missions is (kind, target, team names), kept from the day before so the messages can name a team that is gone.
    readonly Dictionary<int, (MissionKind Kind, string Target, string Team)> _missionInfo = new();
    (int Mission, MissionKind Kind, string Target)? _lastSuccess;

    /// <summary>A StartMission order given today and not yet judged.</summary>
    public sealed record PendingMission(long Sequence, MissionKind Kind, IReadOnlyList<int> Characters, IReadOnlyList<int> SpecialForces, string Home, string Target, int ArrivalDay);

    public IEnumerable<PendingMission> PendingMissions => _pendingMissions.Values.OrderBy(p => p.Sequence);

    /// <summary>The player's running missions (own state), in mission id order; Move groups are left out (the screens do not give them).</summary>
    public IReadOnlyList<MissionGroup> MyMissions => Missions.Missions.Where(m => m.Side == Player && m.Kind != MissionKind.Move).OrderBy(m => m.Id).ToList();

    /// <summary>The player's mission group a character is in, or null.</summary>
    public MissionGroup MissionOf(int character) =>
        Missions.Missions.FirstOrDefault(m => m.Side == Player && (m.Characters.Contains(character) || m.DecoyCharacters.Contains(character)));

    public MissionGroup MissionOfSpecialForce(int unit) =>
        Missions.Missions.FirstOrDefault(m => m.Side == Player && (m.SpecialForces.Contains(unit) || m.DecoySpecialForces.Contains(unit)));

    /// <summary>Is the character in a queued (not yet judged) StartMission order?</summary>
    public PendingMission PendingMissionOf(int character) => _pendingMissions.Values.FirstOrDefault(p => p.Characters.Contains(character));
    public PendingMission PendingMissionOfSpecialForce(int unit) => _pendingMissions.Values.FirstOrDefault(p => p.SpecialForces.Contains(unit));

    /// <summary>Queue a StartMission order (the core judges it when the next day starts; a refusal comes back in plain words).</summary>
    public long SubmitMission(MissionKind kind, IReadOnlyList<int> chars, IReadOnlyList<int> sfs, string target, int? targetCharacter, string home, int arrivalDay)
    {
        long seq = Game.Submit(s => new StartMissionOrder(s, Player, kind, chars.ToList(), sfs.ToList(), new List<int>(), targetCharacter == null ? target : null, targetCharacter));
        _missionOrders.Add(seq);
        _pendingMissions[seq] = new PendingMission(seq, kind, chars.ToList(), sfs.ToList(), home, target, arrivalDay);
        return Track(seq, $"{KindWords(kind)} on {target} by {TeamWords(chars, sfs)}");
    }

    /// <summary>Queue a CancelMission order. rule: m5-missions §Orders (CancelMission): only while the team works at the target.</summary>
    public long SubmitCancelMission(MissionGroup m)
    {
        int id = m.Id;
        long seq = Game.Submit(s => new CancelMissionOrder(s, Player, id));
        _missionOrders.Add(seq);
        return Track(seq, $"Cancel the {KindWords(m.Kind)} mission on {m.Target}");
    }

    /// <summary>A cancel order is waiting for this mission.</summary>
    public bool CancelPending(MissionGroup m) => _pending.Values.Any(w => w == $"Cancel the {KindWords(m.Kind)} mission on {m.Target}");

    // ------------------------------------------------------------------ words

    public static string KindWords(MissionKind k) => k switch
    {
        MissionKind.InciteUprising => "Incite uprising",
        MissionKind.SubdueUprising => "Subdue uprising",
        MissionKind.DeathStarSabotage => "Death Star sabotage",
        MissionKind.JediTraining => "Jedi training",
        MissionKind.ShipResearch => "Ship research",
        MissionKind.FacilityResearch => "Facility research",
        MissionKind.TroopResearch => "Troop research",
        _ => k.ToString(),
    };

    /// <summary>What the mission does and where it may go, one line (from the rule sheet's effects and target legality). rule: m5-missions §Targets, §6.</summary>
    public static string KindHelp(MissionKind k) => k switch
    {
        MissionKind.Diplomacy => "Raises support for you at a neutral system or one of yours (not in revolt). Keeps trying until you cancel it. Skill: diplomacy.",
        MissionKind.Espionage => "Any system: on success you see its facilities, troops and fleets (and maybe more of its side's systems). Skill: espionage.",
        MissionKind.Reconnaissance => "Any system: a look at it. Succeeds by itself unless the team is caught. Skill: espionage.",
        MissionKind.Sabotage => "An enemy or neutral system: destroys one facility. Very hard: the team is usually caught. Skill: combat and espionage.",
        MissionKind.InciteUprising => "An enemy system: stirs up unrest against its holder. Keeps working until you cancel it. Skill: leadership.",
        MissionKind.SubdueUprising => "One of your systems in revolt: calms the people and raises your support. Skill: leadership.",
        MissionKind.ShipResearch or MissionKind.FacilityResearch or MissionKind.TroopResearch =>
            "One of your systems with the matching yard (shipyard, construction yard or training facility): research points; repeats until you cancel it.",
        MissionKind.Rescue => "Frees one of your captured characters where the enemy holds him or her. Skill: combat.",
        MissionKind.Abduction => "Captures an enemy leader. Skill: combat.",
        MissionKind.Assassination => "Kills an enemy leader. Skill: combat.",
        MissionKind.Recruitment => "Brings a new leader to your side at one of your systems.",
        MissionKind.DeathStarSabotage => "Destroys an enemy Death Star.",
        MissionKind.JediTraining => "Trains a Force user.",
        _ => "",
    };

    /// <summary>
    /// Why a kind cannot be ordered at all yet, whatever the team (the sheet's Missing rules, and character targets the side cannot see), or null.
    /// rule: m5-missions §Missing rules; Lead notes from the build 2, 3, 4, 6; m5-fog-and-hq §N8 (enemy leaders' whereabouts are not in the picture).
    /// </summary>
    public string KindBlockedReason(MissionKind k) => k switch
    {
        MissionKind.Recruitment when Missions.Recruit == null => "not available yet: the rule book does not say which leader gets recruited",
        MissionKind.JediTraining => "not available yet: the rule book does not say yet who runs Jedi training",
        MissionKind.DeathStarSabotage when Missions.HasDeathStar == null => "not available yet: there is no Death Star in this game",
        MissionKind.Abduction or MissionKind.Assassination => "not available here: your side cannot see where enemy leaders are",
        _ => null,
    };

    /// <summary>The words for a refused mission order (every MissionReject code, and the shared reasons the mission orders use). rule: m5-missions §Orders.</summary>
    public static string WhyMission(string reason) => reason switch
    {
        MissionReject.UnknownKind => "that kind of mission does not exist",
        MissionReject.HiddenKind => "that is a story mission and cannot be ordered",
        MissionReject.WrongSide => "your side cannot carry out that mission",
        MissionReject.UnknownMember => "a team member is no longer in the game",
        MissionReject.DuplicateMember => "a team member was picked twice",
        MissionReject.NoTeam => "the team is empty (pick at least one character or special forces unit)",
        MissionReject.NotFree => "a team member is busy (injured, captured, travelling, aboard a fleet, a system General, or already on a mission)",
        MissionReject.NotTogether => "the team members are not all at the same system",
        MissionReject.NotAbleToDo => "a team member does not have the skill this mission needs",
        MissionReject.NotEligibleSpecialForce => "a special forces unit in the team cannot do this kind of mission",
        MissionReject.BadDecoy => "decoys cannot go on this mission",
        MissionReject.TooManyMembers => "the team is too big",
        MissionReject.BadTarget => "that target is not allowed for this mission",
        MissionReject.NoDeathStar => "there is no enemy Death Star there",
        MissionReject.UnknownMission => "that mission is already over",
        MissionReject.RuleMissing => "the rule book does not cover this mission yet",
        MissionReject.TargetProtected => "that leader is out of reach: aboard a fleet, or at a capital whose garrison is full",
        RejectReason.NotYours => "a team member is not yours",
        RejectReason.NotCompleted => "a special forces unit is still in training",
        RejectReason.Unreachable => "there is no route to the target",
        RejectReason.InTransit => "the team is still travelling (a mission can be cancelled once the team is at the target)",
        RejectReason.NothingToResearch => "there is nothing left to research on that track",
        RejectReason.NoMatchingYard => "research needs one of your systems with the matching yard",
        _ => Why(reason),
    };

    public string SpecialForceName(int unit)
    {
        var o = Host.Economy.Find(unit);
        string cls = o?.Class ?? (_classOf.TryGetValue(unit, out var c) ? c : "special forces");
        return $"{cls} #{unit}";
    }

    public string TeamWords(IEnumerable<int> chars, IEnumerable<int> sfs)
    {
        var names = chars.Select(CharName).Concat(sfs.Select(SpecialForceName)).ToList();
        return names.Count == 0 ? "nobody" : string.Join(", ", names);
    }

    public string TeamOf(MissionGroup m) => TeamWords(m.Characters, m.SpecialForces);

    /// <summary>The phase of one of the player's missions in words, with the day that matters. rule: m5-missions §Rule (Classic) phases.</summary>
    public string PhaseWords(MissionGroup m) => m.Phase switch
    {
        MissionPhase.Outbound when m.TotalTicks == 0 => $"already at {m.Target}, starting work",
        MissionPhase.Outbound => $"travelling out to {m.Target}, arrives day {m.ArrivalDay}",
        MissionPhase.Working => $"at {m.Target}, next result due day {m.Due}",
        MissionPhase.ReturnPending => $"finished ({OutcomeWords(m.Outcome)}), leaving for home",
        MissionPhase.Returning => $"returning to {m.Path[^1]}, arrives day {m.ArrivalDay}",
        _ => m.Phase.ToString(),
    };

    public static string OutcomeWords(MissionOutcome o) => o switch
    {
        MissionOutcome.Succeeded => "succeeded",
        MissionOutcome.Failed => "failed",
        MissionOutcome.Foiled => "foiled by the enemy",
        MissionOutcome.Cancelled => "cancelled",
        MissionOutcome.TargetInvalid => "called off: the target is no longer valid",
        MissionOutcome.TargetDestroyed => "the target system was destroyed",
        _ => "",
    };

    string MissionWhere(MissionGroup m) => $"on a {KindWords(m.Kind)} mission to {m.Target}: {PhaseWords(m)}";

    string MissionMapPlace(MissionGroup m) => m.Path.Count == 0 ? m.Target : m.Path[^1];

    void SnapshotMissions()
    {
        foreach (var m in Missions.Missions)
            if (m.Side == Player && m.Kind != MissionKind.Move) _missionInfo[m.Id] = (m.Kind, m.Target, TeamOf(m));
    }

    string TeamOfId(int mission) => _missionInfo.TryGetValue(mission, out var i) ? i.Team : "your team";

    // ------------------------------------------------------------------ events -> messages (only the fog-filtered feed: rule m5-fog-and-hq §N8)

    // The mission side's own events name the target as Target, so only that side reads them; the defender learns of agents only through
    // AgentDetected (fog §N7 3) or the visible effects. rule: m5-missions §Events (audience).
    IEnumerable<Message> DescribeMission(GameEvent e)
    {
        int d = e.Day;
        switch (e)
        {
            case MissionStarted s when Mine(s.Side) && s.MissionKind != MissionKind.Move:
            {
                string team = TeamWords(s.Characters.Items, s.SpecialForces.Items);
                _missionInfo[s.Mission] = (s.MissionKind, s.Target, team);
                yield return new Message(d, s.TotalTicks == 0
                    ? $"Mission started: {KindWords(s.MissionKind)} on {s.Target} by {team} (already there)."
                    : $"Mission started: {KindWords(s.MissionKind)} on {s.Target} by {team}: {s.TotalTicks} days, arrives day {s.ArrivalDay}.", s.Target, MsgKind.Info);
                break;
            }
            case MissionLanded l when Mine(l.Side) && _missionInfo.TryGetValue(l.Mission, out var li):
                yield return new Message(d, $"{li.Team} reached {l.Target} ({KindWords(li.Kind)}): first result due day {l.Due}.", l.Target, MsgKind.Info);
                break;
            case MissionSucceeded s when Mine(s.Side):
                _lastSuccess = (s.Mission, s.MissionKind, s.Target);
                yield return new Message(d, SuccessWords(s), s.Target, MsgKind.Good);
                break;
            case MissionIterationDone x when Mine(x.Side) && !x.Success && _missionInfo.TryGetValue(x.Mission, out var xi):
                yield return new Message(d, $"{KindWords(xi.Kind)} on {x.Target}: no success this time. The team keeps trying; next result due day {x.Due}.", x.Target, MsgKind.Info);
                break;
            case MissionFailed f when Mine(f.Side):
                yield return new Message(d, $"{KindWords(f.MissionKind)} on {f.Target} failed: {TeamOfId(f.Mission)} did not succeed and comes home.", f.Target, MsgKind.Bad);
                break;
            case MissionFoiled f when Mine(f.Side):
            {
                var kind = _missionInfo.TryGetValue(f.Mission, out var fi) ? KindWords(fi.Kind) : "Mission";
                yield return new Message(d, $"{kind} on {f.System} was foiled: the enemy ({f.DefenderClass}) detected {TeamOfId(f.Mission)}. The team comes home.", f.System, MsgKind.Bad);
                break;
            }
            case MissionEnded x when Mine(x.Side) && x.Outcome is MissionOutcome.Cancelled or MissionOutcome.TargetInvalid:
                yield return new Message(d, x.Outcome == MissionOutcome.Cancelled
                    ? $"{KindWords(x.MissionKind)} on {x.Target} cancelled: {TeamOfId(x.Mission)} comes home."
                    : $"{KindWords(x.MissionKind)} on {x.Target} called off: the target is no longer valid. The team comes home.", x.Target, MsgKind.Warn);
                break;
            case MissionEnded x when Mine(x.Side) && x.Outcome == MissionOutcome.TargetDestroyed:
                yield return new Message(d, $"{KindWords(x.MissionKind)} on {x.Target} failed: the system was destroyed by the Death Star.", x.Target, MsgKind.Bad);
                break;
            case MissionEnded x when Mine(x.Side) && x.Outcome == MissionOutcome.Succeeded:
                break; // the success line already said it
            case MissionReturned r when Mine(r.Side):
            {
                string team = TeamOfId(r.Mission);
                _missionInfo.Remove(r.Mission);
                yield return new Message(d, $"{team} {(team.Contains(',') ? "are" : "is")} back at {r.At} and free for new orders.", r.At, MsgKind.Info);
                break;
            }
            case SabotageNoTarget n when Mine(n.Side):
                yield return new Message(d, $"Sabotage on {n.Target}: there was no finished facility to destroy.", n.Target, MsgKind.Warn);
                break;
            case MissionSkillGained g when Mine(g.Side):
                yield return new Message(d, $"{CharName(g.Character)} learned from the research: {SkillWords(g.Skill)} now {g.NewValue}.", null, MsgKind.Good);
                break;
            case AgentDetected a when a.Viewer == Player:
                yield return new Message(d, $"Enemy agents detected at {a.System}! Your forces there foiled their mission.", a.System, MsgKind.Warn);
                break;
            case FacilityDestroyed f when f.Reason == DestroyReason.Sabotage && _lastSuccess is { Kind: MissionKind.Sabotage } ls && ls.Target == f.System:
                yield return new Message(d, $"Your agents destroyed a {(_classOf.TryGetValue(f.Facility, out var fc) ? fc : "facility")} on {f.System}.", f.System, MsgKind.Good);
                break;
            case SupportChanged sc when sc.Cause is SupportCause.Diplomacy or SupportCause.Subdue && _lastSuccess is { } ls2 && ls2.Target == sc.System:
            {
                int was = Player == Side.Collective ? sc.Old : 100 - sc.Old, now = Player == Side.Collective ? sc.New : 100 - sc.New;
                yield return new Message(d, $"Support for you on {sc.System}: now {now}% (was {was}%).", sc.System, now >= was ? MsgKind.Good : MsgKind.Bad);
                break;
            }
        }
    }

    static string SkillWords(Skill k) => k switch
    {
        Skill.ShipDesign => "ship design", Skill.FacilityDesign => "facility design", Skill.TroopTraining => "troop training",
        _ => k.ToString().ToLowerInvariant(),
    };

    string SuccessWords(MissionSucceeded s) => s.MissionKind switch
    {
        MissionKind.Espionage => $"Espionage on {s.Target} succeeded: you now see its facilities, troops and fleets.",
        MissionKind.Reconnaissance => $"Reconnaissance of {s.Target} succeeded: you now see the system.",
        MissionKind.Diplomacy => $"Diplomacy on {s.Target} succeeded: support for you rises. The team keeps working.",
        MissionKind.Sabotage => $"Sabotage on {s.Target} succeeded.",
        MissionKind.InciteUprising => $"Incite uprising on {s.Target} succeeded: unrest against the holder grows.",
        MissionKind.SubdueUprising => $"Subdue uprising on {s.Target} succeeded: the people calm down.",
        MissionKind.ShipResearch or MissionKind.FacilityResearch or MissionKind.TroopResearch => $"{KindWords(s.MissionKind)} at {s.Target} succeeded: research points gained.",
        MissionKind.Rescue => $"Rescue at {s.Target} succeeded: your character is free.",
        _ => $"{KindWords(s.MissionKind)} on {s.Target} succeeded.",
    };
}
