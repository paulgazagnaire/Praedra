using System;
using System.Linq;
using Godot;
using Praedra.Core.Construction;
using Praedra.Core.Economy;

namespace PraedraViewer;

/// <summary>
/// Playtest of your planet's Infrastructure window, built from Paul's Paper design (2026-11-18) and his answers (2026-11-19): two columns
/// wide; the yard cells are the Build tabs (ships and fighters under the shipyards); the arrows show the mines, refineries and defences;
/// a card orders, its yard says so, and the yard's right-click menu cancels; "Building:" with the name on hover; the links to the Ground
/// and Fleet windows; folding. Every click is a real button's Pressed signal; menus run the line a right-click would show.
/// </summary>
public partial class Main
{
    void PlaytestInfra(GameScreen gs, string near)
    {
        var s = gs.S;
        var eco = s.Host.Economy;
        bool HasAt(string at, YardFamily f) => eco.Objects.Any(o => o.Category == ObjectCategory.Facility && o.Completed && o.System == at
            && FacilityKinds.TryParse(o.Class, out var k) && FacilityKinds.IsYard(k) && Yards.FamilyOf(k) == f);
        // the system you hold with a construction yard, a yard at work and the most kinds of yard, so that the cards, the orders
        // and "Building:" get checked; the windows test's own system wins a tie
        var yardFamilies = new[] { YardFamily.Construction, YardFamily.Shipyard, YardFamily.Training };
        bool AtWork(string at) => yardFamilies.Any(f => s.Host.Construction.ProjectAt(at, f) is { Units.Count: > 0 });
        string sys = s.Data.Galaxy.Systems.Select(x => x.Name).Where(n => s.OwnerOf(n) == s.Player && !s.IsDestroyed(n))
            .OrderByDescending(n => (HasAt(n, YardFamily.Construction) ? 10 : 0) + (AtWork(n) ? 5 : 0) + yardFamilies.Count(f => HasAt(n, f)))
            .ThenBy(n => n == near ? 0 : 1).ThenBy(n => n, StringComparer.Ordinal).FirstOrDefault();
        Expect(sys != null, "you hold a system");
        if (sys == null) return;
        string key = $"Infra {sys}";
        gs.OpenMgmt(GameScreen.MgKind.Infra, sys); gs.PlayRefresh();
        ObjectCategory? Cat(string cls) => eco.Costs.TryGet(cls, out var r) ? r.Category : null;
        string Cards() => gs.InfraState(sys) is { } st ? string.Join(", ", st.Cards.Select(c => c.Name)) : "(no window)";
        bool Has(YardFamily f) => HasAt(sys, f);
        string CellOf(YardFamily f) => f == YardFamily.Construction ? "Construction" : f == YardFamily.Shipyard ? "Shipyard" : "Training";
        // with no yard of the kind, no card and a line that says so; with one, cards of the right kind only
        void Tab(string cell, YardFamily fam, string words, Func<ObjectCategory?, bool> right, string none)
        {
            bool clicked = gs.Click($"Yard {sys} {cell}");
            var st = gs.InfraState(sys);
            if (Has(fam)) Expect(clicked && st?.Cell == cell && st.Value.Cards.Count > 0 && st.Value.Cards.All(c => right(Cat(c.Name))), $"{words}: " + Cards());
            else Expect(clicked && st?.Cell == cell && st.Value.Cards.Count == 0 && gs.MgText(key).Contains(none), $"{words}: none, and the window says why ({sys} has no such yard)");
        }

        var st0 = gs.InfraState(sys);
        Expect(st0 is { Width: 698 } && st0.Value.Want <= st0.Value.Width + 0.5f,
            $"your planet's Infrastructure window ({sys}) is two columns wide (698) and nothing in it asks for more ({st0?.Want:0} wanted)");
        string text = gs.MgText(key);
        Expect(text.Contains($"{sys.ToUpperInvariant()}: INFRASTRUCTURE") && new[] { "System energy", "Raw material", "Construction yards", "Orbital shipyards", "Training facilities", "Buildings", "Build", "How many" }.All(text.Contains),
            "it has the design's parts: " + text.Replace("\n", " / "));

        // the yard cells are the Build tabs
        Tab("Construction", YardFamily.Construction, "Construction yards: the facilities to build", c => c == ObjectCategory.Facility, "This planet has no construction yard");
        Tab("Shipyard", YardFamily.Shipyard, "Orbital shipyards: ships and fighters", c => c is ObjectCategory.CapitalShip or ObjectCategory.Fighter, "This planet has no shipyard");
        if (Has(YardFamily.Shipyard)) Expect(gs.InfraState(sys)?.Cards.Any(c => Cat(c.Name) == ObjectCategory.Fighter) == true, "the shipyards' cards include the fighters: " + Cards());
        Tab("Training", YardFamily.Training, "Training facilities: troops", c => c is ObjectCategory.Regiment or ObjectCategory.SpecialForce, "This planet has no training facility");

        // the arrows (Paul 2026-11-19, 4B): the mines, refineries and defences, then back to the yard last opened
        Expect(gs.Click($"Strip next {sys}"), "› shows the other cells");
        text = gs.MgText(key);
        Expect(gs.InfraState(sys) is { Page: 1, Cell: "Mines" } m && (Has(YardFamily.Construction) ? m.Cards.Count > 0 : m.Cards.Count == 0) && m.Cards.All(c => c.Name == "Mine")
            && text.Contains("Refineries") && text.Contains("Defences") && !text.Contains("Orbital shipyards"),
            $"the mines, refineries and defences, Mines open, with only the Mine to build{(Has(YardFamily.Construction) ? "" : " (none: no construction yard)")}: " + Cards());
        Expect(!gs.Enabled($"Strip next {sys}") && gs.Click($"Strip back {sys}") && gs.InfraState(sys) is { Page: 0, Cell: "Training" }, "‹ goes back to the yards, on the one last opened");

        // a card orders "How many"; the yard says so; its right-click menu takes the order back (the first yard with a card you can order)
        string usable = null, yardCell = null;
        YardFamily usableFam = YardFamily.Construction;
        foreach (var fam in yardFamilies.Where(Has))
        {
            gs.Click($"Yard {sys} {CellOf(fam)}");
            usable = gs.InfraState(sys)?.Cards.FirstOrDefault(c => c.Usable).Name;
            if (usable != null) { usableFam = fam; yardCell = $"Yard {sys} {CellOf(fam)}"; break; }
        }
        if (usable != null)
        {
            int q = s.QueuedOrders;
            Expect(gs.Click($"Build {usable}") && s.QueuedOrders == q + 1, $"a click on {usable}'s card orders it");
            gs.MgText(key);
            bool busy = s.Host.Construction.ProjectAt(sys, usableFam) is { Units.Count: > 0 };
            var words = gs.CellWords(yardCell);
            Expect(busy ? words.Normal == "Building:" && words.Hover != null : words.Normal == "Ordered:" && words.Hover == usable, $"the {CellOf(usableFam)} cell says so: {words}");
            var menu = gs.MenuOf(yardCell);
            Expect(menu.Any(x => x.StartsWith("Cancel: ", StringComparison.Ordinal)), "its right-click menu can cancel the order: " + string.Join(" | ", menu));
            Expect(gs.RunMenu(yardCell, "Cancel: ") && s.QueuedOrders == q, "and Cancel takes it back");
        }
        else GD.Print($"  (no card can be ordered at {sys} now: the order from a card is not checked)");

        // a yard at work says "Building:" and, on hover, what (Paul 2026-11-19); right-click offers to stop it
        gs.MgText(key);
        if (!AtWork(sys)) GD.Print($"  (no yard at work at {sys} now: \"Building:\" is not checked)");
        foreach (var fam in yardFamilies)
        {
            if (s.Host.Construction.ProjectAt(sys, fam) is not { Units.Count: > 0 } p) continue;
            string cell = $"Yard {sys} {CellOf(fam)}";
            var words = gs.CellWords(cell);
            Expect(words.Normal == "Building:" && words.Hover == eco.Find(p.Units[0])?.Class, $"the {fam} cell says \"Building:\", and {eco.Find(p.Units[0])?.Class} on hover: {words}");
            Expect(gs.MenuOf(cell).Contains("Stop building..."), $"the {fam} cell's right-click menu can stop its work");
        }

        // a building: right-click to scrap
        var b = eco.Objects.Where(o => o.Category == ObjectCategory.Facility && o.System == sys && o.Completed).OrderBy(o => o.Id).FirstOrDefault();
        if (b != null)
        {
            var tab = FacilityKinds.TryParse(b.Class, out var k) ? k switch
            {
                FacilityKind.Mine => "Mines", FacilityKind.Refinery => "Refineries",
                _ when FacilityKinds.IsYard(k) => Yards.FamilyOf(k) switch { YardFamily.Shipyard => "Shipyard", YardFamily.Training => "Training", _ => "Construction" },
                _ => "Defences",
            } : "Defences";
            gs.ShotWindow("infra", $"{sys}:{tab}"); gs.MgText(key);
            Expect(gs.MenuOf($"Building {b.Class} {b.Id}").Contains("Scrap this one..."), $"under {tab}, the {b.Class} tile offers to scrap it");
            gs.Click($"Yard {sys} Construction");
            if (gs.InfraState(sys) is { Page: 1 }) gs.Click($"Strip back {sys}");
        }

        // the way to the Ground window and the fleet
        Expect(gs.Click($"Ground and military at {sys}") && gs.MgmtKeys.Contains($"Ground {sys}"), "Ground and military opens the Ground window");
        var fleets = s.Game.State.Fleets.Values.Where(f => f.Side == s.Player && f.At == sys && f.Transit == null).OrderBy(f => f.Id).ToList();
        if (fleets.Count == 0) Expect(!gs.Enabled($"Fleet at {sys}"), "Fleet is greyed out with no fleet of yours in orbit");
        else Expect(gs.Click($"Fleet at {sys}") && (fleets.Count == 1 ? gs.MgmtKeys.Contains($"Fleet {fleets[0].Id}") : gs.MgmtKeys.Contains($"Ground {sys}")),
            $"Fleet opens {(fleets.Count == 1 ? fleets[0].Name + "'s window" : "the list of the " + fleets.Count + " fleets")}");

        // – folds it to its name
        Expect(gs.Click($"Fold {key}") && !gs.MgText(key).Contains("System energy") && gs.MgText(key).Contains("INFRASTRUCTURE"), "– folds it to its name");
        Expect(gs.Click($"Fold {key}") && gs.MgText(key).Contains("System energy"), "+ unfolds it");
        while (gs.MgmtCount > 0) gs.PressKey(Key.Escape);
    }
}
