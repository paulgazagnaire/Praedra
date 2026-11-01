using System;
using System.Collections.Generic;
using System.Linq;
using Godot;
using Praedra.Core.Economy;
using Praedra.Core.Fog;
using Praedra.Core.Fleets;

using Side = Praedra.Core.Clock.Side;

namespace PraedraViewer;
/// <summary>The sector window, part two: one system card and the numbers it shows. See GameScreen.SectorView.cs.</summary>
public partial class GameScreen
{
    // ================================================================== one card

    PanelContainer SystemCardTile(string n)
    {
        bool dead = S.IsDestroyed(n);
        bool mine = !dead && S.OwnerOf(n) == Me;
        var pic = S.Picture(n);
        bool explored = mine || pic != null;
        var (known, owner) = S.KnownOwnerOf(n);
        Side? holder = mine ? Me : dead ? null : owner;
        Color oc = dead ? Ui.Danger : !explored || (!known && !mine) ? Ui.Text3 : Ui.OwnerColor(holder, Me);
        bool focused = _secDetail.Visible && _secFocus == n;

        var card = new PanelContainer { CustomMinimumSize = new Vector2(CardMinWidth, _secDense ? 0 : CardMinHeight), SizeFlagsHorizontal = SizeFlags.ExpandFill, MouseFilter = MouseFilterEnum.Stop, MouseDefaultCursorShape = CursorShape.PointingHand };
        card.SetMeta(UiTest.FullName, "Sector card " + n);
        StyleBoxFlat Style(bool hover) => Ui.Box(hover ? Ui.CardHoverBg : Ui.CardBg, 12, focused ? oc : hover ? new Color(1, 1, 1, 0.24f) : new Color(1, 1, 1, 0.07f), focused ? 2 : 1, 0, 0);
        card.AddThemeStyleboxOverride("panel", Style(false));
        // a card rebuilt under a still mouse (a new day) keeps its hover look
        Callable.From(() => { if (IsInstanceValid(card) && card.IsVisibleInTree() && card.GetGlobalRect().HasPoint(card.GetGlobalMousePosition())) card.AddThemeStyleboxOverride("panel", Style(true)); }).CallDeferred();
        card.MouseEntered += () => { card.AddThemeStyleboxOverride("panel", Style(true)); _secMini.Hot = n; _secMini.QueueRedraw(); };
        card.MouseExited += () => { card.AddThemeStyleboxOverride("panel", Style(false)); if (_secMini.Hot == n) { _secMini.Hot = null; _secMini.QueueRedraw(); } };
        card.GuiInput += e =>
        {
            if (e is not InputEventMouseButton { Pressed: true, ButtonIndex: MouseButton.Left } mb) return;
            // the card is rebuilt by what the click does: do it after this input event is over
            if (mb.DoubleClick) { Later(() => ShowOnMap(n)); return; }
            Later(() => { if (focused) CloseSystemPane(); else OpenSystemPane(n, _secPane); });
        };
        card.TooltipText = Ui.Wrap($"{n}: click for its window (buildings, fleets, forces). Double-click to show it on the map.");
        var h = Ui.HBox(0);
        h.MouseFilter = MouseFilterEnum.Ignore;
        card.AddChild(h);

        // ---- the planet band on the left: the planet large and cropped, the owner's colour as a thin line down its edge
        var art = new Control { CustomMinimumSize = new Vector2(CardArt, 0), ClipContents = true, MouseFilter = MouseFilterEnum.Ignore, SizeFlagsHorizontal = SizeFlags.ExpandFill, SizeFlagsStretchRatio = 0.5f };
        h.AddChild(art);
        var sky = new Panel { MouseFilter = MouseFilterEnum.Ignore };
        var skyBox = new StyleBoxFlat { BgColor = Ui.PlanetSky };
        skyBox.CornerRadiusTopLeft = 11; skyBox.CornerRadiusBottomLeft = 11;
        sky.AddThemeStyleboxOverride("panel", skyBox);
        sky.SetAnchorsAndOffsetsPreset(LayoutPreset.FullRect);
        art.AddChild(sky);
        if (PlanetArt.Of(n) is Texture2D tex)
        {
            float px = PlanetArt.Dots * 2; // two screen pixels per dot: the dither stays crisp
            var tr = new TextureRect { Texture = tex, TextureFilter = TextureFilterEnum.Nearest, ExpandMode = TextureRect.ExpandModeEnum.IgnoreSize, StretchMode = TextureRect.StretchModeEnum.Scale, MouseFilter = MouseFilterEnum.Ignore };
            tr.SetAnchorsPreset(LayoutPreset.Center);
            tr.OffsetLeft = -px / 2; tr.OffsetRight = px / 2; tr.OffsetTop = -px / 2; tr.OffsetBottom = px / 2;
            if (dead) tr.Modulate = new Color(0.6f, 0.22f, 0.2f, 0.5f);
            else if (!explored) tr.Modulate = new Color(0.26f, 0.27f, 0.3f, 1f);
            art.AddChild(tr);
        }
        var fade = new TextureRect { Texture = FadeTexture, ExpandMode = TextureRect.ExpandModeEnum.IgnoreSize, StretchMode = TextureRect.StretchModeEnum.Scale, MouseFilter = MouseFilterEnum.Ignore };
        fade.SetAnchorsAndOffsetsPreset(LayoutPreset.RightWide);
        fade.OffsetLeft = -40;
        art.AddChild(fade);
        var stripe = new ColorRect { Color = new Color(oc, mine ? 0.95f : 0.6f), CustomMinimumSize = new Vector2(2, 0), MouseFilter = MouseFilterEnum.Ignore };
        h.AddChild(stripe);

        // ---- the right side: name, owner, the numbers, the three links
        var pad = new MarginContainer { SizeFlagsHorizontal = SizeFlags.ExpandFill, MouseFilter = MouseFilterEnum.Ignore };
        foreach (var (k, px) in new[] { ("margin_left", 14), ("margin_right", 14), ("margin_top", _secDense ? 7 : 11), ("margin_bottom", _secDense ? 7 : 11) }) pad.AddThemeConstantOverride(k, px);
        h.AddChild(pad);
        var v = Ui.VBox(_secDense ? 3 : 6);
        v.MouseFilter = MouseFilterEnum.Ignore;
        pad.AddChild(v);

        var nameRow = Ui.HBox(8);
        nameRow.MouseFilter = MouseFilterEnum.Ignore;
        var nm = Ui.Label(n, null, Ui.Text, 17, wrap: false);
        nm.AddThemeFontOverride("font", Ui.SemiBold);
        nm.ClipText = true;
        nm.SizeFlagsHorizontal = SizeFlags.ExpandFill;
        nameRow.AddChild(nm);
        string ownWord = dead ? "DESTROYED" : mine ? (n == S.MyHqSystem ? (Me == Side.Hand ? "CAPITAL" : "HQ") : "YOURS") : !explored ? "UNEXPLORED"
            : !known ? "OWNER ?" : owner == null ? "NOBODY'S" : n == S.EnemyHqSystem ? (Me == Side.Hand ? "THEIR HQ" : "THEIR CAPITAL") : "ENEMY";
        nameRow.AddChild(Ui.Tag(ownWord, mine && n == S.MyHqSystem ? Ui.Gold : oc));
        v.AddChild(nameRow);
        var sub = Ui.HBox(6);
        sub.MouseFilter = MouseFilterEnum.Ignore;
        var kl = Ui.Label((PlanetArt.KindOf(n) ?? "").ToUpperInvariant(), null, Ui.Text3, 10, wrap: false);
        kl.AddThemeFontOverride("font", Ui.Caps);
        kl.SizeFlagsHorizontal = SizeFlags.ExpandFill;
        kl.SizeFlagsVertical = SizeFlags.ShrinkCenter;
        sub.AddChild(kl);
        foreach (var (word, c, tip) in Alarms(n, mine)) { var t = Ui.Tag(word, c, true); t.TooltipText = Ui.Wrap(tip); t.MouseFilter = MouseFilterEnum.Pass; sub.AddChild(t); }
        v.AddChild(sub);
        if (!_secDense) v.AddChild(Ui.Space(1));

        var st = Ui.VBox(_secDense ? 3 : 6);
        st.MouseFilter = MouseFilterEnum.Ignore;
        st.SizeFlagsVertical = SizeFlags.ExpandFill;
        v.AddChild(st);
        if (dead) DeadStats(st, n);
        else if (mine) OwnStats(st, n);
        else if (!explored) { var q = Quiet("Never looked at."); q.TooltipText = Ui.Wrap("Send a fleet, or a spy on an Espionage mission, to see who holds it and what is there."); q.MouseFilter = MouseFilterEnum.Pass; st.AddChild(q); }
        else ForeignStats(st, n, pic);

        var links = Ui.HBox(6);
        links.MouseFilter = MouseFilterEnum.Ignore;
        v.AddChild(links);
        var (bCount, bTip, bColor) = BuildingsLink(n, mine, pic, dead);
        var (fCount, fTip, fColor) = FleetsLink(n, pic);
        var (mCount, mTip, mColor) = ForcesLink(n, mine, pic, dead);
        links.AddChild(CardLink(n, SecPane.Buildings, "BUILDINGS", bCount, bColor, bTip, focused));
        links.AddChild(CardLink(n, SecPane.Fleets, "FLEETS", fCount, fColor, fTip, focused));
        links.AddChild(CardLink(n, SecPane.Forces, "FORCES", mCount, mColor, mTip, focused));
        return card;
    }

        static Texture2D _fade;
    /// <summary>A fade from clear to the card's ground at the right edge of the planet band, so the planet melts into the card.</summary>
    static Texture2D FadeTexture => _fade ??= new GradientTexture2D
    {
        Gradient = new Gradient { Colors = new[] { new Color(Ui.CardBg, 0f), Ui.CardBg }, Offsets = new[] { 0f, 1f } },
        FillFrom = new Vector2(0, 0), FillTo = new Vector2(1, 0), Width = 64, Height = 4,
    };

    Label Quiet(string text)
    {
        var l = Ui.Label(text, null, Ui.Text3, Ui.Small);
        return l;
    }

    /// <summary>The words that must jump out of a card: uprising, blockade, an enemy fleet, a harvester.</summary>
    List<(string, Color, string)> Alarms(string n, bool mine)
    {
        var a = new List<(string, Color, string)>();
        if (S.IsDestroyed(n)) return a;
        bool sees = S.Sees(n);
        if (sees && S.VisibleEnemyFleetsAt(n).Count > 0) a.Add(("ENEMY FLEET", Ui.Danger, $"Enemy fleet here now: {string.Join("; ", S.VisibleEnemyFleetsAt(n))}"));
        if (sees && S.Host.Support.IsUprising(n)) a.Add(("UPRISING", Ui.Danger, "The people are in revolt: yards and mines stop until it ends."));
        if (sees && S.Game.State.Blockades.TryGetValue(n, out var b)) a.Add(("BLOCKADE", b == Me ? Ui.You : Ui.Warn, b == Me ? "Your fleet blockades it: its mines and yards stop." : "Blockaded by the enemy: mines and yards here stop."));
        if (!mine && S.Picture(n)?.HarvesterQueued is { Value: true }) a.Add(("HARVESTER", Ui.Danger, "A harvester was being built here when you last looked."));
        return a;
    }

    void OwnStats(VBoxContainer st, string n)
    {
        var eco = S.Host.Economy;
        int sup = eco.SupportOf(n, Me);
        Color sc = sup < 40 ? Ui.Enemy : sup >= 60 ? Ui.You : Ui.Text;
        st.AddChild(Meter("SUPPORT", $"{sup}%", sc, sup / 100f, sc, $"How much the people here back you (the {S.NameOf(S.Enemy)} has {100 - sup}%). Low support slows mines and yards, needs troops, and below a point the planet may go over."));
        var slots = eco.SlotsOf(n);
        int used = eco.EnergyUsed(n);
        st.AddChild(Pips("ENERGY", used, slots.Energy, Ui.You, $"Energy: the building slots of this system. {slots.Energy - used} free of {slots.Energy}; every building takes one."));
        int mines = eco.OfKindAt(n, FacilityKind.Mine), rawSlots = eco.EffectiveRaw(n);
        st.AddChild(Pips("RAW", mines, rawSlots, Ui.Gold, $"Raw material slots, where mines can dig: {mines} mines on {rawSlots} slots. A mine takes one raw slot and one energy slot."));
        int raw = RawPerMonth(n), refs = eco.OfKindAt(n, FacilityKind.Refinery), refined = RefinedPerMonth(n);
        int smug = mines + refs > 0 ? eco.DiversionPercent(n, Me) : 0;
        st.AddChild(Pair("OUTPUT / 30 D", raw == 0 && refined == 0 ? "–" : $"+{raw} raw  +{refined} ref.", smug > 0 ? Ui.Warn : raw + refined > 0 ? Ui.Good : Ui.Text3,
            $"Every 30 days at today's support, about: {raw} raw dug by {Ui.Count(mines, "mine", "mines")}, {refined} refined made by {Ui.Count(refs, "refinery", "refineries")} (when there is raw to refine). Both go to your side's pools."
            + (smug > 0 ? $" Smugglers may take a cycle's output {smug}% of the time here: troops, fighters and ships on the planet scare them off." : "")));
        int have = S.Host.Support.RegimentCount(n), need = S.Host.Support.RequiredGarrison(n);
        var two = Ui.HBox(18);
        two.MouseFilter = MouseFilterEnum.Ignore;
        var g = Pair("TROOPS", need == 0 ? $"{have}" : $"{have}/{need}", have < need ? Ui.Warn : Ui.Text,
            need == 0 ? $"{Ui.Count(have, "regiment", "regiments")} here; none needed at this support." : $"{have} regiments of the {need} needed to keep order. Below 60% support a system needs one regiment for every 10 points under 60.");
        g.SizeFlagsHorizontal = SizeFlags.ExpandFill; g.SizeFlagsStretchRatio = 0.7f;
        two.AddChild(g);
        var (what, wc) = YardsWords(n);
        var y = Pair("YARDS", what, wc, "What the yards here build now, and how many days the current one still needs.");
        y.SizeFlagsHorizontal = SizeFlags.ExpandFill;
        two.AddChild(y);
        st.AddChild(two);
    }

    void ForeignStats(VBoxContainer st, string n, SystemPicture pic)
    {
        bool live = S.Sees(n);
        int? last = S.LastSeenDay(n);
        int age = last is int l ? S.Game.Day - l : int.MaxValue;
        string when = live ? "seen live now" : last is int ld ? $"last seen day {ld} ({Ui.DaysAgo(age)})" : "not seen since the war began";
        var seen = Ui.Label(when.ToUpperInvariant(), null, live ? Ui.Good : age > 10 ? Ui.Warn : Ui.Text3, 11, wrap: false);
        seen.AddThemeFontOverride("font", Ui.Caps);
        st.AddChild(seen);
        if (pic.Owner is { } po)
        {
            int s = Me == Side.Collective ? po.Value.CollectiveSupport : 100 - po.Value.CollectiveSupport;
            st.AddChild(Meter("SUPPORT FOR YOU", $"{s}%", Ui.Text2, s / 100f, new Color(Ui.Text2, 0.7f), $"What the people thought of you on day {po.Stamp}. A Diplomacy mission raises it."));
        }
        else st.AddChild(Pair("SUPPORT FOR YOU", "?", Ui.Text3, "Not known."));
        if (pic.Status is { } ss) st.AddChild(Pair("SLOTS", $"{ss.Value.Energy} energy · {ss.Value.Raw} raw", Ui.Text2, $"Seen day {ss.Stamp}. {(ss.Value.Populated ? "Populated." : "Nobody lives here.")}"));
        if (pic.Facilities is { } fac) st.AddChild(Pair("BUILDINGS", fac.Value.Values.Sum() == 0 ? "none" : fac.Value.Values.Sum().ToString(), Ui.Text2,
            fac.Value.Count == 0 ? $"None seen (day {fac.Stamp})." : $"Seen day {fac.Stamp}: " + string.Join(", ", fac.Value.OrderBy(kv => kv.Key, StringComparer.Ordinal).Select(kv => kv.Value == 1 ? kv.Key : $"{kv.Value} x {kv.Key}"))));
        else st.AddChild(Pair("BUILDINGS", "?", Ui.Text3, "Nobody has looked at the ground yet."));
        if (pic.Ground is { } gr)
        {
            int t = gr.Value.Where(g => g.Side != Me).Sum(g => g.Total);
            st.AddChild(Pair("TROOPS", t.ToString(), t > 0 ? Ui.Enemy : Ui.Text2, $"Regiments of others seen on the ground, day {gr.Stamp}."));
        }
        else st.AddChild(Pair("TROOPS", "?", Ui.Text3, "Not known."));
    }

    void DeadStats(VBoxContainer st, string n)
    {
        string d = S.DestroyedDay(n) is int day ? $"on day {day}" : "";
        st.AddChild(Ui.Label($"Harvested {d}. Nothing is left: no people, no buildings, no troops. Fleets can still pass and stop here.".Replace("  ", " "), null, new Color("ffb3b3"), Ui.Small));
    }

    /// <summary>A key, a value and a bar under them.</summary>
    Control Meter(string key, string value, Color valueColor, float share, Color fill, string tip)
    {
        var v = Ui.VBox(5);
        v.AddChild(Pair(key, value, valueColor, tip));
        v.AddChild(Ui.Bar(share, fill, 4, new Color(1, 1, 1, 0.06f)));
        v.MouseFilter = MouseFilterEnum.Ignore;
        return v;
    }

    /// <summary>A key in small capitals on the left, a value on the right, both one line.</summary>
    static HBoxContainer Pair(string key, string value, Color valueColor, string tip) => Ui.Row(key, value, valueColor, tip, "StatLabel", 13);

    /// <summary>Slots as a row of small blocks: filled for used, hollow for free (up to 15 slots, the slot cap).</summary>
    Control Pips(string key, int used, int total, Color c, string tip)
    {
        var h = Ui.HBox(8);
        h.MouseFilter = MouseFilterEnum.Pass;
        h.TooltipText = Ui.Wrap(tip);
        var k = Ui.Label(key, "StatLabel", wrap: false);
        k.CustomMinimumSize = new Vector2(52, 0);
        k.SizeFlagsVertical = SizeFlags.ShrinkCenter;
        h.AddChild(k);
        var pips = Ui.HBox(2);
        pips.SizeFlagsHorizontal = SizeFlags.ExpandFill;
        pips.SizeFlagsVertical = SizeFlags.ShrinkCenter;
        pips.MouseFilter = MouseFilterEnum.Ignore;
        for (int i = 0; i < Math.Min(total, 15); i++)
        {
            var p = new Panel { CustomMinimumSize = new Vector2(5, 10), MouseFilter = MouseFilterEnum.Ignore };
            p.AddThemeStyleboxOverride("panel", i < used ? Ui.Box(c, 1, null, 0, 0, 0) : Ui.Box(new Color(1, 1, 1, 0.09f), 1, null, 0, 0, 0));
            pips.AddChild(p);
        }
        h.AddChild(pips);
        int free = Math.Max(0, total - used);
        var val = Ui.Label(total == 0 ? "none" : $"{free} free / {total}", null, total == 0 || free == 0 ? Ui.Text3 : Ui.Text, 13, wrap: false);
        val.AddThemeFontOverride("font", Ui.NumBold);
        h.AddChild(val);
        return h;
    }

    Button CardLink(string n, SecPane pane, string word, string count, Color color, string tip, bool focused)
    {
        bool on = focused && _secPane == pane;
        var b = new Button { FocusMode = FocusModeEnum.None, MouseDefaultCursorShape = CursorShape.PointingHand, SizeFlagsHorizontal = SizeFlags.ExpandFill, CustomMinimumSize = new Vector2(0, 28) };
        b.TooltipText = Ui.Wrap(tip);
        b.SetMeta(UiTest.FullName, $"{n} {word.ToLowerInvariant()}");
        b.AddThemeStyleboxOverride("normal", Ui.Box(on ? new Color(color, 0.16f) : new Color(1, 1, 1, 0.025f), 8, on ? color : new Color(1, 1, 1, 0.07f), 1, 4, 4));
        b.AddThemeStyleboxOverride("hover", Ui.Box(new Color(color, 0.12f), 8, new Color(color, 0.7f), 1, 4, 4));
        b.AddThemeStyleboxOverride("pressed", Ui.Box(new Color(color, 0.2f), 8, color, 1, 4, 4));
        b.AddThemeStyleboxOverride("focus", new StyleBoxEmpty());
        b.Pressed += () => Later(() => { if (on) CloseSystemPane(); else OpenSystemPane(n, pane); });
        var row = Ui.HBox(6);
        row.MouseFilter = MouseFilterEnum.Ignore;
        row.SetAnchorsAndOffsetsPreset(LayoutPreset.FullRect);
        row.Alignment = BoxContainer.AlignmentMode.Center;
        b.AddChild(row);
        var w = Ui.Label(word, null, on ? Ui.Text : Ui.Text2, 10, wrap: false);
        w.AddThemeFontOverride("font", Ui.Caps);
        w.SizeFlagsVertical = SizeFlags.ShrinkCenter;
        row.AddChild(w);
        var c = Ui.Label(count, null, color, 13, wrap: false);
        c.AddThemeFontOverride("font", Ui.NumBold);
        c.SizeFlagsVertical = SizeFlags.ShrinkCenter;
        row.AddChild(c);
        return b;
    }

    (string, string, Color) BuildingsLink(string n, bool mine, SystemPicture pic, bool dead)
    {
        if (dead) return ("–", "Nothing is left here.", Ui.Text3);
        if (mine)
        {
            int c = S.Host.Economy.Objects.Count(o => o.Category == ObjectCategory.Facility && o.System == n && o.Completed);
            int idle = IdleYards(n);
            return (c.ToString(), $"{Ui.Count(c, "building", "buildings")} here{(idle > 0 ? $", {Ui.Count(idle, "idle yard", "idle yards")}" : "")}. Click to build, scrap and see what each one does.", idle > 0 ? Ui.Warn : Ui.Text);
        }
        if (pic?.Facilities is { } f) return (f.Value.Values.Sum().ToString(), $"Buildings seen on day {f.Stamp}. Click for the list.", Ui.Text2);
        return ("?", "Not known.", Ui.Text3);
    }

    (string, string, Color) FleetsLink(string n, SystemPicture pic)
    {
        int own = S.Game.State.Fleets.Values.Count(f => f.Side == Me && f.At == n);
        int enemy = S.Sees(n) ? S.VisibleEnemyFleetsAt(n).Count : 0;
        int seen = !S.Sees(n) && pic?.Fleets is { } pf ? pf.Value.Count : 0;
        if (enemy > 0) return (own > 0 ? $"{own}·{enemy}" : enemy.ToString(), $"Your fleets here: {own}. Enemy fleets here now: {enemy}. Click to see them.", Ui.Danger);
        if (own > 0) return (own.ToString(), $"{Ui.Count(own, "fleet", "fleets")} of yours in orbit. Click to see and command them.", Ui.You);
        if (seen > 0) return (seen.ToString(), $"Enemy fleets seen here when you last looked: {seen}.", Ui.Enemy);
        return ("0", S.Sees(n) ? "No fleet in orbit." : "None of yours; you don't see enemy fleets here.", Ui.Text3);
    }

    (string, string, Color) ForcesLink(string n, bool mine, SystemPicture pic, bool dead)
    {
        int leaders = S.MyCharacters.Count(c => c.Status != Praedra.Core.Characters.CharacterStatus.Dead && Cm.LocationOf(c) == n);
        if (dead) return (leaders > 0 ? leaders.ToString() : "–", "Nothing is left here.", Ui.Text3);
        if (mine)
        {
            int regs = S.Host.Support.RegimentCount(n), need = S.Host.Support.RequiredGarrison(n), fighters = FightersBasedAt(n).Count;
            int total = regs + fighters + leaders;
            return (total.ToString(), $"{Ui.Count(regs, "regiment", "regiments")}, {Ui.Count(fighters, "fighter squadron", "fighter squadrons")} based here, {Ui.Count(leaders, "leader", "leaders")}. Click to train troops and send missions.",
                regs < need ? Ui.Warn : total > 0 ? Ui.Text : Ui.Text3);
        }
        int theirs = pic?.Ground is { } gr ? gr.Value.Where(g => g.Side != Me).Sum(g => g.Total) : -1;
        string count = leaders > 0 ? $"{leaders}" : theirs >= 0 ? theirs.ToString() : "?";
        return (count, leaders > 0 ? $"{Ui.Count(leaders, "leader", "leaders")} of yours here." : theirs >= 0 ? $"Troops of others seen: {theirs}." : "Not known.", leaders > 0 ? Ui.You : theirs > 0 ? Ui.Enemy : Ui.Text3);
    }

    // ================================================================== numbers the cards share

    /// <summary>Yards here with nothing to build.</summary>
    int IdleYards(string n) => IdleYardFamilies(n).Count;

    (string, Color) YardsWords(string n)
    {
        var eco = S.Host.Economy;
        var parts = new List<string>();
        var idleFams = IdleYardFamilies(n);
        bool idle = idleFams.Count > 0;
        foreach (var fam in YardFamiliesAt(n).Where(f => !idleFams.Contains(f)))
            parts.Add($"{eco.Find(S.Host.Construction.ProjectAt(n, fam).Units[0])?.Class ?? "?"} {DaysLeftOf(n, fam)}d");
        if (parts.Count == 0) return idle ? ("idle", Ui.Warn) : ("none", Ui.Text3);
        return (string.Join(", ", parts) + (idle ? " +idle" : ""), idle ? Ui.Warn : Ui.Text);
    }

    /// <summary>About how much raw the mines here dig in 30 days at today's support: one raw a cycle each, the cycle as the core times it
    /// (rule: m2-mines-refineries-pools §Cycle length). A stopped mine (blockade, uprising) digs nothing.</summary>
    int RawPerMonth(string n) => PerMonth(n, FacilityKind.Mine);

    /// <summary>The same for refineries: one refined a cycle each, when the side has raw to refine.</summary>
    int RefinedPerMonth(string n) => PerMonth(n, FacilityKind.Refinery);

    int PerMonth(string n, FacilityKind kind)
    {
        var eco = S.Host.Economy;
        var p = eco.Params;
        string cls = FacilityKinds.ClassName(kind);
        int sup = Math.Clamp(eco.SupportOf(n, Me), p.SupportFloor, p.SupportCeiling);
        double sum = 0;
        foreach (var o in eco.Objects)
        {
            if (o.Category != ObjectCategory.Facility || !o.Completed || o.System != n || o.Class != cls || eco.IsSuspended(o)) continue;
            int d = Math.Max(1, EconomyModule.CycleDays(p, sup, eco.ShareOf(o.Id)));
            sum += (double)MonthDays / d;
        }
        return (int)Math.Round(sum);
    }

    void ShowOnMap(string n)
    {
        CloseSectorView();
        if (!G.TryFindSystem(n, out int idx)) return;
        _view.Centre(idx);
        _view.Select(idx);
        OnViewChanged();
    }
}
