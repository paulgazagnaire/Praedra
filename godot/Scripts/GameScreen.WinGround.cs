using System;
using System.Collections.Generic;
using System.Linq;
using Godot;
using Praedra.Core.Characters;
using Praedra.Core.Economy;
using Praedra.Core.Fleets;
using Praedra.Core.Fog;

using Side = Praedra.Core.Clock.Side;

namespace PraedraViewer;

/// <summary>
/// A planet's Ground window, from Paul's "new colors" frames (2026-11-24: ground-new-frame-1-new-colors and -2; export
/// praedra/specs/ui/paper/new-colors-2026-11-24/), in the Fleet window's look: the head on the darker surface (the name, the window
/// buttons, the planet's loyalty with its 2 px bar), a strip of tabs (Command, Troops, Fighters, Fleets; Paul removed the Lore tab), then the
/// open tab on the lighter page, everything as cards three a row.
/// A planet you don't hold gets the same window (Paul 2026-11-24: "Make it the same window as the owned system. Display the last know info
/// on the system, if 4 enemy regiments invaded, display them"): what your side last saw there, read only, each part with the day it was
/// seen; your own leaders can still land and leave there (rule: m5-characters §Posts, DetachCharacter anywhere a fleet stands still).
/// Screens only: every drop is an order the core judges when the clock moves.
/// </summary>
public partial class GameScreen
{
    void GroundWin(MgWin w, string n)
    {
        bool mine = S.OwnerOf(n) == Me;
        var pic = mine ? null : S.Picture(n);
        GroundHead(w, n, mine, pic);
        if (w.Min) return;

        var own = S.Game.State.Fleets.Values.Where(f => f.Side == Me && f.At == n).OrderBy(f => f.Id).ToList();
        var (foes, foesDay) = EnemyFleetsAt(n, pic);
        // the system's General: one post, only on a planet you hold (rule: m5-characters §Posts, AssignSystemGeneral needs the holder;
        // Paul 2026-11-24 kept A: no Commander post on a planet, the rules have none)
        var gen = mine ? Cm.SystemGeneralOf(n) : null;
        if (gen != null && gen.Side != Me) gen = null;
        var here = S.MyCharacters.Where(c => c.Status != CharacterStatus.Dead && c.AttachedFleet == 0 && c.Transit == null && Cm.LocationOf(c) == n && c.Id != gen?.Id)
            .OrderBy(c => S.CharName(c.Id), StringComparer.Ordinal).ToList();
        string fleetsCount = own.Count + foes.Count == 0 ? null : (own.Count + foes.Count).ToString();
        if (mine)
        {
            var regs = S.Host.Troops.RegimentsOnSystem(n).Where(o => o.Side == Me).OrderBy(o => o.Id).ToList();
            var sf = MySpecialForcesAt(n);
            var based = FightersBasedAt(n);
            // the Troops count (Paul 2026-11-24, A): the regiments here out of those needed to keep the planet calm. rule: m2-support (garrison)
            int have = S.Host.Support.RegimentCount(n), need = S.Host.Support.RequiredGarrison(n);
            FleetStrip(w,
                ("Command", null, null, () => GroundCommand(n, gen, here, null)),
                // a planet that needs no garrison shows the regiments alone, "14" not "14/0" (Paul 2026-11-25, "B"); none and none: no count
                ("Troops", need > 0 ? $"{have}/{need}" : have > 0 ? have.ToString() : null, have < need ? Ui.Warn : null, () => GroundTroops(n, regs, sf)),
                ("Fighters", based.Count == 0 ? null : based.Count.ToString(), null, () => GroundFighters(n, based)),
                ("Fleets", fleetsCount, null, () => GroundFleets(n, own, foes, foesDay)));
            if (w.Pages.TryGetValue("Troops", out var tt))
                tt.Tab.TooltipText = Ui.Wrap($"{have} {Plural(have, "regiment", "regiments")} here; {(need == 0 ? "none needed" : $"{need} needed")} to keep {n} calm."
                    + " Below 60% loyalty a planet needs one regiment for every 10 points under 60. With fewer, it can rise up and its mines and yards stop."
                    + (have < need ? " Amber: too few." : ""));
        }
        else
        {
            var seen = EnemyGround(pic);
            var sf = MySpecialForcesAt(n);
            int units = seen.Units.Count;
            FleetStrip(w,
                ("Command", null, null, () => GroundCommand(n, null, here, pic)),
                ("Troops", units == 0 ? null : units.ToString(), null, () => GroundTroopsSeen(n, seen, sf, pic)),
                ("Fighters", null, null, GroundFightersSeen),
                ("Fleets", fleetsCount, null, () => GroundFleets(n, own, foes, foesDay)));
        }
    }

    // ================================================================== the head

    /// <summary>
    /// The head, as the Fleet window's: the name in BitPotion 48 px with ⋮⋮ – ×, then "Loyalty" and its share with a 2 px bar. Under it, one
    /// line only when it applies: an uprising, too few regiments, a blockade; on a planet you don't hold, who holds it and when you last saw it.
    /// </summary>
    void GroundHead(MgWin w, string n, bool mine, SystemPicture pic)
    {
        var head = new PanelContainer { MouseFilter = MouseFilterEnum.Pass };   // a left drag still moves the window (the head under it)
        head.AddThemeStyleboxOverride("panel", Look.Box(Look.Bg));
        head.SetMeta(UiTest.FullName, $"Ground head {n}");
        var pad = Pad(FleetHeadPad, FleetHeadPad, FleetHeadPad, FleetHeadPad);
        head.AddChild(pad);
        var parts = Boxed(new VBoxContainer(), 24);
        pad.AddChild(parts);
        parts.AddChild(WinNameRow(w, n.ToUpperInvariant(), !mine && !Scouted(n) ? Ui.Dimmed : Look.Text));
        if (!w.Min)
        {
            float lineW = WidthOf(w) - 2 * EdgeW - 2 * FleetHeadPad;
            parts.AddChild(GroundStatus(n, mine, pic, lineW));
        }
        w.Head.AddChild(head);
    }

    /// <summary>A window's name and its buttons (the Fleet window's head row): BitPotion 48 px, 32 px when it does not fit beside them.</summary>
    HBoxContainer WinNameRow(MgWin w, string upper, Color ink)
    {
        var grip = MoveGrip();
        grip.AddThemeColorOverride("font_color", Look.Text);
        var row = Boxed(new HBoxContainer(), NameGap);
        float room = WidthOf(w) - 2 * EdgeW - 2 * FleetHeadPad - NameGap - 3 * WinButton - 2 * 4;
        int size = Ui.NameSize(upper, room);
        var name = Look.Words(upper, size, ink);
        name.AddThemeFontOverride("font", Ui.NameFont(size, upper));
        name.SizeFlagsHorizontal = SizeFlags.ExpandFill;
        name.SizeFlagsVertical = SizeFlags.ShrinkCenter;
        name.ClipText = true;
        name.TextOverrunBehavior = TextServer.OverrunBehavior.TrimEllipsis;
        name.SetMeta(UiTest.FullName, $"Name {w.Key}");
        row.AddChild(name);
        row.AddChild(WinButtons(w, grip, grey: true));
        return row;
    }

    /// <summary>"Loyalty" and the share of the people for you, with its bar; then the one line that matters most today.</summary>
    Control GroundStatus(string n, bool mine, SystemPicture pic, float lineW)
    {
        var v = Boxed(new VBoxContainer(), 4);
        v.MouseFilter = MouseFilterEnum.Pass;
        v.SetMeta(UiTest.FullName, $"Ground status {n}");
        int? loyal = mine ? S.Host.Economy.SupportOf(n, Me)
            : pic?.Owner is { } po ? (Me == Side.Collective ? po.Value.CollectiveSupport : 100 - po.Value.CollectiveSupport) : null;
        var line = Boxed(new HBoxContainer(), StatusGap);
        // on a planet you don't hold the share is yours, not its holder's: "Loyalty 0%" under "Held by the Collective" read as a revolt (review 23)
        line.AddChild(Look.Words(mine ? "Loyalty" : "Loyalty to you", 12, Look.Text, true));
        var right = Look.Words(loyal is int l ? $"{l}%" : "unknown", 12, Look.Quiet);
        right.SizeFlagsHorizontal = SizeFlags.ExpandFill;
        right.HorizontalAlignment = HorizontalAlignment.Right;
        line.AddChild(right);
        v.AddChild(line);
        v.AddChild(new ThinBar { Share = (loyal ?? 0) / 100f, Fill = Ui.You, Track = loyal == null ? Colors.Transparent : Look.Divider, CustomMinimumSize = new Vector2(0, 2) });
        string tip = mine
            ? $"How much the people of {n} back you ({loyal}%). Below 60% they need regiments to stay calm. {DiplomacyWord} missions raise it."
            : loyal is int y ? $"What the people of {n} thought of you when you last looked ({y}% for you, {100 - y}% for the {S.NameOf(Me == Side.Hand ? Side.Collective : Side.Hand)}). A {DiplomacyWord} mission raises it."
            : $"You have never looked at {n}: send a fleet or a spy.";

        // the one line under the bar: the worst thing true today
        string words = null;
        Color ink = Look.Soft;
        if (mine)
        {
            int have = S.Host.Support.RegimentCount(n), need = S.Host.Support.RequiredGarrison(n);
            if (S.Host.Support.IsUprising(n)) (words, ink) = ("Uprising: its mines and yards stop until it ends.", Ui.Danger);
            else if (S.Game.State.Blockades.ContainsKey(n)) (words, ink) = ("Blockaded: its mines and yards stop until the enemy fleet is driven off.", Ui.Danger);
            else if (have < need) (words, ink) = ($"Too few regiments: {have} of the {need} needed to keep it calm.", Ui.Warn);
        }
        else
        {
            var (known, owner) = S.KnownOwnerOf(n);
            // a fleet (or an agent) here sees who holds it, its support and the fleets in orbit, never the ground: Paul kept the rule
            // (2026-11-27, QA playthrough item 5: "You see it now" over a blind Troops tab read as a bug). rule: m5-fog-and-hq §F3. One fact
            // here; the Troops tab says how to see more (review 33). Seen live, a neutral world has no defenders (m3-assault §0).
            bool live = S.Sees(n), orbit = S.Game.State.Fleets.Values.Any(f => f.Side == Me && f.At == n);
            string who = !known ? "Unexplored: you don't know who holds it." : owner == null ? (live ? "Neutral: no troops defend it." : "Neutral: neither side holds it.")
                : $"Held by the {S.NameOf(owner.Value)}.";
            int? last = S.LastSeenDay(n);
            string when = live ? (owner == null && known ? "" : orbit ? " Your fleet sees its orbit, not its ground." : " You see it now, but not its ground.") : last is int d ? (d >= S.Game.Day ? $" Seen today (day {d})." : $" Seen day {d} ({Ui.DaysAgo(S.Game.Day - d)}): it may have changed.") : "";
            words = who + when;
            ink = known && owner != null ? Ui.Enemy : Look.Soft;
        }
        if (words != null)
        {
            var l2 = Look.Words(words, 12, ink);
            l2.AutowrapMode = TextServer.AutowrapMode.WordSmart;
            l2.CustomMinimumSize = new Vector2(lineW, 0);
            l2.SetMeta(UiTest.FullName, $"Ground note {n}");
            v.AddChild(Ui.Space(4));
            v.AddChild(l2);
        }
        v.TooltipText = Ui.Wrap(tip);
        return v;
    }

    // ================================================================== Command: the General, the leaders here, missions

    /// <summary>
    /// The frame's Command tab: Ground command (the General's post, on a planet you hold), then the characters standing here as cards; on a
    /// planet you don't hold, the enemy leaders last seen there. Then who is on the way, and the buttons: a mission from here, the
    /// Infrastructure window.
    /// </summary>
    void GroundCommand(string n, CharacterRecord gen, List<CharacterRecord> here, SystemPicture pic)
    {
        bool mine = pic == null && S.OwnerOf(n) == Me;
        if (mine)
        {
            var cmd = FleetSection("Ground command", 16);
            var col = Boxed(new VBoxContainer(), 8);
            col.CustomMinimumSize = new Vector2(CardW, 0);
            col.SizeFlagsHorizontal = SizeFlags.ShrinkBegin;
            var cap = Look.Words("General", 12, Look.Text, true);
            cap.MouseFilter = MouseFilterEnum.Pass;
            cap.TooltipText = "The system's General adds their leadership to the defenders when the planet is invaded.";
            col.AddChild(cap);
            var gz = DropZone($"Ground {n} General", "make General", d => RefuseSystemGeneral(d, n), d => DropSystemGeneral(d, n));
            if (gen != null) gz.Flow.AddChild(LeaderCard(gen));
            else gz.Flow.AddChild(EmptyCard(DropLeader));
            gz.Box.TooltipText = Ui.Wrap(gen == null ? $"Drag a leader standing at {n} here to make them its General. One from another system flies here first."
                : $"Drag {S.CharName(gen.Id)} off the post to release them.");
            col.AddChild(gz.Box);
            cmd.AddChild(col);
            if (gen == null) WhoCanBeGeneral(cmd, n, here);
        }

        var chars = FleetSection("Leaders", 16, null, mine ? "Your leaders standing on the planet, not on a fleet. Drag one onto a fleet in orbit, a post, or another system."
            : $"You don't hold {n}, but your leaders can stand on it: drag a leader off a fleet of yours here to land them, or one standing here onto a fleet of yours in orbit.");
        var lz = DropZone($"Ground {n} Leaders", "land", d => RefuseToGround(d, n, 'L'), d => DropToGround(d, n));
        CardFlow(lz.Flow);
        foreach (var c in here) lz.Flow.AddChild(LeaderCard(c));
        // on a planet you don't hold, leaders land only off a fleet of yours in orbit: with none, say so rather than invite a refused drop
        bool landable = mine || S.Game.State.Fleets.Values.Any(f => f.Side == Me && f.At == n);
        if (here.Count == 0) lz.Flow.AddChild(EmptyCard(landable ? DropLeader : "Bring a fleet here to land leaders"));
        lz.Box.TooltipText = Ui.Wrap(mine ? "Drag a leader here to land them on the planet." : "Drag a leader off a fleet of yours here to land them.");
        chars.AddChild(lz.Box);

        // the enemy leaders your side last saw here (group D: rule m5-fog-and-hq §F1), read only
        if (pic?.Characters is { } seen && seen.Value.Where(c => c.Side != Me).OrderBy(c => c.Id).ToList() is { Count: > 0 } foes)
        {
            var sec = FleetSection("Enemy leaders", 16, foes.Count.ToString());
            sec.AddChild(SeenLine(seen.Stamp));
            var flow = CardFlow(new HFlowContainer { MouseFilter = MouseFilterEnum.Ignore });
            foreach (var c in foes)
            {
                string name = S.CharName(c.Id);
                string sub = c.Status == CharacterStatus.Free ? null : c.Status.ToString().ToLowerInvariant();
                var card = FleetCard(LeaderPic(c.Id, Ui.Enemy), GameSession.CardName(name), sub);
                card.TooltipText = Ui.Wrap($"{name}, a leader of the {S.NameOf(c.Side)}: seen here on day {seen.Stamp}. They may have left since.");
                card.SetMeta(UiTest.FullName, "Enemy leader " + name);
                flow.AddChild(card);
            }
            sec.AddChild(flow);
        }

        // your leaders flying here to stand on the planet (rule: m5-characters §Moving a character alone)
        var coming = S.MyCharacters.Where(c => c.Transit is { } t && t.Path.Count > 0 && t.Path[^1] == n && c.JoinFleet == 0).OrderBy(c => c.Id).ToList();
        if (coming.Count > 0)
        {
            var way = FleetSection("On the way", 16, coming.Count.ToString(), $"Sent here from other systems. They take their place when they reach {n}.");
            var flow = CardFlow(new HFlowContainer { MouseFilter = MouseFilterEnum.Ignore });
            foreach (var c in coming)
                flow.AddChild(FleetCard(LeaderPic(c.Id, Ui.You), GameSession.CardName(S.CharName(c.Id)),
                    Arrives(c.Transit, c.JoinPost != CharacterPost.None ? $"to be {GameSession.PostWords(c.JoinPost)}" : null), ghost: true));
            way.AddChild(flow);
        }

        var bar = ButtonRow();
        if (CanSendFrom(n))
            bar.AddChild(FleetAction("Send a mission...", $"Mission from {n}", () => { CloseSectorView(); OpenPlanner(n); },
                "Pick a team standing here (leaders and special forces), the kind of mission and the target."));
        bar.AddChild(InfraButton(n));
        Add(bar);
    }

    /// <summary>The post a leader would leave to take this one: "General of X", or "Admiral of Fleet 2" (review 32 follow-up); null when free.</summary>
    string PostNow(CharacterRecord c) =>
        S.SystemPostOf(c) is string gs ? $"General of {gs}"
        : c.AttachedFleet != 0 && c.Post != CharacterPost.None && S.Game.State.Fleets.TryGetValue(c.AttachedFleet, out var f) ? $"{GameSession.PostWords(c.Post)} of {S.NameOf(f)}"
        : null;

    /// <summary>Whether the list of who can be a General is unfolded (one switch for every planet, kept while the game runs).</summary>
    bool _generalsOpen;

    /// <summary>
    /// Under an empty General post: which of your leaders can take it, free ones first and nearest first, each a button that opens the
    /// window where they stand so they can be dragged here (leaders move by drag only, Paul 2026-11-22). The post used to say who can't
    /// only during a drag, and never who can (QA playthrough 2026-11-27, item 11). Most posts are empty, so the list folds into one line
    /// that keeps the Leaders section, the drag source, near the top (review 32).
    /// </summary>
    void WhoCanBeGeneral(VBoxContainer cmd, string n, List<CharacterRecord> here)
    {
        const int Shown = 3;
        var able = S.MyCharacters.Where(c => Cm.ClassOf(c).CanGeneral && S.SystemPostOf(c) != n && CanTravel(c, n) == null)
            .Select(c => (c, days: TravelDays(c, n))).OrderBy(x => PostNow(x.c) != null).ThenBy(x => x.days).ThenBy(x => S.CharName(x.c.Id), StringComparer.Ordinal).ToList();
        if (able.Count == 0)
        {
            var none = Look.Words("None of your leaders free to travel can be a General.", 12, Look.Muted);
            none.AutowrapMode = TextServer.AutowrapMode.WordSmart;
            none.SetMeta(UiTest.FullName, $"Ground {n} who can be General");
            cmd.AddChild(none);
            return;
        }
        string why = here.Count > 0 && !here.Any(c => Cm.ClassOf(c).CanGeneral) ? "No leader here can be a General. " : "";
        var words = Look.Words($"Who can be a General ({able.Count}) {(_generalsOpen ? "▾" : "▸")}", 12, Look.Muted);
        var fold = new Button { Flat = true, FocusMode = FocusModeEnum.None, MouseDefaultCursorShape = CursorShape.PointingHand,
            SizeFlagsHorizontal = SizeFlags.ShrinkBegin,
            TooltipText = Ui.Wrap(why + (_generalsOpen ? "Fold the list." : $"Show the nearest {Math.Min(Shown, able.Count)}, each a way to the window where they stand.")) };
        foreach (var state in new[] { "normal", "hover", "pressed", "hover_pressed", "focus" }) fold.AddThemeStyleboxOverride(state, new StyleBoxEmpty());
        Fill(fold, words, 0, 2);
        fold.MouseEntered += () => words.AddThemeColorOverride("font_color", Look.Text);
        fold.MouseExited += () => words.AddThemeColorOverride("font_color", Look.Muted);
        fold.Pressed += () => { _generalsOpen = !_generalsOpen; MgRefreshSoon(); };
        fold.SetMeta(UiTest.FullName, $"Ground {n} who can be General");
        cmd.AddChild(fold);
        if (!_generalsOpen) return;
        var rows = new VBoxContainer { MouseFilter = MouseFilterEnum.Ignore };
        rows.AddThemeConstantOverride("separation", 8);
        foreach (var (c, days) in able.Take(Shown))
        {
            string name = S.CharName(c.Id);
            var f = c.AttachedFleet != 0 && S.Game.State.Fleets.TryGetValue(c.AttachedFleet, out var af) ? af : null;
            string at = Cm.LocationOf(c) ?? "";
            string post = PostNow(c);   // free leaders come first; a posted one says so on the button
            string where = post ?? (f != null ? $"on {S.NameOf(f)}" : at);
            string when = days == 0 ? "here" : $"{Ui.Count(days, "day", "days")} away";
            var b = FleetAction($"{name} · {where} · {when}", $"General candidate {name}",
                () => { if (f != null) OpenMgmt(MgKind.Fleet, fleet: f.Id, tab: "Command"); else OpenMgmt(MgKind.Ground, Cm.LocationOf(c), tab: "Command"); },
                $"{name}{(post != null ? ", " + post + "," : "")} stands {(f != null ? $"on {S.NameOf(f)} at {f.At}" : $"at {at}")}. Open that window and drag {name} onto this General post"
                + (days == 0 ? "." : $": they fly here first, {Ui.Count(days, "day", "days")}."));
            b.SizeFlagsHorizontal = SizeFlags.ShrinkBegin;
            rows.AddChild(b);
        }
        cmd.AddChild(rows);
        if (able.Count > Shown)
        {
            var more = Look.Words($"and {able.Count - Shown} more in Leaders (C).", 12, Look.Muted);
            cmd.AddChild(more);
        }
    }

    /// <summary>The way to the planet's Infrastructure window, as its own "Ground and military" button the other way.</summary>
    Button InfraButton(string n, InfraTab? open = null, string words = "Infrastructure")
    {
        bool mine = S.OwnerOf(n) == Me;
        var b = FleetAction(words, $"{words} at {n}", () =>
            {
                if (open is InfraTab t && FamilyOfTab(t) is { } fam) OpenInfraOn(n, t, fam);
                else OpenMgmt(MgKind.Infra, n);
            },
            mine ? $"The Infrastructure window of {n}: its buildings, its slots, and what its yards build."
                 : $"The Infrastructure window of {n}: what your side last saw built there.",
            icon: open == InfraTab.Training ? PixelIconData.Tile["troops"] : PixelIconData.Tile["buildings"]);
        b.SizeFlagsHorizontal = SizeFlags.ShrinkBegin;
        return b;
    }

    /// <summary>A row of the frames' buttons, 8 px apart, wrapping on a narrow page.</summary>
    static HFlowContainer ButtonRow()
    {
        var bar = new HFlowContainer { MouseFilter = MouseFilterEnum.Ignore };
        bar.AddThemeConstantOverride("h_separation", 8); bar.AddThemeConstantOverride("v_separation", 8);
        return bar;
    }

    /// <summary>When a part of your picture of a planet you don't hold was seen, in the status grey.</summary>
    Label SeenLine(int day)
    {
        int ago = S.Game.Day - day;
        var l = Look.Words(ago <= 0 ? $"Seen today (day {day})." : $"Seen day {day} ({Ui.DaysAgo(ago)}): they may have moved since.", 12, Look.Soft);
        l.AutowrapMode = TextServer.AutowrapMode.WordSmart;
        return l;
    }

    // ================================================================== Troops

    /// <summary>The frame's Troops tab on your planet: the regiments, then the special forces, each a card; then Train troops (the Infrastructure
    /// window on its training facilities). Any enemy unit standing here shows after them, in the enemy's colour.</summary>
    void GroundTroops(string n, List<EconObject> regs, List<EconObject> sf)
    {
        var rs = FleetSection("Regiments", 16, null, "Below 60% loyalty a planet needs regiments to stay calm: one for every 10 points under 60. Too few and it rises up.");
        var rz = DropZone($"Ground {n} Regiments", "land", d => RefuseToGround(d, n, 'R'), d => DropToGround(d, n));
        CardFlow(rz.Flow);
        UnitTiles(rz.Flow, regs.Select(o => ((Drag?)new Drag('R', o.Id), o, (string)null)), n, UnitCard);
        if (regs.Count == 0) rz.Flow.AddChild(EmptyCard("Drop regiments here to land them", FleetBodyW, WideEmptyH));
        rs.AddChild(rz.Box);

        // special forces: they never ride fleets (m3-troops-and-special-forces); dragged to another system's window they go there (a Move mission)
        var ss = FleetSection("Special forces", 16, null, "Agents never ride fleets. Right-click one to send it on a mission; drag it onto another system's Troops tab to move it there.");
        ss.AddChild(SpecialForcesZone(n, sf));

        // enemy ground units on your own planet, live (an invasion under way, or agents caught)
        var foes = S.Host.Troops.RegimentsOnSystem(n).Where(o => o.Side != Me).OrderBy(o => o.Class, StringComparer.Ordinal).ThenBy(o => o.Id).ToList();
        if (foes.Count > 0)
        {
            var es = FleetSection("Enemy regiments", 16, foes.Count.ToString(), $"Enemy regiments standing on {n} now.");
            var flow = CardFlow(new HFlowContainer { MouseFilter = MouseFilterEnum.Ignore });
            foreach (var o in foes) flow.AddChild(EnemyUnitCard(o.Class, o.Side == Side.Hand, null));
            es.AddChild(flow);
        }

        var bar = ButtonRow();
        bar.AddChild(InfraButton(n, InfraTab.Training, "Train troops"));
        if (sf.Any(SfReady))
            bar.AddChild(FleetAction("Send a mission...", $"Mission from {n}", () => { CloseSectorView(); OpenPlanner(n); },
                "Pick a team standing here (leaders and special forces), the kind of mission and the target."));
        Add(bar);
    }

    /// <summary>Your special forces at a system, each a card (drag to move it, right-click for a mission), in their drop place.</summary>
    Control SpecialForcesZone(string n, List<EconObject> sf)
    {
        var sz = DropZone($"Ground {n} SpecialForces", "send here", d => RefuseToGround(d, n, 'F'), d => DropToGround(d, n));
        CardFlow(sz.Flow);
        int shown = 0;
        foreach (var o in sf)
        {
            Drag? d = SfReady(o) ? new Drag('F', o.Id) : null;
            if (d is { } dd && _moved.ContainsKey(dd.Key)) continue;
            string name = S.SpecialForceName(o.Id);
            var card = FleetCard(UnitPic(UnitIcon.Kind.Agent, Me == Side.Hand, 1, d == null ? Look.Soft : Ui.You, o.Class), name, SpecialForceState(o));
            card.TooltipText = Ui.Wrap(name + (ClassTip(o.Class) is string ct ? "\n" + ct : "") + (d != null ? "\nRight-click to send it on a mission. Drag it onto another system to move it there." : ""));
            if (d is { } drag) { card.SetMeta(UiTest.FullName, drag.Key); MakeDraggable(card, new List<Drag> { drag }, name); MissionMenu(card, n, null, o.Id); }
            sz.Flow.AddChild(card);
            shown++;
        }
        if (shown == 0) sz.Flow.AddChild(EmptyCard("Drop special forces here to send them", FleetBodyW, WideEmptyH));
        return sz.Box;
    }

    /// <summary>An enemy unit as a card: its class's picture in the enemy's colour on the dark tile, its class. Read only.</summary>
    PanelContainer EnemyUnitCard(string cls, bool hand, int? seenDay)
    {
        bool agent = S.Host.Troops.Data.SpecialForce(cls) != null;
        var card = FleetCard(UnitPic(agent ? UnitIcon.Kind.Agent : UnitIcon.Kind.Regiment, hand, 1, Ui.Enemy, cls), cls);
        card.TooltipText = Ui.Wrap($"{cls}, of the {S.NameOf(hand ? Side.Hand : Side.Collective)}" + (seenDay is int d ? $": seen here on day {d}." : ".")
            + (ClassTip(cls) is string ct ? "\n" + ct : ""));
        card.SetMeta(UiTest.FullName, "Enemy unit " + cls);
        return card;
    }

    /// <summary>The enemy's ground units in your picture of a planet (group D, per side and class), one entry per unit (Paul 2026-11-14: one
    /// unit per card), regiments first; and the day they were seen. rule: m5-fog-and-hq §F1 group D.</summary>
    (List<(string Class, bool Hand, bool Agent)> Units, int? Day) EnemyGround(SystemPicture pic)
    {
        var list = new List<(string, bool, bool)>();
        if (pic?.Ground is not { } gr) return (list, null);
        foreach (var g in gr.Value.Where(x => x.Side != Me).OrderBy(x => (int)x.Side))
            foreach (var (cls, count) in g.Units.OrderBy(kv => S.Host.Troops.Data.SpecialForce(kv.Key) != null).ThenBy(kv => kv.Key, StringComparer.Ordinal))
                for (int i = 0; i < count; i++) list.Add((cls, g.Side == Side.Hand, S.Host.Troops.Data.SpecialForce(cls) != null));
        return (list, gr.Stamp);
    }

    /// <summary>
    /// The Troops tab of a planet you don't hold: the enemy's regiments and special forces as your side last saw them, each a card in the
    /// enemy's colour, with the day; nothing to drag. Your own special forces there, if any, as on your planets.
    /// </summary>
    void GroundTroopsSeen(string n, (List<(string Class, bool Hand, bool Agent)> Units, int? Day) seen, List<EconObject> sf, SystemPicture pic)
    {
        var regs = seen.Units.Where(u => !u.Agent).ToList();
        var agents = seen.Units.Where(u => u.Agent).ToList();
        // a fleet in orbit never sees the ground (rule: m5-fog-and-hq §F3, group D); an Espionage success or an invasion does. Seen live, a
        // neutral world has no defenders at all: regiments would make their side its holder, and an assault meets only the holder's
        // (m2-control-and-capture, Derived; m3-assault §0), so there what can stop a landing is its defences (§1, check 4), buildings (review 33)
        bool orbit = S.Game.State.Fleets.Values.Any(f => f.Side == Me && f.At == n);
        bool neutralNow = S.Sees(n) && S.KnownOwnerOf(n) is (true, null);
        var rs = FleetSection("Regiments", 16, neutralNow ? "0" : seen.Day == null ? null : regs.Count.ToString(),   // never seen: no made-up 0
            $"The regiments your side last saw on {n}. You can land troops only on a planet you hold: to take it, invade from a fleet in orbit.");
        if (neutralNow)
        {
            var free = Para($"No troops defend a neutral world: an invasion with regiments aboard takes it, unless its defences stop the landing. "
                + (pic?.Facilities is { } fac ? $"Its defences, seen day {fac.Stamp}, are in its Infrastructure window." : $"Its defences are unseen from orbit: {BuildingsMissions} shows them."));
            free.SetMeta(UiTest.FullName, $"Ground troops unseen {n}");
            rs.AddChild(free);
        }
        else if (seen.Day is not int never)
        {
            var blind = Para(pic == null ? $"Unexplored: you don't know who is on the ground at {n}. A fleet there shows who holds it; an Espionage mission shows its troops."
                : orbit ? $"The ground of {n} has never been seen. Your fleet here sees ships, not troops: an Espionage mission shows them."
                : $"The ground of {n} has never been seen. An Espionage mission shows its troops.");
            blind.SetMeta(UiTest.FullName, $"Ground troops unseen {n}");
            rs.AddChild(blind);
        }
        else
        {
            rs.AddChild(SeenLine(never));
            if (orbit && never < S.Game.Day) rs.AddChild(Para("Your fleet here sees ships, not troops: an Espionage mission looks again."));
            if (regs.Count > 0)
            {
                var flow = CardFlow(new HFlowContainer { MouseFilter = MouseFilterEnum.Ignore });
                foreach (var u in regs) flow.AddChild(EnemyUnitCard(u.Class, u.Hand, never));
                rs.AddChild(flow);
            }
            else rs.AddChild(Para("No regiments were seen."));
        }
        if (agents.Count > 0 && seen.Day is int day)
        {
            var ss = FleetSection("Special forces", 16, agents.Count.ToString(), $"The enemy agents your side last saw on {n}.");
            var flow = CardFlow(new HFlowContainer { MouseFilter = MouseFilterEnum.Ignore });
            foreach (var u in agents) flow.AddChild(EnemyUnitCard(u.Class, u.Hand, day));
            ss.AddChild(flow);
        }
        if (sf.Count > 0)
        {
            var ys = FleetSection("Your special forces", 16, sf.Count.ToString(), "Your agents standing here. Right-click one to send it on a mission.");
            ys.AddChild(SpecialForcesZone(n, sf));
        }
    }

    /// <summary>A short paragraph on the page, 12 px, wrapping.</summary>
    static Label Para(string words, Color? ink = null)
    {
        var l = Look.Words(words, 12, ink ?? Look.Text);
        l.AutowrapMode = TextServer.AutowrapMode.WordSmart;
        return l;
    }

    // ================================================================== Fighters

    void GroundFighters(string n, List<EconObject> based)
    {
        var fs = FleetSection("Fighters", 16, null, "Squadrons based on the planet defend it. Drag them onto a fleet's Fighters tab to load them on a carrier.");
        var fz = DropZone($"Ground {n} Fighters", "base here", d => RefuseToGround(d, n, 'Q'), d => DropToGround(d, n));
        CardFlow(fz.Flow);
        UnitTiles(fz.Flow, based.Select(o => ((Drag?)new Drag('Q', o.Id), o, HullWords(o))), n, UnitCard);
        if (based.Count == 0) fz.Flow.AddChild(EmptyCard("Drop squadrons here to base them", FleetBodyW, WideEmptyH));
        fs.AddChild(fz.Box);
    }

    /// <summary>A planet you don't hold: its own squadrons are never seen from orbit (the picture has no such group, rule: m5-fog-and-hq §F1);
    /// those aboard its fleets show in Fleets.</summary>
    void GroundFightersSeen()
    {
        var fs = FleetSection("Fighters", 16);
        fs.AddChild(Para("Squadrons based on a planet you don't hold are never seen: only those aboard its fleets, in Fleets.", Look.Soft));
    }

    // ================================================================== Fleets

    /// <summary>The enemy fleets at a system: those you see now, else those of your last look, and that day (null when live).</summary>
    (List<FleetPicture> List, int? Day) EnemyFleetsAt(string n, SystemPicture pic)
    {
        if (S.Sees(n)) return (S.VisibleEnemyFleetsAt(n).ToList(), null);
        if (pic?.Fleets is { } pf) return (pf.Value.Where(f => f.Side != Me).ToList(), pf.Stamp);
        return (new List<FleetPicture>(), null);
    }

    /// <summary>Your fleets in orbit (a row each: drop ships, troops, fighters or leaders on it, click to open its window), then the enemy's,
    /// live or as last seen.</summary>
    void GroundFleets(string n, List<Fleet> own, List<FleetPicture> foes, int? foesDay)
    {
        var ys = FleetSection("Your fleets", 16, own.Count.ToString());
        if (own.Count == 0) ys.AddChild(Para($"No fleet of yours is in orbit at {n}.", Look.Soft));
        foreach (var f in own) ys.AddChild(FleetRow(f, false, null));
        if (foes.Count == 0 && foesDay == null && !S.Sees(n)) return;
        var es = FleetSection("Enemy fleets", 16, foes.Count.ToString());
        if (foesDay is int d) es.AddChild(SeenLine(d));
        if (foes.Count == 0) es.AddChild(Para(S.Sees(n) ? "None in orbit now." : "None were seen.", Look.Soft));
        foreach (var fp in foes) es.AddChild(EnemyFleetRow(fp, foesDay));
    }

    /// <summary>An enemy fleet as your side sees it (class counts only, never its name): its biggest ship's picture in the enemy's colour, its
    /// ships and squadrons. rule: m5-fog-and-hq §F1 group E.</summary>
    Control EnemyFleetRow(FleetPicture fp, int? day)
    {
        var box = new PanelContainer { MouseFilter = MouseFilterEnum.Stop };
        box.AddThemeStyleboxOverride("panel", ZoneStyle(null, "row"));
        var h = Boxed(new HBoxContainer(), 8);
        bool hand = fp.Side == Side.Hand;
        var big = fp.Ships.Keys.OrderByDescending(SizeOfClass).ThenBy(c => c, StringComparer.Ordinal).FirstOrDefault();
        var icon = UnitIcon.For(UnitIcon.Kind.Ship, hand, Ui.Enemy, big != null ? SizeOfClass(big) : 1, new Vector2(46, 26), cls: big);
        icon.SizeFlagsVertical = SizeFlags.ShrinkCenter;
        h.AddChild(icon);
        var v = Boxed(new VBoxContainer(), 0);
        v.SizeFlagsHorizontal = SizeFlags.ExpandFill;
        v.AddChild(Look.Words($"Fleet of the {S.NameOf(fp.Side)}", 12, Ui.Enemy, true));
        string what = Ui.Count(fp.ShipCount, "ship", "ships") + (fp.SquadronCount > 0 ? $", {Ui.Count(fp.SquadronCount, "squadron", "squadrons")}" : "");
        var sl = Look.Words(what, 12, Look.Soft);
        sl.ClipText = true;
        v.AddChild(sl);
        h.AddChild(v);
        box.AddChild(h);
        box.TooltipText = Ui.Wrap((day is int d ? $"Seen here on day {d}: " : "In orbit now: ") + fp);
        box.SetMeta(UiTest.FullName, "Enemy fleet " + fp);
        return box;
    }
}
