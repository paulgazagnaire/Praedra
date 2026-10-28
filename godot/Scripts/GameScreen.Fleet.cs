using System;
using System.Collections.Generic;
using System.Linq;
using Godot;
using Praedra.Core.Battle;
using Praedra.Core.Defenses;
using Praedra.Core.Economy;
using Praedra.Core.Fleets;

using Side = Praedra.Core.Clock.Side;

namespace PraedraViewer;

/// <summary>
/// The fleet panel (user stories C1, C2, C6, C7): where the fleet is and where it goes, its strength and speed, what it is made of (each ship
/// class explained on hover), its orders (move, troops, fighters, attack), its officers, and split, transfer and merge with the fleets at the
/// same system. Only the player's own fleets can be selected.
/// </summary>
public partial class GameScreen
{
    readonly HashSet<int> _splitSel = new();

    string WhereIs(Fleet f)
    {
        if (f.Transit is { } t && f.At == null)
            return $"Travelling {t.Path[t.HopIndex]} -> {t.Path[t.HopIndex + 1]} (next stop day {t.DueDay}). Heading for {t.Path[^1]}, arriving day {GameSession.ArrivalDay(t)}.";
        if (S.PlannedMoveOf(f.Id) is { } pm) return $"At {f.At}. Will leave when the next day starts: moving to {pm.To}, arrives day {pm.ArrivalDay}.";
        return $"At {f.At}.";
    }

    List<EconObject> FightersAboard(Fleet f) =>
        S.Host.Economy.Objects.Where(o => o.Category == ObjectCategory.Fighter && o.AboardShip != 0 && f.Ships.Contains(o.AboardShip)).OrderBy(o => o.Id).ToList();

    List<EconObject> FightersBasedAt(string system) => system == null ? new List<EconObject>() :
        S.Host.Economy.Objects.Where(o => o.Category == ObjectCategory.Fighter && o.Side == Me && o.Completed && o.AboardShip == 0 && o.System == system).OrderBy(o => o.Id).ToList();

    void FleetPanel(Fleet f)
    {
        var troops = S.Host.Troops;
        var eco = S.Host.Economy;
        bool atSystem = f.At != null;
        string at = f.At;
        var owner = at == null ? null : S.OwnerOf(at);
        var tags = new List<(string, Color)> { ("YOUR FLEET", Ui.You) };
        if (!atSystem) tags.Add(("MOVING", Ui.Text2));
        if (S.HoldsHarvester(f)) tags.Add(("HARVESTER", Ui.Enemy));
        var here = atSystem ? S.Game.State.Fleets.Values.Where(x => x.Side == Me && x.At == at).OrderBy(x => x.Id).ToList() : new List<Fleet> { f };
        Title(f.Name, WhereIs(f), tags.ToArray());
        if (here.Count > 1)
        {
            int i = here.FindIndex(x => x.Id == f.Id);
            var nav = Ui.HBox(6);
            nav.AddChild(Ui.Label($"Fleet {i + 1} of {here.Count} at {at}", "Hint", wrap: false));
            nav.AddChild(Ui.Grow());
            nav.AddChild(Ui.Button("Previous", () => CycleFleetHere(-1), "Shift+Tab", "Chip"));
            nav.AddChild(Ui.Button("Next fleet here", () => CycleFleetHere(1), "Tab", "Chip"));
            Add(nav);
        }
        foreach (var pend in S.PendingOf(f.Id)) Line($"Waiting for the next day: {pend}", Pending, Ui.Small);

        // ---- numbers
        var aboard = troops.RegimentsAboard(f);
        var fightersAboard = FightersAboard(f);
        using (Card())
        {
            Stat("Strength", $"{Fm.StrengthOf(f):0.0}", null, "How hard the fleet hits, measured against a reference force of three Mandates (1.0 = as strong as that). It counts ships and fighters with their damage.");
            Stat("Speed rating", $"{Fm.SpeedOf(f)}  (lower is faster)", null, "The slowest ship sets the fleet's speed. Routes multiply it: major routes 3x, minor 1.5x.");
            Stat("Ships", f.Ships.Count.ToString());
            Stat("Fighter squadrons", $"{fightersAboard.Count + f.Squadrons.Count}  (room for {Fm.FreeBerths(f)} more aboard)");
            Line($"Regiments aboard: {(aboard.Count == 0 ? "none" : Counts(aboard.Select(a => a.Regiment.Class)))}   (room for {Fm.FreeTroopSlots(f)} more)", Ui.Text2, Ui.Small);
        }

        // ---- orders
        Heading("Orders");
        var move = Act(_moveFleet == f.Id ? "Cancel move" : "Move...", () => { if (_moveFleet == f.Id) CancelMove(); else StartMove(f); },
            "Then click a destination system (or a row of a sector window). The route and arrival day show before anything is sent.", !atSystem, "Primary");
        move.Alignment = HorizontalAlignment.Center;
        if (!atSystem) Note("The fleet is travelling; give orders when it arrives.");

        if (atSystem)
        {
            // troops (C7)
            if (owner == Me)
            {
                var avail = eco.Objects.Where(o => o.Category == ObjectCategory.Regiment && o.Completed && o.Side == Me && o.System == at && o.AboardShip == 0)
                    .OrderBy(o => o.Id).Select(o => o.Id).ToList();
                int room = Fm.FreeTroopSlots(f);
                int nAll = Math.Min(avail.Count, room);
                int need = S.Host.Support.RequiredGarrison(at!); // rule: m2-support (garrison); taking regiments below it risks a revolt
                int nSafe = Math.Min(Math.Max(0, avail.Count - need), room);
                if (nAll == 0)
                    Act($"Load troops from {at} ({avail.Count} here, room for {room})", () => { }, "Needs regiments on the planet and free troop room on the fleet (transports have it).", true);
                if (nSafe > 0)
                    Act($"Load troops: {nSafe} ({avail.Count} here, room for {room}; the garrison stays)", () => LoadTroops(f, avail.Take(nSafe).ToList()),
                        $"Takes {nSafe} regiment(s) off the planet onto this fleet and leaves enough to keep order.");
                if (nAll > nSafe)
                    Act($"Load every regiment: {nAll} (the planet may revolt)", () => LoadTroops(f, avail.Take(nAll).ToList()),
                        "Takes all the regiments you have room for. Fewer than the garrison the planet needs can make the people rise up (yards and mines stop).", false, "WarnButton");
            }
            if (aboard.Count > 0)
                Act($"Unload all troops at {at}", () => UnloadTroops(f, aboard.Select(a => a.Regiment.Id).ToList()),
                    owner == Me ? "Lands the regiments on the planet: they join its garrison." : "Lands the regiments only on a system you hold. To land them here, press Invade.", owner != Me);

            // fighters (C7)
            var based = FightersBasedAt(at);
            int berths = Fm.FreeBerths(f);
            if (based.Count > 0 && berths > 0)
                Act($"Load fighters: {Math.Min(based.Count, berths)} of {based.Count} based here", () => LoadFighters(f, based.Take(Math.Min(based.Count, berths)).Select(o => o.Id).ToList()),
                    "Puts fighter squadrons based on this planet aboard the fleet's carriers.");
            if (fightersAboard.Count > 0 && owner == Me)
                Act($"Unload fighters at {at} ({fightersAboard.Count})", () => UnloadFighters(f, fightersAboard.Select(o => o.Id).ToList()), "Bases the squadrons aboard on this planet.");

            // attack
            if (owner != Me)
            {
                bool attackPending = S.PendingOf(f.Id).Any(p => p.StartsWith("Bombard", StringComparison.Ordinal) || p.StartsWith("Invade", StringComparison.Ordinal));
                Heading($"Attack {at}");
                Note("Bombard weakens the defences from orbit. Invade lands the regiments aboard to take the planet: land more than the defenders have.");
                foreach (var (cat, label, tip) in new[]
                {
                    (BombardCategory.Military, "Bombard: military targets", "Shoots at regiments and defence buildings."),
                    (BombardCategory.Civilian, "Bombard: civilian targets", "Hurts the planet's support for its owner."),
                    (BombardCategory.General, "Bombard: everything", "Shoots at everything."),
                })
                {
                    var c = cat;
                    Act(label, () => Bombard(f, c), tip, attackPending);
                }
                Act($"Invade {at} with {aboard.Count} regiment(s)", () => Invade(f), aboard.Count == 0 ? "No regiments aboard: load some at one of your planets first." : "Lands the regiments aboard to take the planet.",
                    aboard.Count == 0 || attackPending, "Primary");
                if (attackPending) Note("A fleet can carry out one attack order at a time. Press +1 day (or Play) so this one starts, then give the next.");
            }
        }

        if (atSystem && S.HoldsHarvester(f)) HarvesterSection(f);

        // ---- what it is made of (C2)
        Heading("Ships and fighters");
        var byClass = f.Ships.Select(id => eco.Find(id)).Where(o => o != null).GroupBy(o => o.Class).OrderBy(g => g.Key, StringComparer.Ordinal);
        foreach (var g in byClass)
        {
            var damaged = g.Count(o => Fm.ClassOf(o) is { } sc && o.Hull < sc.Hull);
            var r = Ui.Row($"{g.Key}", $"{g.Count()}{(damaged > 0 ? $"  ({damaged} damaged)" : "")}", damaged > 0 ? Ui.Warn : null, ClassTip(g.Key));
            Add(r);
        }
        foreach (var g in fightersAboard.Concat(f.Squadrons.Select(id => eco.Find(id)).Where(o => o != null)).GroupBy(o => o.Class).OrderBy(g => g.Key, StringComparer.Ordinal))
            Add(Ui.Row($"{g.Key} (fighters)", g.Count().ToString(), null, ClassTip(g.Key)));
        if (f.Ships.Count == 0 && fightersAboard.Count == 0 && f.Squadrons.Count == 0) Line("none", Dim);
        Line($"Ships: {Counts(f.Ships.Select(id => eco.Find(id)?.Class ?? "?"))}", Ui.Text3, Ui.Tiny);

        OfficersSection(f);

        // battle behaviour
        Heading("When an enemy fleet arrives");
        bool retreat = _retreatPending ?? S.Host.Battles.OnContactOf(Me) == OnContactMode.Retreat;
        var cb = new CheckButton { Text = "Retreat instead of fighting (all your fleets)", ButtonPressed = retreat, FocusMode = FocusModeEnum.None, AutowrapMode = TextServer.AutowrapMode.WordSmart, SizeFlagsHorizontal = SizeFlags.ExpandFill };
        cb.TooltipText = Ui.Wrap("On: your fleets pull back to a neighbouring system when an enemy arrives, instead of fighting. It applies to every fleet of yours.");
        cb.Toggled += on => SetRetreat(on);
        Add(cb);

        // split, transfer, merge (C6)
        if (atSystem)
        {
            Heading("Split, transfer or merge");
            var others = here.Where(o => o.Id != f.Id).ToList();
            if (f.Ships.Count > 1)
            {
                Note("Tick ships, then split them into a new fleet or move them into another fleet here.");
                foreach (int id in f.Ships)
                {
                    int sid = id;
                    var o = eco.Find(id);
                    var cbs = new CheckBox { Text = $"{o?.Class ?? "?"} #{id}", ButtonPressed = _splitSel.Contains(id), FocusMode = FocusModeEnum.None };
                    if (o != null && Fm.ClassOf(o) is { } sc) cbs.TooltipText = Ui.Wrap($"Hull {o.Hull} of {sc.Hull}. " + ClassTip(o.Class));
                    cbs.Toggled += on => { if (on) _splitSel.Add(sid); else _splitSel.Remove(sid); };
                    Add(cbs);
                }
                Act("Split the ticked ships into a new fleet", () => Split(f), "The ticked ships form a new fleet at this system.");
                foreach (var o in others)
                {
                    var other = o;
                    Act($"Move the ticked ships into {o.Name}", () => Transfer(f, other), "The ticked ships join that fleet; this fleet keeps the rest.");
                }
            }
            else Note("A fleet of one ship cannot be split.");
            foreach (var o in others)
            {
                var other = o;
                Act($"Merge this fleet into {o.Name}", () => Merge(f, other), "Every ship, fighter, regiment and officer joins that fleet.");
            }
        }
    }

    // ================================================================== the harvester (rule: m5-harvester §2)

    void HarvesterSection(Fleet f)
    {
        string at = f.At;
        bool gone = S.IsDestroyed(at);
        bool pending = S.PendingOf(f.Id).Any(p => p.StartsWith("Destroy System", StringComparison.Ordinal));
        Heading("harvester");
        Note("This fleet holds a harvester. Destroy System wipes out the system it is at.");
        Act("Destroy System...", () => AskDestroySystem(f.Id), "Opens a question first. The strike happens when the next day starts.", gone || pending, "DangerButton");
        if (gone) Line($"{at} is already destroyed.", Dim, Ui.Small);
        else if (pending) Line("The strike is waiting for the next day.", Pending, Ui.Small);
    }

    void AskDestroySystem(int fleetId)
    {
        if (!S.Game.State.Fleets.TryGetValue(fleetId, out var f) || f.At == null) return;
        string at = f.At;
        Confirm($"Destroy {at}?",
            $"{at} and everything on it (the planet, buildings, troops, and every enemy or neutral ship there) will be destroyed for good, and about half of the minor characters there will die. " +
            "The rest of the sector turns against the Hand, and a second strike in the same sector turns the whole sector to the Collective. It happens when the next day starts and cannot be undone.",
            "Yes, destroy it", () => DestroySystem(fleetId), danger: true);
    }

    void DestroySystem(int fleetId)
    {
        if (!S.Game.State.Fleets.TryGetValue(fleetId, out var f) || f.At == null) return;
        string at = f.At;
        S.SubmitDestroySystem(f);
        Sent($"{f.Name} destroys {at}", at); // the core judges it at the next day; a refusal comes back in the messages with its reason
    }

    // ================================================================== orders

    void Sent(string what, string system = null)
    {
        S.Say($"Order sent: {what}.", MsgKind.Info, system);
        AddLogItem(S.Messages[^1], false);
        Toast($"Order sent: {what}. It starts next day.");
        _panelDirty = true; _panelTimer = 0;
        RefreshTop();
    }

    void LoadTroops(Fleet f, List<int> ids)
    {
        if (ids.Count == 0) return;
        S.TrackFleet(f.Id, S.Host.Troops.SubmitLoad(Me, f.Id, ids), $"Load troops onto {f.Name}");
        Sent($"load {ids.Count} regiment(s) onto {f.Name}", f.At);
    }

    void UnloadTroops(Fleet f, List<int> ids)
    {
        S.TrackFleet(f.Id, S.Host.Troops.SubmitUnload(Me, f.Id, ids), $"Unload troops from {f.Name}");
        Sent($"unload {ids.Count} regiment(s) from {f.Name}", f.At);
    }

    void LoadFighters(Fleet f, List<int> ids)
    {
        if (ids.Count == 0) return;
        S.TrackFleet(f.Id, Fm.SubmitLoadSquadrons(Me, f.Id, ids), $"Load fighters onto {f.Name}");
        Sent($"load {ids.Count} fighter squadron(s) onto {f.Name}", f.At);
    }

    void UnloadFighters(Fleet f, List<int> ids)
    {
        S.TrackFleet(f.Id, Fm.SubmitUnloadSquadrons(Me, f.Id, ids), $"Unload fighters from {f.Name}");
        Sent($"unload {ids.Count} fighter squadron(s) from {f.Name}", f.At);
    }

    void Bombard(Fleet f, BombardCategory cat)
    {
        S.TrackFleet(f.Id, S.Host.Bombardment.SubmitBombard(Me, f.Id, f.At!, cat), $"Bombard {f.At} with {f.Name}");
        Sent($"{f.Name} bombards {f.At} ({cat.ToString().ToLowerInvariant()})", f.At);
    }

    void Invade(Fleet f)
    {
        S.TrackFleet(f.Id, S.Host.Assault.SubmitAssault(Me, f.Id, f.At!), $"Invade {f.At} with {f.Name}");
        Sent($"{f.Name} invades {f.At}", f.At);
    }

    void SetRetreat(bool on)
    {
        _retreatPending = on;
        S.Track(S.Host.Battles.SubmitSetOnContact(Me, on ? OnContactMode.Retreat : OnContactMode.Fight), "Change the battle behaviour");
        S.Say(on ? "Order sent: your fleets will retreat when an enemy arrives." : "Order sent: your fleets will fight when an enemy arrives.");
        AddLogItem(S.Messages[^1], false);
        RefreshTop();
    }

    List<int> Ticked(Fleet f) => _splitSel.Where(f.Ships.Contains).OrderBy(x => x).ToList();

    void Split(Fleet f)
    {
        var ids = Ticked(f);
        if (ids.Count == 0) { Toast("Tick at least one ship first.", MsgKind.Warn); return; }
        S.TrackFleet(f.Id, Fm.SubmitSplitFleet(Me, f.Id, ids), $"Split {f.Name}");
        _splitSel.Clear();
        Sent($"split {ids.Count} ship(s) off {f.Name}", f.At);
    }

    void Transfer(Fleet f, Fleet into)
    {
        var ids = Ticked(f);
        if (ids.Count == 0) { Toast("Tick at least one ship first.", MsgKind.Warn); return; }
        S.TrackFleet(f.Id, Fm.SubmitTransferShips(Me, ids, into.Id), $"Move ships from {f.Name} into {into.Name}");
        _splitSel.Clear();
        Sent($"move {ids.Count} ship(s) from {f.Name} into {into.Name}", f.At);
    }

    void Merge(Fleet f, Fleet into)
    {
        S.TrackFleet(f.Id, Fm.SubmitMergeFleets(Me, f.Id, into.Id), $"Merge {f.Name} into {into.Name}");
        Sent($"merge {f.Name} into {into.Name}", f.At);
    }
}
