using System;
using System.Collections.Generic;
using System.Linq;
using Praedra.Core.AI;
using Praedra.Core.Assault;
using Praedra.Core.Battle;
using Praedra.Core.Characters;
using Praedra.Core.Clock;
using Praedra.Core.Construction;
using Praedra.Core.Control;
using Praedra.Core.Defenses;
using Praedra.Core.Economy;
using Praedra.Core.Fleets;
using Praedra.Core.Fog;
using Praedra.Core.Setup;
using Praedra.Core.Support;
using Praedra.Core.Troops;

using Side = Praedra.Core.Clock.Side;

namespace PraedraViewer;

/// <summary>The screens' bridge, part 2: the player's side's events become log messages, popups and pause reasons (moved unchanged from GameSession.cs).</summary>
public sealed partial class GameSession
{

    // Only the events the player's side may know: the fog's filtered feed (rule: m5-fog-and-hq §N8 event audience). A battle always has both
    // sides there, so its report still reaches the player.
    void ReadEvents(StepOutcome o)
    {
        var events = Fog.EventsFor(Player);
        // QA-86: a fleet that merges on arrival is told once, as "joined", not as a fleet arriving
        _joinedNow.Clear();
        for (int k = _printed; k < events.Count; k++) if (events[k] is FleetJoined fj) _joinedNow.Add(fj.Fleet);
        for (; _printed < events.Count; _printed++)
        {
            bool combat = events[_printed] is Contact or ForcedStop or FleetRetreated or BattleEnded or SystemCaptured
                or BombardmentStarted or BombardmentResolved or BombardCancelled or AssaultStarted or AssaultResolved or AssaultCancelled
                or Praedra.Core.Harvester.SystemDestroyed or Praedra.Core.Harvester.FleetDestroyedByHarvester;
            var cat = CategoryOf(events[_printed], combat);
            string tag = events[_printed].GetType().Name;
            foreach (var m0 in Describe(events[_printed], o))
            {
                var m = m0 with { Combat = combat || m0.Combat, Cat = cat, Tag = tag };
                Messages.Add(m);
                o.New.Add(m);
            }
        }
        if (Messages.Count > 3000) Messages.RemoveRange(0, Messages.Count - 2000);
    }

    readonly HashSet<int> _joinedNow = new();

    bool Mine(Side? s) => s == Player;

    /// <summary>The log kind of an event (user story G4).</summary>
    static MsgCat CategoryOf(GameEvent e, bool combat)
    {
        if (combat) return MsgCat.Battles;
        switch (e)
        {
            case OrderRejected or OrderAccepted: return MsgCat.Orders;
            case FleetArrived or FleetJoined or FleetJoinFailed or ForcedStop or FleetRetreated or BlockadeStarted or BlockadeLifted or Contact or RegimentsLoaded or RegimentsUnloaded
                or SquadronsLoaded or SquadronsUnloaded or HqArrived: return MsgCat.Fleets;
            case FleetSighted or SensorReport or OwnerChangeSeen or HearthTipGiven or HarvesterQueuedSeen or HqSectorClueGiven: return MsgCat.Intel;
            case Praedra.Core.Research.ResearchLevelReached or AutoscrapWarning or ObjectScrapped or ProjectStarted or ProjectCancelled or UnitCompleted
                or DeliveryArrived or DeliveryRefused: return MsgCat.Build;
        }
        string ns = e.GetType().Namespace ?? "";
        if (ns.EndsWith(".Characters", StringComparison.Ordinal) || ns.EndsWith(".Missions", StringComparison.Ordinal)) return MsgCat.Leaders;
        if (ns.EndsWith(".Harvester", StringComparison.Ordinal)) return MsgCat.Intel;
        return MsgCat.Systems;
    }

    IEnumerable<Message> Describe(GameEvent e, StepOutcome o)
    {
        int d = e.Day;
        switch (e)
        {
            case OrderRejected r when _pending.TryGetValue(r.Sequence, out var what):
            {
                _pending.Remove(r.Sequence);
                string why = _missionOrders.Contains(r.Sequence) ? WhyMission(r.Reason) : _charOrders.Contains(r.Sequence) ? WhyCharacter(r.Reason) : IsHqOrder(what) ? WhyHq(r.Reason) : Why(r.Reason);
                yield return new Message(d, $"Order refused: {what}. Reason: {why}.", null, MsgKind.Bad);
                break;
            }
            case OrderAccepted a when _pending.TryGetValue(a.Sequence, out var what) && IsHqOrder(what) && a.Path != null:
                _pending.Remove(a.Sequence);
                yield return new Message(d, $"HQ moving to {a.Path.Items[^1]}, arrives day {a.ArrivalDay} ({a.TotalTicks} days on the lanes).", a.Path.Items[^1], MsgKind.Good);
                break;
            case OrderAccepted a when _pending.TryGetValue(a.Sequence, out var what):
                _pending.Remove(a.Sequence);
                if (a.Path != null)
                    yield return new Message(d, $"{what}: {a.TotalTicks} days, arrives day {a.ArrivalDay}.", a.Path.Items[^1], MsgKind.Info);
                break;

            // ---- fleets
            case FleetArrived f when _joinedNow.Contains(f.Fleet):
                break;
            case FleetJoined j when Mine(j.Side):
            {
                string who = j.Ships.Items.Count() == 1 && Host.Economy.Find(j.Ships.Items.First()) is { } ship ? (ship.Name ?? ship.Class) : FleetName(j.Fleet);
                yield return new Message(d, $"{who} joined {FleetName(j.Into)} at {j.System}.", j.System, MsgKind.Good);
                break;
            }
            case FleetArrived f when Mine(FleetSide(f.Fleet)):
                yield return new Message(d, $"{FleetName(f.Fleet)} arrived at {f.System}.", f.System, MsgKind.Info);
                break;
            case FleetJoinFailed j when Mine(j.Side):
                yield return new Message(d, j.Reason == "gone"
                    ? $"{FleetName(j.Fleet)} reached {j.System}, but {FleetName(j.Into)} no longer exists: no merge."
                    : $"{FleetName(j.Fleet)} reached {j.System}, but {FleetName(j.Into)} {(j.Reason == "moving" ? "is moving" : "had left")}: no merge.", j.System, MsgKind.Warn);
                break;
            case FleetArrived f when OwnerOf(f.System) == Player:
                yield return new Message(d, $"Enemy fleet arrived at {f.System}.", f.System, MsgKind.Warn); // no enemy fleet names (§N8)
                break;
            case FleetSighted x when OwnerOf(x.System) != Player: // at your own systems FleetArrived already says it
                yield return new Message(d, $"Enemy fleet sighted at {x.System}: {x.Detail}.", x.System, MsgKind.Warn);
                break;
            case SensorReport x: // the core sends it to the station's owner only (rule: m5-fog-and-hq §N4)
                yield return new Message(d, $"Sensors: {SensorSizeWords(x.Size)} {(x.FleetSide == Side.Hand ? "Hand" : "Collective")} fleet heading for {x.System}, expected on day {x.ReachDay}.", x.System, MsgKind.Warn);
                break;
            case OwnerChangeSeen x:
                yield return new Message(d, $"News from {x.System}: now held by {(x.Now is Side n ? "the " + NameOf(n) : "nobody")} (was {(x.Was is Side was ? "the " + NameOf(was) : "nobody")}).", x.System, MsgKind.Info);
                break;

            // ---- the Hearth (HqArrived reaches the Collective only; the departure is told by the accepted order above)
            case HqArrived h:
                yield return new Message(d, $"Your headquarters arrived at {h.System}.", h.System, MsgKind.Good);
                break;
            case HearthTipGiven x: // the core sends it to the spy's side only (rule: m5-fog-and-hq §N6a)
                yield return new Message(d, $"A spy heard where the Collective headquarters is: {x.System}.", x.System, MsgKind.Good);
                break;
            case HarvesterQueuedSeen x: // the seeing side only (rule: m5-fog-and-hq §N6b)
                yield return new Message(d, $"Intelligence reports a harvester under construction at {x.System}.", x.System, MsgKind.Bad);
                break;
            case HqSectorClueGiven x: // the spy's side only (rule: m5-fog-and-hq §N6, §N6a)
                yield return new Message(d, $"A spy heard the Collective headquarters is somewhere in sector {x.Sector}.", null, MsgKind.Good);
                break;
            case HqDestroyed h:
                yield return new Message(d, $"The Collective headquarters was destroyed at {h.System} ({HqCauseWords(h.Cause)}).", h.System, Player == Side.Hand ? MsgKind.Good : MsgKind.Bad);
                break;
            case ForcedStop x when Mine(FleetSide(x.Fleet)):
                yield return new Message(d, $"{FleetName(x.Fleet)} was stopped at {x.System} by an enemy fleet.", x.System, MsgKind.Warn);
                break;
            case BlockadeStarted b when OwnerOf(b.System) == Player && b.Blockader != Player:
                yield return new Message(d, $"{b.System} is blockaded by the enemy: mines and yards there stop.", b.System, MsgKind.Bad);
                break;
            case BlockadeLifted b when OwnerOf(b.System) == Player && b.Blockader != Player:
                yield return new Message(d, $"The blockade of {b.System} is over.", b.System, MsgKind.Good);
                break;
            case Contact c:
            {
                string key = $"{c.System}|{c.HandFleets}|{c.CollectiveFleets}";
                if (key != _lastContact)
                {
                    _lastContact = key;
                    yield return new Message(d, $"Enemy forces meet at {c.System}.", c.System, MsgKind.Warn);
                }
                break;
            }
            case FleetRetreated r:
                if (_battles.TryGetValue(r.From, out var rb))
                    rb.Retreats.Add($"{FleetName(r.Fleet)} retreated to {r.To}");
                if (Mine(FleetSide(r.Fleet)))
                    yield return new Message(d, $"{FleetName(r.Fleet)} retreated from {r.From} to {r.To}.", r.From, MsgKind.Warn);
                break;

            case SquadronsLoaded q when Mine(FleetSide(q.Fleet)):
                yield return new Message(d, $"{q.Squadrons.Items.Count()} fighter squadron(s) loaded onto {FleetName(q.Fleet)} at {q.System}.", q.System, MsgKind.Info);
                break;
            case SquadronsUnloaded q when Mine(FleetSide(q.Fleet)):
                yield return new Message(d, $"{q.Squadrons.Items.Count()} fighter squadron(s) unloaded from {FleetName(q.Fleet)} at {q.System}.", q.System, MsgKind.Info);
                break;

            // ---- the win by control (rule: m5-setup-and-victory §Victory, capital and control): told to both sides
            case Praedra.Core.Victory.DominanceChanged x:
            {
                int pct = Host.Victory.Rules.DominanceSharePercent;
                if (x.Leader is not Side lead) yield return new Message(d, "Nobody holds enough of the galaxy for a win by control any more.", null, MsgKind.Info);
                else if (lead == Player) yield return new Message(d, $"You hold {pct}% of the claimed worlds and the enemy capital has fallen: keep it until day {x.DueDay} to win.", null, MsgKind.Good);
                else yield return new Message(d, $"The {NameOf(lead)} holds {pct}% of the claimed worlds: it wins on day {x.DueDay} unless you break its hold.", null, MsgKind.Bad);
                break;
            }

            // ---- battle
            case BattleStarted b:
                _battles[b.System] = new BattleDraft { Collective = b.CollectiveUnits.Items.ToList(), Hand = b.HandUnits.Items.ToList() };
                o.PauseReason ??= "battle";
                break;
            case UnitDestroyed u when _battles.TryGetValue(u.System, out var bd):
                bd.Lost.Add((u.Unit, bd.Collective.Contains(u.Unit) ? Side.Collective : Side.Hand));
                break;
            case BattleEnded be:
                yield return BattleReport(be, o);
                break;

            // ---- characters: only the feed the player's side may see (rule: m5-fog-and-hq §N8 event audience; m5-characters-and-talent §Events)
            case CharacterCreated c:
                yield return new Message(d, Mine(c.Side) ? $"{CharName(c.Character)} joined your side{At(c.System)}." : $"A new {NameOf(c.Side)} leader, {CharName(c.Character)}, appeared{At(c.System)}.", c.System, Mine(c.Side) ? MsgKind.Good : MsgKind.Info);
                break;
            case CharacterArrived c:
                yield return new Message(d, Mine(c.Side) ? $"{CharName(c.Character)} arrived at {c.System}." : $"Enemy leader {CharName(c.Character)} arrived at {c.System}.", c.System, Mine(c.Side) ? MsgKind.Info : MsgKind.Warn);
                break;
            case CharacterJoinFailed j when Mine(j.Side):
                yield return new Message(d, j.Reason switch
                {
                    "post taken" => $"{CharName(j.Character)} reached {j.System}, but the post was taken: they ride {FleetName(j.Fleet)} as a passenger.",
                    "not free" => $"{CharName(j.Character)} reached {j.System} but could not join {FleetName(j.Fleet)}: they are not free.",
                    "gone" => $"{CharName(j.Character)} reached {j.System}, but {FleetName(j.Fleet)} no longer exists. They wait on the ground.",
                    _ => $"{CharName(j.Character)} reached {j.System}, but {FleetName(j.Fleet)} had left. They wait on the ground.",
                }, j.System, MsgKind.Warn);
                break;
            case CharacterAttached c when Mine(c.Side):
                yield return new Message(d, c.Cause == CharacterCause.Merged
                    ? $"{CharName(c.Character)} moved to {FleetName(c.Fleet)} (the fleets merged){(c.Post == CharacterPost.None ? "" : $" as {PostWords(c.Post)}")}."
                    : c.Post == CharacterPost.None ? $"{CharName(c.Character)} boarded {FleetName(c.Fleet)} as a passenger."
                    : $"{CharName(c.Character)} joined {FleetName(c.Fleet)} as {PostWords(c.Post)}.", FleetSystem(c.Fleet), MsgKind.Good);
                break;
            case CharacterDetached c when Mine(c.Side) && c.Cause is not (CharacterCause.Injured or CharacterCause.Captured or CharacterCause.Killed):
                yield return new Message(d, $"{CharName(c.Character)} left {FleetName(c.Fleet)}{At(c.System)}{CauseWords(c.Cause)}.", c.System, c.Cause == CharacterCause.Order ? MsgKind.Info : MsgKind.Warn);
                break;
            case PostVacated p when Mine(p.Side) && (p.Post == PostKind.SystemGeneral || p.Cause is not (CharacterCause.Order or CharacterCause.Injured or CharacterCause.Captured or CharacterCause.Killed)):
                // a detach by order, an injury, a capture and a death have their own line; this tells the rest (a release, a merge, a lost system)
                yield return new Message(d, $"{CharName(p.Character)} is no longer {PostKindWords(p.Post)}{CauseWords(p.Cause)}.", null, p.Cause == CharacterCause.Order ? MsgKind.Info : MsgKind.Warn);
                break;
            case SystemGeneralAssigned g when Mine(g.Side):
                yield return new Message(d, $"{CharName(g.Character)} is now the General of {g.System}: the defenders there fight better in an invasion.", g.System, MsgKind.Good);
                break;
            case CharacterInjured c:
                yield return new Message(d, Mine(c.Side) ? $"{CharName(c.Character)} was injured{At(c.System)}{CauseWords(c.Cause)}." : $"Enemy leader {CharName(c.Character)} was injured{At(c.System)}.", c.System, Mine(c.Side) ? MsgKind.Bad : MsgKind.Good);
                break;
            case CharacterHealed c when Mine(c.Side):
                yield return new Message(d, $"{CharName(c.Character)} has recovered from the injuries.", null, MsgKind.Good);
                break;
            case CharacterCaptured c:
                yield return new Message(d, Mine(c.Side) ? $"{CharName(c.Character)} was captured by the {NameOf(c.Captor)}{At(c.System)}."
                    : c.Captor == Player ? $"You captured {CharName(c.Character)} of the {NameOf(c.Side)}{At(c.System)}."
                    : $"{CharName(c.Character)} was captured{At(c.System)}.", c.System, Mine(c.Side) ? MsgKind.Bad : MsgKind.Good);
                break;
            case PrisonerLanded c when Mine(c.Side) || c.Captor == Player:
                yield return new Message(d, Mine(c.Side) ? $"{CharName(c.Character)}, a prisoner, was taken off a ship and is now held on {c.System}."
                    : $"Your fleet put the prisoner {CharName(c.Character)} ashore on {c.System}.", c.System, Mine(c.Side) ? MsgKind.Info : MsgKind.Good);
                break;
            case CharacterFreed c:
                yield return new Message(d, Mine(c.Side) ? $"{CharName(c.Character)} was freed{At(c.System)}." : $"Enemy leader {CharName(c.Character)} was freed{At(c.System)}.", c.System, Mine(c.Side) ? MsgKind.Good : MsgKind.Bad);
                break;
            case CharacterEscaped c:
                yield return new Message(d, Mine(c.Side) ? $"{CharName(c.Character)} escaped from prison{At(c.System)}." : $"Enemy leader {CharName(c.Character)} escaped from your prison{At(c.System)}.", c.System, Mine(c.Side) ? MsgKind.Good : MsgKind.Bad);
                break;
            case CharacterKilled c:
                yield return new Message(d, Mine(c.Side) ? $"{CharName(c.Character)} was killed{At(c.System)}{CauseWords(c.Cause)}." : $"Enemy leader {CharName(c.Character)} was killed{At(c.System)}.", c.System, Mine(c.Side) ? MsgKind.Bad : MsgKind.Good);
                break;
            case TalentTrainingResult t when Mine(t.Side):
                yield return new Message(d, t.Success
                    ? $"Train Talent: {CharName(t.Student)} grew in Talent under {CharName(t.Trainer)} (Talent now {t.NewTalent})."
                    : $"Train Talent: {CharName(t.Student)} made no progress with {CharName(t.Trainer)} this time.", null, t.Success ? MsgKind.Good : MsgKind.Info);
                break;
            case TalentRankChanged r when Mine(r.Side) && r.To is TalentRank to:
                yield return new Message(d, $"{CharName(r.Character)} is now a {RankWords(to)}.", null, MsgKind.Good);
                break;
            case TalentDiscovered f:
                yield return new Message(d, Mine(f.Side) ? $"{CharName(f.Character)} shows Talent! Train Talent is now possible." : $"Enemy leader {CharName(f.Character)} shows Talent.", null, Mine(f.Side) ? MsgKind.Good : MsgKind.Warn);
                break;

            // ---- research
            case Praedra.Core.Research.ResearchLevelReached r when r.Side == Player && r.Classes.Items.Any():
                yield return new Message(d, $"Research: {r.ResearchKind} level {r.Level} reached. You can now build: {string.Join(", ", r.Classes.Items)}.", null, MsgKind.Good);
                break;

            // ---- control, support
            case SystemCaptured c:
            {
                // (B5a) a world can now go neutral (To = null) or join a side by support
                string who = c.To == Player ? "You captured" : (c.From == Player ? "You lost" : c.To is Side to ? $"The {NameOf(to)} captured" : "Neutral now:");
                var kind = c.To == Player ? MsgKind.Good : (c.From == Player ? MsgKind.Bad : MsgKind.Info);
                var m = new Message(d, $"{who} {c.System}.", c.System, kind);
                if (c.To == Player || c.From == Player) { o.PauseReason ??= "captured"; o.Popup ??= m with { Title = "System captured", Detail = m.Text }; }
                yield return m;
                break;
            }
            case UprisingStarted u when u.Holder == Player:
                yield return new Message(d, $"Uprising on {u.System}! The people rise against you. Station more regiments there.", u.System, MsgKind.Bad);
                break;
            case UprisingStarted u:
                yield return new Message(d, $"Uprising against the {NameOf(u.Holder)} on {u.System}.", u.System, MsgKind.Info);
                break;
            case UprisingEnded u when OwnerOf(u.System) == Player:
                yield return new Message(d, $"The uprising on {u.System} is over.", u.System, MsgKind.Good);
                break;
            case RegimentDestroyed r when r.Side == Player && r.Reason == "CaughtAlone":
                yield return new Message(d, $"A {r.Class} travelling alone was caught and destroyed at {r.System} by an enemy interdictor.", r.System, MsgKind.Bad);
                break;
            case RegimentDestroyed r when r.Side != Player && r.Reason == "CaughtAlone":
                yield return new Message(d, $"Your fleet at {r.System} caught an enemy {r.Class} travelling alone and destroyed it.", r.System, MsgKind.Good);
                break;
            case RegimentDestroyed r when r.Side == Player:
                yield return new Message(d, $"A {r.Class} was destroyed on {r.System}.", r.System, MsgKind.Bad);
                break;
            case FacilityDestroyed f when OwnerOf(f.System) == Player:
                yield return new Message(d, $"A facility was destroyed on {f.System} ({ReasonWords(f.Reason)}).", f.System, MsgKind.Bad);
                break;
            case Disaster x when OwnerOf(x.System) == Player:
                yield return new Message(d, $"Disaster on {x.System}: {x.Destroyed.Items.Count()} facilities lost.", x.System, MsgKind.Bad);
                break;

            // ---- economy
            case AutoscrapWarning w when w.Side == Player:
                yield return new Message(d, "Maintenance is over the limit: units will be scrapped. Build refineries or scrap ships.", null, MsgKind.Warn);
                break;
            case ObjectScrapped s when s.Side == Player:
                // reasons: "Order" (the player), "Autoscrap" (upkeep over the limit), "DeliveryRefused" (told by its own DeliveryRefused message)
                switch (s.Reason)
                {
                    case "Order": yield return new Message(d, $"You scrapped a {s.Class}: {s.Refund} refined back.", null, MsgKind.Info); break;
                    case "Autoscrap": yield return new Message(d, $"Upkeep over the limit: a {s.Class} was scrapped ({s.Refund} refined back).", null, MsgKind.Warn); break;
                    case "DeliveryRefused": break;
                    default: yield return new Message(d, $"A {s.Class} was scrapped ({s.Refund} refined back).", null, MsgKind.Warn); break;
                }
                break;

            // ---- construction
            case ProjectStarted p when p.Side == Player:
                yield return new Message(d, $"Construction started at {p.System}: {p.Count} x {p.Class}.", p.System, MsgKind.Info);
                break;
            case ProjectCancelled p when p.Side == Player && p.Reason != "Replaced":
                yield return new Message(d, $"Construction at {p.System} was cancelled ({(p.Reason == "SystemDestroyed" ? "the system was destroyed" : p.Reason)}).", p.System, MsgKind.Warn);
                break;
            case UnitCompleted u when u.Side == Player:
                yield return new Message(d, $"{u.Class} completed at {u.Yard}.", u.Yard, MsgKind.Good);
                break;
            case DeliveryArrived u when ObjectIsMine(u.Object):
                yield return new Message(d, $"{u.Class} delivered to {u.System}.", u.System, MsgKind.Good);
                break;
            case DeliveryRefused u when u.Side == Player:
                yield return new Message(d, $"{u.Class} could not be delivered to {u.System} (refunded {u.Refund}).", u.System, MsgKind.Bad);
                break;

            // ---- troops
            case RegimentsLoaded t when Mine(FleetSide(t.Fleet)):
                yield return new Message(d, $"{t.Regiments.Items.Count()} regiment(s) loaded onto {FleetName(t.Fleet)} at {t.System}.", t.System, MsgKind.Info);
                break;
            case RegimentsUnloaded t when Mine(FleetSide(t.Fleet)):
                yield return new Message(d, $"{t.Regiments.Items.Count()} regiment(s) unloaded from {FleetName(t.Fleet)} at {t.System}.", t.System, MsgKind.Info);
                break;

            // ---- bombardment
            case BombardmentStarted b:
                if (b.Side == Player || OwnerOf(b.System) == Player)
                    yield return new Message(d, $"{FleetName(b.Fleet)} ({NameOf(b.Side)}) bombards {b.System}.", b.System, b.Side == Player ? MsgKind.Info : MsgKind.Bad);
                break;
            case BombardmentResolved b:
            {
                string text = b.Hits == 0
                    ? $"Bombardment of {b.System}: the shields held, no damage."
                    : $"Bombardment of {b.System}: {b.Hits} hit(s); {b.Regiments} regiment(s) and {b.Facilities} facilit(ies) destroyed" +
                      (b.Energy + b.Raw > 0 ? $", {b.Energy} energy and {b.Raw} raw slot(s) lost." : ".");
                yield return new Message(d, text, b.System, OwnerOf(b.System) == Player ? MsgKind.Bad : MsgKind.Info);
                break;
            }
            case BombardCancelled b when Mine(FleetSide(b.Fleet)):
                yield return new Message(d, $"Bombardment of {b.System} by {FleetName(b.Fleet)} was cancelled ({Why(b.Reason)}).", b.System, MsgKind.Warn);
                break;

            // ---- assault
            case AssaultStarted a:
            {
                bool youDefend = OwnerOf(a.System) == Player && a.Side != Player;
                var m = new Message(d, youDefend ? $"The {NameOf(a.Side)} is invading {a.System}! {a.A0} attackers against your {a.D0} defenders." :
                    $"Invasion of {a.System}: {a.A0} attackers against {a.D0} defenders.", a.System, youDefend ? MsgKind.Bad : MsgKind.Info);
                if (youDefend) o.PauseReason ??= "invaded";
                if (youDefend || a.Side == Player) o.PauseReason ??= "invasion";
                yield return m;
                break;
            }
            case AssaultResolved a:
            {
                bool won = a.Result == "Won";
                string text = $"Invasion of {a.System} by the {NameOf(a.Side)}: {(won ? "the attackers won" : "the defenders held")}. " +
                              $"Attackers {a.A0} -> {a.A} regiments, defenders {a.D0} -> {a.D}.";
                bool involved = a.Side == Player || OwnerOf(a.System) == Player || (won && false);
                var kind = (a.Side == Player) == won ? MsgKind.Good : MsgKind.Bad;
                var m = new Message(d, text, a.System, kind, "Invasion report", text);
                if (involved) o.Popup ??= m;
                yield return m;
                break;
            }
            case AssaultCancelled a when Mine(FleetSide(a.Fleet)):
                yield return new Message(d, $"Invasion of {a.System} by {FleetName(a.Fleet)} was cancelled ({Why(a.Reason)}).", a.System, MsgKind.Warn);
                break;

            // ---- the harvester (GameSession.Harvester.cs); the clock pauses on a destroyed system like on a capture
            case Praedra.Core.Harvester.SystemDestroyed:
                foreach (var m in DescribeHarvester(e))
                {
                    var (title, detail) = HarvestReport((Praedra.Core.Harvester.SystemDestroyed)e); // rule: m5-harvester §7 Harvest report
                    var report = m with { Title = title, Detail = detail };
                    // QA-98: the harvest report wins the day's popup over a battle or a capture the same day (those stay a click away in the log),
                    // and the log line carries the report too, so it can be opened again later
                    if (o.PauseReason != "destroyed") { o.PauseReason = "destroyed"; o.Popup = report; }
                    yield return report;
                }
                break;
            case Praedra.Core.Harvester.IHarvesterEvent or Praedra.Core.Harvester.DestroySystemCancelled:
                foreach (var m in DescribeHarvester(e)) yield return m;
                break;

            // ---- missions (GameSession.Missions.cs)
            default:
                foreach (var m in DescribeMission(e)) yield return m;
                break;
        }
    }

    bool ObjectIsMine(int objectId) => Host.Economy.Find(objectId)?.Side == Player;

    static string ReasonWords(DestroyReason r) => r switch
    {
        DestroyReason.OverEnergy => "not enough energy slots",
        DestroyReason.OverRaw => "not enough raw slots",
        DestroyReason.Disaster => "disaster",
        DestroyReason.UprisingIncident => "uprising",
        DestroyReason.Bombardment => "bombardment",
        DestroyReason.Assault => "invasion",
        DestroyReason.Sabotage => "sabotage by enemy agents",
        DestroyReason.Harvester => "harvester",
        _ => r.ToString(),
    };

    Message BattleReport(BattleEnded e, StepOutcome o)
    {
        var r = e.Result;
        _battles.TryGetValue(r.System, out var draft);
        draft ??= new BattleDraft();
        _battles.Remove(r.System);

        string SideLine(Side s, List<int> units)
        {
            string fleets = FleetsOf(units);
            return $"{You(s)}: {(fleets.Length > 0 ? fleets + " - " : "")}{Group(units)}";
        }
        string Lost(Side s)
        {
            var ids = draft.Lost.Where(l => l.Side == s).Select(l => l.Unit).ToList();
            return ids.Count == 0 ? "no losses" : $"lost {ids.Count}: {Group(ids)}";
        }
        string Outcome(BattleOutcome oc) => oc switch
        {
            BattleOutcome.Victorious => "victorious",
            BattleOutcome.Annihilated => "destroyed",
            BattleOutcome.Retreated => "retreated",
            _ => "no decision",
        };
        string verdict = r.Collective == BattleOutcome.Victorious ? "The Collective won."
            : r.Hand == BattleOutcome.Victorious ? "The Hand won."
            : "Nobody won.";
        bool youWon = (Player == Side.Collective ? r.Collective : r.Hand) == BattleOutcome.Victorious;
        bool youLost = (Player == Side.Collective ? r.Hand : r.Collective) == BattleOutcome.Victorious;
        string detail =
            $"Battle of {r.System}, day {e.Day}\n\n" +
            SideLine(Side.Collective, draft.Collective) + "\n" +
            SideLine(Side.Hand, draft.Hand) + "\n\n" +
            $"{verdict}\n" +
            $"Collective ({Outcome(r.Collective)}): {Lost(Side.Collective)}; {r.CollectiveSurvivors} survivor(s).\n" +
            $"Hand ({Outcome(r.Hand)}): {Lost(Side.Hand)}; {r.HandSurvivors} survivor(s).";
        if (draft.Retreats.Count > 0) detail += "\n\n" + string.Join("\n", draft.Retreats) + ".";
        string line = $"Battle at {r.System}: {(youWon ? "you won" : youLost ? "you lost" : verdict.TrimEnd('.').ToLowerInvariant())}. " +
                      $"Collective losses {r.CollectiveLosses}, Hand losses {r.HandLosses}. (click for the report)";
        var m = new Message(e.Day, line, r.System, MsgKind.Battle, $"Battle at {r.System}", detail);
        if (o.PauseReason != "destroyed") { o.Popup = m; o.PauseReason = "battle"; } // QA-98: a harvest the same day keeps the popup
        return m;
    }
}
