namespace Rebellion.Importer;

/// <summary>
/// Field names of the raw CSVs: sheet section 5 for the static-data files it lists, the entry layouts of section 2 for the table files, and
/// <c>w0..wN</c> for static-data files the sheet does not list. <c>fN</c> = nobody has named word N. rule: B1-own-importer §Work 2;
/// 02-gdata-formats §2, §5.
/// </summary>
public static class Layouts
{
    /// <summary>Record header words 0..11 of the buildable static-data files. rule: 02-gdata-formats §1, §5.</summary>
    private static readonly string[] BuildableHeader =
    {
        "id", "f1", "production_family", "next_production_family", "family", "word5", "is_alliance", "is_empire",
        "cost", "maintenance", "research_order", "research_difficulty",
    };

    /// <summary>Words 12..37 shared by CAPSHPSD and FIGHTSD. rule: 02-gdata-formats §5.</summary>
    private static readonly string[] ShipCommon =
    {
        "uprising_defense", "detection", "shield_strength", "sub_light_engine", "maneuverability", "hyperdrive", "hyperdrive_if_damaged",
        "turbolaser_fore", "ion_cannon_fore", "laser_cannon_fore", "turbolaser_aft", "ion_cannon_aft", "laser_cannon_aft",
        "turbolaser_port", "ion_cannon_port", "laser_cannon_port", "turbolaser_starboard", "ion_cannon_starboard", "laser_cannon_starboard",
        "turbolaser_range", "ion_cannon_range", "laser_cannon_range",
        "turbolaser_attack_strength", "ion_cannon_attack_strength", "laser_cannon_attack_strength", "overall_attack_strength",
    };

    /// <summary>Words 12..36 of MJCHARSD and MNCHARSD. rule: 02-gdata-formats §5.</summary>
    private static readonly string[] CharacterTail =
    {
        "diplomacy", "diplomacy_variance", "espionage", "espionage_variance", "ship_design", "ship_design_variance",
        "troop_training", "troop_training_variance", "facility_design", "facility_design_variance", "combat", "combat_variance",
        "leadership", "leadership_variance", "loyalty", "loyalty_variance", "jedi_probability", "known_jedi", "jedi_level",
        "jedi_level_variance", "can_be_admiral", "can_be_commander", "can_be_general", "unable_to_betray", "jedi_trainer",
    };

    private static string[] Cat(params string[][] parts) => parts.SelectMany(p => p).ToArray();

    /// <summary>The static-data files of sheet section 5, by file name (no extension).</summary>
    public static readonly IReadOnlyDictionary<string, string[]> Sd = new SortedDictionary<string, string[]>(StringComparer.Ordinal)
    {
        ["CAPSHPSD"] = Cat(BuildableHeader, ShipCommon, new[]
        {
            "hull", "tractor_beam_power", "tractor_beam_range", "gravity_well_projector", "interdiction_strength", "bombardment_modifier",
            "damage_control", "weapon_recharge_rate", "shield_recharge_rate", "fighter_capacity", "troop_capacity", "f49",
        }),
        ["FIGHTSD"] = Cat(BuildableHeader, ShipCommon, new[] { "torpedoes", "torpedoes_range", "squadron_size", "bombardment_defense" }),
        ["TROOPSD"] = Cat(BuildableHeader, new[] { "uprising_defense", "detection", "bombardment_defense", "attack", "defense" }),
        ["SPECFCSD"] = Cat(BuildableHeader, new[]
        {
            "f12", "f13", "espionage", "espionage_variance", "f16", "f17", "f18", "f19", "f20", "f21", "combat", "combat_variance",
            "leadership", "leadership_variance", "loyalty_base", "loyalty_variance", "mission_bit",
        }),
        ["DEFFACSD"] = Cat(BuildableHeader, new[] { "bombard_defense", "attack", "shield" }),
        ["MANFACSD"] = Cat(BuildableHeader, new[] { "f12", "f13" }),
        ["PROFACSD"] = Cat(BuildableHeader, new[] { "f12", "f13" }),
        ["ALLFACSD"] = new[] { "id", "f1", "production_family", "next_production_family", "family", "word5", "is_alliance", "is_empire", "f8", "f9", "f10", "f11", "f12" },
        ["MJCHARSD"] = Cat(BuildableHeader, CharacterTail),
        ["MNCHARSD"] = Cat(BuildableHeader, CharacterTail),
        ["MISSNSD"] = new[]
        {
            "id", "f1", "production_family", "next_production_family", "family", "word5", "is_alliance", "is_empire",
            "special_force_mask", "f9", "timer_min", "timer_spread", "repeats", "hidden",
            // word 14: the check runs at all (0x520b80); word 15: a member may resign, = Foilable and the end-on-foil gate (0x520b90, 0x5346b0). B2d §2, §5, §6.
            "guard_checks", "can_resign", "f16", "f17", "f18", "f19", "f20", "f21", "f22", "f23", "f24", "f25", "f26", "f27",
        },
    };

    /// <summary>The entry kind of every table file, from the sheet's entry-kind table. A file whose header names another kind fails. rule: 02-gdata-formats §2.</summary>
    public static readonly IReadOnlyDictionary<string, string> TableKinds = BuildTableKinds();

    private static IReadOnlyDictionary<string, string> BuildTableKinds()
    {
        var d = new SortedDictionary<string, string>(StringComparer.Ordinal);
        foreach (var f in new[]
        {
            "DIPLMSTB", "ESPIMSTB", "SBTGMSTB", "RESCMSTB", "DSSBMSTB", "RCRTMSTB", "ABDCMSTB", "ASSNMSTB", "INCTMSTB", "SUBDMSTB",
            "FOILTB", "FDECOYTB", "TDECOYTB", "ESCAPETB", "INFORMTB", "RLEVADTB", "CSCRHTTB", "RESRCTB", "UPRIS1TB", "UPRIS2TB",
        }) d[f] = EntryKinds.Int;
        d["GNPRTB"] = EntryKinds.GeneralParam;
        d["SDPRTB"] = EntryKinds.SideParam;
        d["SYFCCRTB"] = EntryKinds.Seed;
        d["SYFCRMTB"] = EntryKinds.Seed;
        foreach (var f in new[] { "CMUNAFTB", "CMUNALTB", "CMUNCRTB", "CMUNEFTB", "CMUNEMTB", "CMUNHQTB", "CMUNYVTB", "FACLCRTB", "FACLHQTB" })
            d[f] = EntryKinds.SeedFamily;
        return d;
    }

    /// <summary>Head names of every table entry: word 0 is the index, word 1 is 1 in every entry. rule: 02-gdata-formats §2.</summary>
    private static readonly string[] EntryHead = { "index", "f1" };

    /// <summary>Header of a table file of the given kind; <paramref name="width"/> = the widest row (seed family rows vary). rule: 02-gdata-formats §2.</summary>
    public static string[] TableHeader(string kind, int width) => kind switch
    {
        EntryKinds.Int => Cat(EntryHead, new[] { "threshold", "value" }),
        EntryKinds.GeneralParam => Cat(EntryHead, new[] { "key" }, Numbered("v", 0, width - 3)),
        EntryKinds.SideParam => Cat(EntryHead, new[] { "key" }, Numbered("v", 0, width - 3)),
        EntryKinds.Seed => Cat(EntryHead, new[] { "key", "object" }),
        EntryKinds.SeedFamily => Cat(EntryHead, new[] { "key", "f3", "f4", "n" }, Numbered("object", 1, width - GData.SeedFamilyHeadWords)),
        _ => throw new ImportException($"no header for entry kind '{kind}'."),
    };

    /// <summary>The header of a file: section 5 names, or the table layout, or <c>w0..wN</c>; it must cover exactly <paramref name="width"/> words.</summary>
    public static string[] Header(string file, string? entryKind, int width)
    {
        string[] h = entryKind != null ? TableHeader(entryKind, width)
            : Sd.TryGetValue(file, out var named) ? named : Numbered("w", 0, width);
        if (h.Length != width) throw new ImportException($"{file}: records of {width} words, the sheet names {h.Length}.");
        return h;
    }

    private static string[] Numbered(string prefix, int first, int count) =>
        Enumerable.Range(first, Math.Max(0, count)).Select(i => prefix + i).ToArray();
}
