using System;
using System.Linq;
using Godot;

namespace PraedraViewer;

/// <summary>
/// Playtest of the map declutter (Paul's "All A", 2026-11-18): only your systems are named, from zoom 3.4, and the others while you point
/// at their sector or a dot of it, or select them; no words on fleets and movers at the opening zoom, short names from 3.4, "Fleet 1 · day
/// 6" on the selected fleet or the glyph under the mouse; Alt names everything; parked fleets sit above the rings; the hover card is one
/// line and waits for the mouse to rest; H, Shift+H and the menu go to the HQs. A fresh Hand game, as Paul plays it.
/// </summary>
public partial class Main
{
    void PlaytestDeclutter(GameScreen gs, string home)
    {
        var s = gs.S; var me = s.Player; var g = gs.G;
        var view = gs.View;
        view.Select(-1); view.SelectedFleet = -1;
        gs.ShotSectorHover("-");   // the mouse on no sector and no dot
        if (!g.TryFindSystem(home, out int homeIdx)) { Expect(false, $"{home} is on the map"); return; }

        // ---- names (pick 1)
        // a nobody's world we have explored, in a sector of more than one system
        string neutral = g.Systems.Where(x => !x.IsOutsideSectors && s.Explored(x.Name) && s.OwnerOf(x.Name) == null
                && g.Systems.Count(y => !y.IsOutsideSectors && y.Sector == x.Sector) > 1)
            .Select(x => x.Name).OrderBy(n => n, StringComparer.Ordinal).FirstOrDefault();
        string unexplored = g.Systems.Select(x => x.Name).Where(n => !s.Explored(n) && s.OwnerOf(n) != me).OrderBy(n => n, StringComparer.Ordinal).FirstOrDefault();
        Expect(neutral != null, "some explored system belongs to nobody");
        if (neutral == null) return;
        var nSys = g.Systems.First(x => x.Name == neutral);
        string sibling = g.Systems.Where(x => !x.IsOutsideSectors && x.Sector == nSys.Sector && x.Name != neutral).Select(x => x.Name).OrderBy(n => n, StringComparer.Ordinal).First();

        view.FocusOn(homeIdx, 2.4f);
        Expect(!gs.NameWanted(home), "at the opening zoom (2.4) no system is named, not even yours");
        view.SetZoom(3.6f);
        Expect(gs.NameWanted(home), "from zoom 3.4 your systems are named");
        Expect(!gs.NameWanted(neutral) && (unexplored == null || !gs.NameWanted(unexplored)), $"but nobody else's ({neutral}, {unexplored})");
        gs.ShotSectorHover(nSys.Sector.Replace(' ', '_'));
        Expect(gs.NameWanted(neutral), $"pointing at {nSys.Sector} names its systems ({neutral})");
        gs.HoverTextFor(sibling);
        Expect(gs.NameWanted(neutral), $"pointing at one of its dots ({sibling}) keeps them named: they used to blink off");
        gs.ShotSectorHover("-");
        Expect(!gs.NameWanted(neutral), "the mouse gone, the name goes");
        if (g.TryFindSystem(neutral, out int nIdx)) view.Select(nIdx);
        Expect(gs.NameWanted(neutral), "a selected system is named");
        view.Select(-1);

        // ---- hold Alt (pick 9)
        view.SetZoom(2.4f);
        gs.HoldAlt(true);
        Expect(gs.AllNamesShown && gs.NameWanted(neutral) && gs.NameWanted(home) && (unexplored == null || gs.NameWanted(unexplored)), "holding Alt names every system");
        gs.HoldAlt(false);
        Expect(!gs.NameWanted(neutral) && !gs.NameWanted(home), "letting go of Alt hides them again");
        gs.HoldAlt(true); gs.LoseFocus();
        Expect(!gs.AllNamesShown, "switching away from the game while holding Alt lets go of it");

        // ---- words on fleets and movers (pick 2), parked fleets above the rings
        var parked = s.Game.State.Fleets.Values.Where(f => f.Side == me && f.At == home).OrderBy(f => f.Id).FirstOrDefault();
        view.FocusOn(homeIdx, 2.4f);
        gs.PlayRefresh();
        Expect(gs.MapWords.Count == 0, "at the opening zoom fleets and movers carry no words: " + string.Join(" | ", gs.MapWords));
        if (parked != null)
        {
            gs.SelectFleet(parked.Id, false);
            Expect(gs.MapWords.Contains(parked.Name), $"the selected fleet is named ({parked.Name}): " + string.Join(" | ", gs.MapWords));
            view.SelectedFleet = -1;
            view.SetZoom(3.6f);
            var words = gs.MapWords;
            Expect(words.Contains(parked.Name), $"from zoom 3.4 each fleet has its short name ({parked.Name}): " + string.Join(" | ", words));
            Expect(words.All(w => !w.Contains(" to ") && !w.Contains("arrives") && !w.Contains("ship(s)")), "short names, no sentences: " + string.Join(" | ", words));
            string overlap = gs.MapWordsProblem();
            Expect(overlap == null, "no word on the map prints over another or past the screen's edge" + (overlap != null ? ": " + overlap : ""));
            if (gs.FleetMarker(parked.Id) is { } p)
            {
                float ringTop = gs.SystemOnScreen(home).Y - gs.RingTop(home);
                Expect(p.Y + 7f <= ringTop, $"{parked.Name} parked at {home} sits above its HQ rings (triangle bottom {p.Y + 7f:0}, rings from {ringTop:0})");
            }
            else Expect(false, $"{parked.Name} has a marker on the map");
        }
        else Expect(false, $"a fleet of yours is parked at {home} at the start");

        // ---- the hover card (pick 8)
        view.SetZoom(2.4f);
        string line = gs.HoverTextFor(home);
        Expect(line.StartsWith(home, StringComparison.Ordinal) && !line.Contains('\n') && line.Contains("yours") && line.Contains("troops"),
            "the hover card is one line: " + line);
        Expect(!gs.HoverCardShown, "it waits for the mouse to rest on the dot");
        gs.RestHover(0.5);
        Expect(gs.HoverCardShown, "and shows after a short rest");
        gs.ShotSectorHover("-");
        Expect(!gs.HoverCardShown, "it goes when the mouse leaves the dot");

        // ---- the HQ buttons are gone (pick 10): H and the menu
        Expect(!gs.ButtonTexts().Any(b => b.Contains("HQ")), "no HQ button on the screen: " + string.Join(", ", gs.ButtonTexts().Where(b => b.Contains("HQ"))));
        view.PanPixels(new Vector2(4000, 0)); view.Select(-1);
        gs.PressKey(Key.H);
        Expect(view.Selected == homeIdx, $"H centres the map on your HQ and selects it ({home})");
        gs.PressKey(Key.Escape); view.Select(-1);
        Expect(gs.ClickViaMenu("Go to your HQ") && view.Selected == homeIdx, "the menu's Go to your HQ does the same");
        gs.PressKey(Key.Escape); view.Select(-1);
        view.FocusOn(homeIdx, 2.4f);
        gs.PlayRefresh();
    }
}
