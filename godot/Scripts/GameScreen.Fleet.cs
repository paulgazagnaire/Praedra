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
/// class explained on hover), its orders (move, troops, fighters; attack, the harvester, fight or retreat and scrap are in the Fleet window's Orders tab, one
/// click away), its officers, and split, transfer and merge with the fleets at the
/// same system. Only the player's own fleets can be selected.
/// </summary>
public partial class GameScreen
{
    readonly HashSet<int> _splitSel = new();

    string WhereIs(Fleet f)
    {
        if (f.Transit is { } t && f.At == null)
            return $"Travelling {t.Path[t.HopIndex]} -> {t.Path[t.HopIndex + 1]} (next stop day {t.DueDay}). Heading for {t.Path[^1]}, arriving day {GameSession.ArrivalDay(t)}.";
        if (S.PlannedMoveOf(f.Id) is { } pm) return $"At {f.At}. Leaving for {pm.To}, arrives day {pm.ArrivalDay}.";
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
        else if (S.PlannedMoveOf(f.Id) != null) tags.Add(("LEAVING", Ui.Warn));
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
        foreach (var (seq, pend) in S.PendingOrdersOf(f.Id)) OrderLine(seq, $"Ordered: {pend}", Ui.Small);

        // ---- numbers
        var aboard = troops.RegimentsAboard(f);
        var fightersAboard = FightersAboard(f);
        using (Card())
        {
            Stat("Strength", $"{Fm.StrengthOf(f):0}", null, "How hard the fleet hits: its damage rate against a reference force of three Mandates, with its damage counted. Three Decrees score about 65.");
            Stat("Speed rating", $"{Fm.SpeedOf(f)}  (lower is faster)", null, "The slowest ship sets the fleet's speed. Routes multiply it: major routes 3x, minor 1.5x.");
            Stat("Ships", f.Ships.Count.ToString());
            int sq = fightersAboard.Count + f.Squadrons.Count, berths0 = Fm.FreeBerths(f), room0 = Fm.FreeTroopSlots(f);
            Stat("Fighter squadrons", sq == 0 && berths0 == 0 ? "none (no hangar)" : berths0 > 0 ? $"{sq}  (room for {berths0} more)" : sq.ToString());
            if (aboard.Count == 0 && room0 == 0) Line("No troop room: transports carry regiments.", Ui.Text3, Ui.Small);
            else Line($"Regiments aboard: {(aboard.Count == 0 ? "none" : Counts(aboard.Select(a => a.Regiment.Class)))}   (room for {room0} more)", Ui.Text2, Ui.Small);
        }

        // ---- orders
        Heading("Orders");
        var move = Act(_moveFleet == f.Id ? "Cancel move" : "Move...", () => { if (_moveFleet == f.Id) CancelMove(); else StartMove(f); },
            "Then click a destination system (or a row of a sector window). The route and arrival day show before anything is sent.", !atSystem, "Primary");
        move.Alignment = HorizontalAlignment.Center;
        if (!atSystem) Note("The fleet is travelling; give orders when it arrives.");
        // attack, the harvester's strike, merge, fight or retreat and scrap live in the fleet's window only (Paul 2026-11-14 pick 5, 2026-11-21)
        Act("Fleet window: attack, merge, ships...", () => OpenMgmt(MgKind.Fleet, fleet: f.Id),
            "Opens the fleet's window: bombard, invade" + (S.HoldsHarvester(f) ? ", the harvester's strike" : "") + ", merge with another fleet, its ships one by one, what they carry, fight or retreat, and scrap.")
            .SetMeta(UiTest.FullName, "Fleet orders");

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
                if (nAll == 0 && room > 0) Note($"No regiments at {at} to load.");
                if (nSafe > 0)
                    Act($"Load troops: {nSafe}, the garrison stays", () => LoadTroops(f, avail.Take(nSafe).ToList()),
                        $"Takes {nSafe} of the {avail.Count} regiment(s) here onto this fleet (room for {room}) and leaves enough to keep order.");
                if (nAll > nSafe)
                    Act($"Load every regiment: {nAll} (the planet may revolt)", () => LoadTroops(f, avail.Take(nAll).ToList()),
                        "Takes all the regiments you have room for. Fewer than the garrison the planet needs can make the people rise up (yards and mines stop).", false, "WarnButton");
            }
            if (aboard.Count > 0)
                Act($"Unload all troops at {at}", () => UnloadTroops(f, aboard.Select(a => a.Regiment.Id).ToList()),
                    owner == Me ? "Lands the regiments on the planet: they join its garrison."
                    : S.IsDestroyed(at) ? $"Nowhere to land them: {at} is destroyed."
                    : "Lands the regiments only on a system you hold. To land them here, invade it from the fleet's window.", owner != Me);

            // fighters (C7)
            var based = FightersBasedAt(at);
            int berths = Fm.FreeBerths(f);
            if (based.Count > 0 && berths > 0)
                Act($"Load fighters: {Math.Min(based.Count, berths)} of {based.Count} based here", () => LoadFighters(f, based.Take(Math.Min(based.Count, berths)).Select(o => o.Id).ToList()),
                    "Puts fighter squadrons based on this planet aboard the fleet's carriers.");
            if (fightersAboard.Count > 0 && owner == Me)
                Act($"Unload fighters at {at} ({fightersAboard.Count})", () => UnloadFighters(f, fightersAboard.Select(o => o.Id).ToList()), "Bases the squadrons aboard on this planet.");
        }

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

        OfficersSection(f);

        // split, transfer, merge (C6)
        if (atSystem)
        {
            Heading("Split, transfer, merge or scrap", "Tick ships, then split them into a new fleet, move them into another fleet here, or scrap them.");
            var others = here.Where(o => o.Id != f.Id).ToList();
            foreach (int id in f.Ships)
            {
                int sid = id;
                var o = eco.Find(id);
                var cbs = new CheckBox { Text = $"{o?.Class ?? "?"} #{id}", ButtonPressed = _splitSel.Contains(id), FocusMode = FocusModeEnum.None };
                if (o != null && Fm.ClassOf(o) is { } sc) cbs.TooltipText = Ui.Wrap($"Hull {o.Hull} of {sc.Hull}. Upkeep {UpkeepOf(o)}. " + ClassTip(o.Class));
                cbs.Toggled += on => { if (on) _splitSel.Add(sid); else _splitSel.Remove(sid); };
                Add(cbs);
            }
            if (f.Ships.Count > 1)
            {
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
            // scrapping cuts upkeep (story C9; rule: m2-maintenance §Scrapping and refunds)
            Act("Scrap the ticked ships...", () => ScrapTicked(f), "Asks first. Half the refined cost comes back and their upkeep is freed.", false, "DangerButton");
        }
    }

    // ================================================================== the harvester (rule: m5-harvester §2)

    void AskDestroySystem(int fleetId)
    {
        if (!S.Game.State.Fleets.TryGetValue(fleetId, out var f) || f.At == null) return;
        string at = f.At;
        Confirm($"Destroy {at}?",
            $"{at} and everything on it (the planet, buildings, troops, and every enemy or neutral ship there) will be destroyed for good, and about half of the minor characters there will die. " +
            "The rest of the sector turns against the Hand, and a second strike in the same sector turns the whole sector to the Collective. It happens as soon as the clock moves and cannot be undone after that.",
            "Yes, destroy it", () => DestroySystem(fleetId), danger: true);
    }

    void DestroySystem(int fleetId)
    {
        if (!S.Game.State.Fleets.TryGetValue(fleetId, out var f) || f.At == null) return;
        string at = f.At;
        S.SubmitDestroySystem(f);
        Sent($"{f.Name} destroys {at}", at); // the core judges it when the clock moves; a refusal comes back in the messages with its reason
    }

    // ================================================================== orders

    void Sent(string what, string system = null)
    {
        S.Say($"Order sent: {what}.", MsgKind.Info, system);
        AddLogItem(S.Messages[^1], false);
        if (!MgDropping) Toast($"Order given: {what}.");   // a window drop shows itself at once
        _panelDirty = true; _panelTimer = 0;
        RefreshMgmt();
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

    /// <summary>One fleet's answer when an enemy fleet arrives: Elusive (Retreat) or Defensive (Fight). rule: m4-battle-classic §1.3 (SetFleetOnContact {fleet, mode}, per fleet, Paul 2026-11-21).</summary>
    void SetRetreat(Fleet f, bool on)
    {
        _retreatPending[f.Id] = on;
        S.Track(S.Host.Battles.SubmitSetFleetOnContact(Me, f.Id, on ? OnContactMode.Retreat : OnContactMode.Fight), $"Change the battle behaviour of {f.Name}");
        S.Say(on ? $"Order sent: {f.Name} will pull back when an enemy arrives." : $"Order sent: {f.Name} will stay and fight when an enemy arrives.");
        AddLogItem(S.Messages[^1], false);
        RefreshTop();
        // the setting shows in the Fleet window's Orders tab; the panel redraws too, so nothing shows the old setting
        _panelDirty = true; _panelTimer = 0;
        MgRefreshSoon();
    }

    List<int> Ticked(Fleet f) => _splitSel.Where(f.Ships.Contains).OrderBy(x => x).ToList();

    void ScrapTicked(Fleet f)
    {
        var ids = Ticked(f);
        if (ids.Count == 0) { Toast("Tick at least one ship first.", MsgKind.Warn); return; }
        var objs = ids.Select(S.Host.Economy.Find).Where(o => o != null).ToList();
        AskScrap(objs, objs.Count == 1 ? $"the {objs[0].Class} #{objs[0].Id}" : $"{objs.Count} ships of {f.Name}", f.At);
    }

    void Split(Fleet f)
    {
        var ids = Ticked(f);
        if (ids.Count == 0) { Toast("Tick at least one ship first.", MsgKind.Warn); return; }
        S.TrackFleet(f.Id, Fm.SubmitSplitFleet(Me, f.Id, ids), $"Split {f.Name}");
        _splitSel.Clear();
        Sent($"split {Ui.Count(ids.Count, "ship", "ships")} off {f.Name}", f.At);
    }

    void Transfer(Fleet f, Fleet into)
    {
        var ids = Ticked(f);
        if (ids.Count == 0) { Toast("Tick at least one ship first.", MsgKind.Warn); return; }
        S.TrackFleet(f.Id, Fm.SubmitTransferShips(Me, ids, into.Id), $"Move ships from {f.Name} into {into.Name}");
        _splitSel.Clear();
        Sent($"move {Ui.Count(ids.Count, "ship", "ships")} from {f.Name} into {into.Name}", f.At);
    }

    void Merge(Fleet f, Fleet into)
    {
        S.TrackFleet(f.Id, Fm.SubmitMergeFleets(Me, f.Id, into.Id), $"Merge {f.Name} into {into.Name}");
        Sent($"merge {f.Name} into {into.Name}", f.At);
    }
}
