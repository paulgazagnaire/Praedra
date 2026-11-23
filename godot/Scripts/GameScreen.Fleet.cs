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
/// The side panel's page for a fleet (user stories C1, C2): where it is and where it goes, its orders with Cancel, a few numbers, Move and
/// Open fleet window. Every other fleet order lives in the fleet's own window (GameScreen.WinFleet.cs); the order helpers below are shared
/// with it. Only the player's own fleets can be selected.
/// </summary>
public partial class GameScreen
{
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

    /// <summary>
    /// The side panel's page for a fleet you selected: where it is, its orders with Cancel, a few numbers, and two buttons, Move and Open
    /// fleet window. Everything else (attack, merge, split, scrap, load, unload, its leaders) is in the fleet's own window, so there is one
    /// way to do each thing (2026-11-22, the fleet follow-ups 1, 2 and 8).
    /// </summary>
    void FleetPanel(Fleet f)
    {
        bool atSystem = f.At != null;
        string at = f.At;
        var tags = new List<(string, Color)> { ("YOUR FLEET", Ui.You) };
        if (!atSystem) tags.Add(("MOVING", Ui.Text2));
        else if (S.PlannedMoveOf(f.Id) != null) tags.Add(("LEAVING", Ui.Warn));
        if (S.HoldsHarvester(f)) tags.Add(("HARVESTER", Ui.Enemy));
        var here = atSystem ? S.Game.State.Fleets.Values.Where(x => x.Side == Me && x.At == at).OrderBy(x => x.Id).ToList() : new List<Fleet> { f };
        Title(S.NameOf(f), WhereIs(f), tags.ToArray());
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

        var eco = S.Host.Economy;
        int fighters = FightersAboard(f).Count + f.Squadrons.Count, regiments = S.Host.Troops.RegimentsAboard(f).Count;
        var hull = HullState(f);
        using (Card())
        {
            // the strength number is not shown bare (Paul 2026-11-22, F7 answer B: a number with no scale); it stays in this hover
            Stat("Ships", f.Ships.Count + (hull.Low > 0 ? $"  ({hull.Low} damaged)" : ""), hull.Critical > 0 ? Ui.Danger : hull.Low > 0 ? Ui.Warn : null,
                (HullWords(hull) is { Length: > 0 } hw ? hw.Trim() + " Damaged: under half its hull, the Ships tab's amber.\n" : "")
                + $"Strength {Fm.StrengthOf(f):0}: how hard the fleet hits, its damage rate against three Mandates with its damage counted (three Decrees score about 65).");
            // what it is made of, in one line (UI review 1, 2026-11-22: Strength alone doesn't say it); words, not stacked cards
            string makeup = string.Join(", ", f.Ships.Select(eco.Find).Where(o => o != null).GroupBy(o => o.Class)
                .OrderByDescending(g => g.Count()).ThenBy(g => g.Key, StringComparer.Ordinal).Select(g => $"{g.Count()} {g.Key}"));
            if (makeup != "") Line(makeup, Dim, Ui.Small);
            Stat("Fighters", RoomCount(fighters, Fm.FreeBerths(f) + FightersAboard(f).Count) ?? "none");
            Stat("Troops", RoomCount(regiments, Fm.FreeTroopSlots(f) + regiments) ?? "none");
        }

        var move = Act(_moveFleet == f.Id ? "Cancel move" : "Move...", () => { if (_moveFleet == f.Id) CancelMove(); else StartMove(f); },
            "Then click a destination system (or a row of a sector window). The route and arrival day show before anything is sent. A right-click on a system sends it there at once.", !atSystem, "Primary");
        move.Alignment = HorizontalAlignment.Center;
        if (!atSystem) Note("The fleet is travelling; give orders when it arrives.");
        var open = Act("Open fleet window", () => OpenMgmt(MgKind.Fleet, fleet: f.Id),
            "Its orders (attack, merge, split, scrap), its ships one by one, loading fighters and troops, its leaders, and whether it fights or pulls back. F opens it too.");
        open.Alignment = HorizontalAlignment.Center;
        open.SetMeta(UiTest.FullName, "Fleet orders");
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
        Sent($"{S.NameOf(f)} destroys {at}", at); // the core judges it when the clock moves; a refusal comes back in the messages with its reason
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
        S.TrackFleet(f.Id, S.Host.Troops.SubmitLoad(Me, f.Id, ids), $"Load troops onto {S.NameOf(f)}");
        Sent($"load {ids.Count} regiment(s) onto {S.NameOf(f)}", f.At);
    }

    void UnloadTroops(Fleet f, List<int> ids)
    {
        S.TrackFleet(f.Id, S.Host.Troops.SubmitUnload(Me, f.Id, ids), $"Unload troops from {S.NameOf(f)}");
        Sent($"unload {ids.Count} regiment(s) from {S.NameOf(f)}", f.At);
    }

    void LoadFighters(Fleet f, List<int> ids)
    {
        if (ids.Count == 0) return;
        S.TrackFleet(f.Id, Fm.SubmitLoadSquadrons(Me, f.Id, ids), $"Load fighters onto {S.NameOf(f)}");
        Sent($"load {ids.Count} fighter squadron(s) onto {S.NameOf(f)}", f.At);
    }

    void UnloadFighters(Fleet f, List<int> ids)
    {
        S.TrackFleet(f.Id, Fm.SubmitUnloadSquadrons(Me, f.Id, ids), $"Unload fighters from {S.NameOf(f)}");
        Sent($"unload {ids.Count} fighter squadron(s) from {S.NameOf(f)}", f.At);
    }

    void Bombard(Fleet f, BombardCategory cat)
    {
        S.TrackFleet(f.Id, S.Host.Bombardment.SubmitBombard(Me, f.Id, f.At!, cat), $"Bombard {f.At} with {S.NameOf(f)}");
        Sent($"{S.NameOf(f)} bombards {f.At} ({cat.ToString().ToLowerInvariant()})", f.At);
    }

    void Invade(Fleet f)
    {
        S.TrackFleet(f.Id, S.Host.Assault.SubmitAssault(Me, f.Id, f.At!), $"Invade {f.At} with {S.NameOf(f)}");
        Sent($"{S.NameOf(f)} invades {f.At}", f.At);
    }

    /// <summary>How a fleet's ships stand by the Ships tab's hull marks (amber under half its hull, red under a quarter): how many are
    /// under half, how many of those under a quarter, and how many have lighter damage (said in hovers only, so one fight doesn't turn
    /// every fleet amber; UI review 2, 2026-11-22).</summary>
    (int Low, int Critical, int Light) HullState(Fleet f)
    {
        int low = 0, critical = 0, light = 0;
        foreach (var o in f.Ships.Select(S.Host.Economy.Find))
        {
            if (o == null || Fm.ClassOf(o) is not { } sc || sc.Hull <= 0 || o.Hull >= sc.Hull) continue;
            float r = o.Hull / (float)sc.Hull;
            if (r < 0.5f) { low++; if (r < 0.25f) critical++; } else light++;
        }
        return (low, critical, light);
    }

    /// <summary>The hover words for <see cref="HullState"/>, or "" when every hull is whole.</summary>
    static string HullWords((int Low, int Critical, int Light) h) =>
        (h.Low > 0 ? $" {Ui.Count(h.Low, "ship", "ships")} under half hull" + (h.Critical > 0 ? $" ({h.Critical} under a quarter)" : "") + "." : "")
        + (h.Light > 0 ? $" {Ui.Count(h.Light, "ship", "ships")} lightly damaged." : "");

    /// <summary>Whether a fleet of yours is Elusive (pulls back when an enemy fleet arrives), counting a change ordered today, which the
    /// day applies first. rule: m4-battle-classic §1.3 (SetFleetOnContact).</summary>
    bool IsElusive(Fleet f) => _retreatPending.TryGetValue(f.Id, out var p) ? p : S.Host.Battles.OnContactOfFleet(f.Id) == OnContactMode.Retreat;

    /// <summary>One fleet's answer when an enemy fleet arrives: Elusive (Retreat) or Defensive (Fight). rule: m4-battle-classic §1.3 (SetFleetOnContact {fleet, mode}, per fleet, Paul 2026-11-21).</summary>
    void SetRetreat(Fleet f, bool on)
    {
        _retreatPending[f.Id] = on;
        _fleets.QueueRedraw();   // its marker turns into an arrowhead (or back) at once
        S.Track(S.Host.Battles.SubmitSetFleetOnContact(Me, f.Id, on ? OnContactMode.Retreat : OnContactMode.Fight), $"Change the battle behaviour of {S.NameOf(f)}");
        S.Say(on ? $"Order sent: {S.NameOf(f)} will pull back when an enemy arrives." : $"Order sent: {S.NameOf(f)} will stay and fight when an enemy arrives.");
        AddLogItem(S.Messages[^1], false);
        RefreshTop();
        // the setting shows in the Fleet window's Orders tab; the panel redraws too, so nothing shows the old setting
        _panelDirty = true; _panelTimer = 0;
        MgRefreshSoon();
    }

    void Merge(Fleet f, Fleet into)
    {
        S.TrackFleet(f.Id, Fm.SubmitMergeFleets(Me, f.Id, into.Id), $"Merge {S.NameOf(f)} into {S.NameOf(into)}");
        Sent($"merge {S.NameOf(f)} into {S.NameOf(into)}", f.At);
    }
}
