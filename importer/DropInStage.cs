using System.Globalization;

namespace Praedra.Importer;

/// <summary>
/// Stage 2: the raw CSVs to the drop-in tables of <c>data/classic/</c>, with the same file names and columns the core read from data/interim,
/// so the loaders only change folder. Every number comes from a record word (sheet section 5 maps each column); the text columns the files
/// do not have (side, kind, research_family, start_*, source) follow the interim's derivation rules, written here. Rows that are not in the
/// game files live in data/design/ (never written by the importer). rule: B1-own-importer §Work 3.
/// </summary>
public static class DropInStage
{
    // ---- word 5 packing. rule: 02-gdata-formats §1 ("low 16 bits text_id; high 16 bits 2 in every record")
    public const int TextIdBits = 16;
    public const long TextIdMask = 0xFFFF;

    // ---- production families of the buildable files: the research track. rule: economy/14 §2.5 (0x28 ship, 0x29 troop, 0x2a facility)
    public const int ShipFamily = 0x28, TroopFamily = 0x29, FacilityFamily = 0x2a;

    /// <summary>The decoy table: FDECOYTB and TDECOYTB must be identical (the rule book treats them as one), written under the first name. rule: B1-own-importer §Work 1.</summary>
    public const string DecoyTable = "FDECOYTB", DecoyTwin = "TDECOYTB";
    public const string FoilTable = "FOILTB";
    /// <summary>The escape-duel table (B2d §5: chance that a spotted member escapes, x = member combat - commander combat). rule: m5-missions §4.</summary>
    public const string EvadeTable = "RLEVADTB";
    /// <summary>The prisoner escape table (B2e §3, x = combat + espionage - captors' average combat - captors' special forces). rule: m5-characters-and-force §Escape.</summary>
    public const string EscapeTable = "ESCAPETB";

    /// <summary>The success tables in the interim's order. rule: m5-missions §Mission tables.</summary>
    public static readonly string[] SuccessTables =
        { "DIPLMSTB", "ESPIMSTB", "SBTGMSTB", "RESCMSTB", "DSSBMSTB", "RCRTMSTB", "INCTMSTB", "SUBDMSTB", "ABDCMSTB", "ASSNMSTB" };

    /// <summary>
    /// MISSNSD record id to our mission kind name and its success table (our names, not in the files: the interim's mapping, missions/01).
    /// rule: m5-missions §Mission records (data).
    /// </summary>
    private static readonly SortedDictionary<int, (string Kind, string Table)> MissionKinds = new()
    {
        [0x01] = ("Move", ""), [0x02] = ("Return", ""), [0x03] = ("Autorouting", ""), [0x04] = ("Adrift", ""),
        [0x10] = ("Diplomacy", "DIPLMSTB"), [0x11] = ("Rescue", "RESCMSTB"), [0x12] = ("Sabotage", "SBTGMSTB"), [0x13] = ("Espionage", "ESPIMSTB"),
        [0x15] = ("Reconnaissance", ""), [0x16] = ("Recruitment", "RCRTMSTB"), [0x17] = ("Abduction", "ABDCMSTB"),
        [0x20] = ("ShipResearch", ""), [0x21] = ("FacilityResearch", ""), [0x22] = ("TroopResearch", ""),
        [0x40] = ("InciteUprising", "INCTMSTB"), [0x41] = ("DeathStarSabotage", "DSSBMSTB"), [0x42] = ("JediTraining", ""),
        [0x43] = ("Dagobah", ""), [0x44] = ("Palace", ""), [0x45] = ("Vacation", ""), [0x46] = ("Sabbatical", ""),
        [0x80] = ("SubdueUprising", "SUBDMSTB"), [0x81] = ("Assassination", "ASSNMSTB"), [0x82] = ("Pickup", ""), [0x83] = ("Bounty", ""),
    };

    /// <summary>
    /// The rows of mission-numbers.csv that are one GNPRTB value: (key, GNPRTB key, the reading's tag). The other rows (RollDie, the diplomacy
    /// and subdue ranges, HanCharacterId, ...) are not a file value and live in data/design/mission-numbers.csv. rule: m5-missions §Numbers.
    /// </summary>
    private static readonly (string Key, int Gnprtb, string Reading)[] MissionNumberKeys =
    {
        ("DetectionConstant", 3584, "Traced: subtracted, so +1; missions/08"),
        ("DecoyOfficerPercent", 3588, "Traced: missions/08"),
        ("DetectionOfficerPercent", 3589, "Traced: missions/08"),
        ("InciteSupport", 6145, "Lead: economy/05, reading unverified"),
        ("AgentLeadershipDivisor", 6144, "Lead: incident score incite and subdue terms"),
        ("ResearchSkillGain", 6158, "Traced: economy/14, m5-research"),
        ("GroupSpeed", 1, "Traced: characters and mission groups use 100; m1-travel"),
        ("HanMissionSpeed", 3083, "Traced: planets/11, heroes/mission-speed"),
    };

    /// <summary>DEFFACSD family to the defense kind (the interim's derivation, m3-planetary-defenses §The six classes).</summary>
    private static string DefenseKind(int family, string where) => family switch
    {
        0x22 or 0x23 => "battery",
        0x24 => "shield generator",
        0x25 => "death star shield",
        _ => throw new ImportException($"{where}: no defense kind for family {family}."),
    };

    public static readonly string[] Outputs =
    {
        "capital-ships.csv", "fighters.csv", "troop-regiments.csv", "special-forces.csv", "defense-facilities.csv", "characters.csv",
        "unit-and-facility-costs.csv", "research-unlocks.csv", "mission-records.csv", "mission-tables.csv", "mission-numbers.csv",
        "bombardment-defense.csv", "gnprtb.csv", "sdprtb.csv",
    };

    /// <summary>Build every drop-in table from a folder of raw CSVs. Returns (file name, CSV text) in <see cref="Outputs"/> order.</summary>
    public static List<(string File, string Text)> Build(string rawDir)
    {
        var raw = new Raw(rawDir);
        var names = PraedraNames.Load();
        return new List<(string File, string Text)>
        {
            ("capital-ships.csv", Ships(raw, "CAPSHPSD", "f49", "field51")),
            ("fighters.csv", Ships(raw, "FIGHTSD", null, null)),
            ("troop-regiments.csv", Regiments(raw)),
            ("special-forces.csv", SpecialForces(raw)),
            ("defense-facilities.csv", Defenses(raw)),
            ("characters.csv", Characters(raw, PraedraNames.LoadRoster())),
            ("unit-and-facility-costs.csv", Costs(raw)),
            ("research-unlocks.csv", Research(raw)),
            ("mission-records.csv", MissionRecords(raw)),
            ("mission-tables.csv", MissionTables(raw)),
            ("mission-numbers.csv", MissionNumbers(raw)),
            ("bombardment-defense.csv", Bombardment(raw)),
            ("gnprtb.csv", Params(raw, "GNPRTB")),
            ("sdprtb.csv", Params(raw, "SDPRTB")),
        }.Select(t => (t.File, PraedraNames.Rename(t.File, t.Text, names))).ToList();
    }

    public static void Write(string rawDir, string outDir)
    {
        var built = Build(rawDir);
        Directory.CreateDirectory(outDir);
        foreach (var (file, text) in built) File.WriteAllText(Path.Combine(outDir, file), text, new System.Text.UTF8Encoding(false));
    }

    // ------------------------------------------------------------------ tables

    /// <summary>capital-ships.csv / fighters.csv: name, id, the two production families, field7 (word 5 high half), then words 6.. in order. rule: 02-gdata-formats §5.</summary>
    private static string Ships(Raw raw, string file, string? lastRaw, string? lastOut)
    {
        var t = raw[file];
        var header = new List<string> { "name", "id", "production_family", "next_production_family", "field7" };
        var cols = new List<string>();
        foreach (var c in t.Header.Skip(t.Col("is_alliance")))
        {
            string o = c switch { "cost" => "refined_material_cost", "maintenance" => "maintenance_cost", _ => c == lastRaw ? lastOut! : c };
            header.Add(o); cols.Add(c);
        }
        var rows = Rows(t, r =>
        {
            var row = new List<string> { raw.Name(t, r), S(t, r, "id"), S(t, r, "production_family"), S(t, r, "next_production_family"), High(t, r) };
            row.AddRange(cols.Select(c => S(t, r, c)));
            return row;
        });
        return Csv.Write(header, rows);
    }

    private static string Regiments(Raw raw)
    {
        var t = raw["TROOPSD"];
        return Csv.Write(new[] { "id", "name", "side", "production_family", "cost", "maintenance", "research_order", "research_difficulty", "attack", "defense", "bombardment_defense", "detection", "uprising_defense" },
            Rows(t, r => new[] { S(t, r, "id"), raw.Name(t, r), OneSide(t, r), S(t, r, "production_family"), S(t, r, "cost"), S(t, r, "maintenance"), S(t, r, "research_order"),
                S(t, r, "research_difficulty"), S(t, r, "attack"), S(t, r, "defense"), S(t, r, "bombardment_defense"), S(t, r, "detection"), S(t, r, "uprising_defense") }));
    }

    private static string SpecialForces(Raw raw)
    {
        var t = raw["SPECFCSD"];
        return Csv.Write(new[] { "id", "name", "side", "production_family", "cost", "maintenance", "espionage", "combat", "leadership", "espionage_variance", "combat_variance", "leadership_variance", "loyalty_base", "mission_bit", "research_order" },
            Rows(t, r => new[] { S(t, r, "id"), raw.Name(t, r), OneSide(t, r), S(t, r, "production_family"), S(t, r, "cost"), S(t, r, "maintenance"), S(t, r, "espionage"),
                S(t, r, "combat"), S(t, r, "leadership"), S(t, r, "espionage_variance"), S(t, r, "combat_variance"), S(t, r, "leadership_variance"), S(t, r, "loyalty_base"),
                "0x" + t.Long(r, "mission_bit").ToString("X3", CultureInfo.InvariantCulture), S(t, r, "research_order") }));
    }

    private static string Defenses(Raw raw)
    {
        var t = raw["DEFFACSD"];
        return Csv.Write(new[] { "id", "name", "family", "cost", "maint", "research_order", "research_difficulty", "attack", "shield", "bombard_defense", "side", "kind" },
            Rows(t, r => new[] { S(t, r, "id"), raw.Name(t, r), S(t, r, "family"), S(t, r, "cost"), S(t, r, "maintenance"), S(t, r, "research_order"), S(t, r, "research_difficulty"),
                S(t, r, "attack"), S(t, r, "shield"), S(t, r, "bombard_defense"), BothSides(t, r), DefenseKind(t.Int(r, "family"), $"DEFFACSD row {r + 1}") }));
    }

    /// <summary>characters.csv: kind (major = MJCHARSD, minor = MNCHARSD), name, then words 0..36 with word 5 split. rule: 02-gdata-formats §5.</summary>
    private static string Characters(Raw raw, IReadOnlySet<int> roster)
    {
        var header = new[] { "kind", "name", "id", "field2", "pf", "npf", "family", "textid", "f7", "alliance", "empire", "cost", "maint", "rorder", "rdiff",
            "dip", "dipv", "esp", "espv", "ship", "shipv", "troop", "troopv", "fac", "facv", "com", "comv", "lead", "leadv", "loy", "loyv",
            "jprob", "knownjedi", "jlvl", "jlvlv", "admiral", "commander", "general", "nobetray", "trainer" };
        var rows = new List<IEnumerable<string>>();
        foreach (var (file, kind) in new[] { ("MJCHARSD", "major"), ("MNCHARSD", "minor") })
        {
            var t = raw[file];
            rows.AddRange(Rows(t, r =>
            {
                if (!roster.Contains(t.Int(r, "id"))) return null!; // a cut character (not in roster.csv): left out
                var row = new List<string> { kind, raw.Name(t, r), S(t, r, "id"), S(t, r, "f1"), S(t, r, "production_family"), S(t, r, "next_production_family"),
                    S(t, r, "family"), Low(t, r), High(t, r) };
                row.AddRange(t.Header.Skip(t.Col("is_alliance")).Select(c => S(t, r, c)));
                return row;
            }).Where(row => row != null));
        }
        return Csv.Write(header, rows);
    }

    /// <summary>The buildable classes in the interim's catalogue order: (kind, file).</summary>
    private static readonly (string Kind, string File)[] Buildable =
    {
        ("capital ship", "CAPSHPSD"), ("fighter", "FIGHTSD"), ("troop", "TROOPSD"), ("special force", "SPECFCSD"),
        ("facility", "MANFACSD"), ("facility", "PROFACSD"), ("defense", "DEFFACSD"),
    };

    private static string Costs(Raw raw)
    {
        var rows = new List<IEnumerable<string>>();
        foreach (var (kind, file) in Buildable)
        {
            var t = raw[file];
            rows.AddRange(Rows(t, r => new[] { kind, S(t, r, "id"), raw.Name(t, r), S(t, r, "is_alliance"), S(t, r, "is_empire"), S(t, r, "cost"), S(t, r, "maintenance"),
                S(t, r, "research_order"), S(t, r, "research_difficulty") }));
        }
        return Csv.Write(new[] { "kind", "id", "name", "alliance", "empire", "cost", "maint", "rorder", "rdiff" }, rows);
    }

    /// <summary>
    /// research-unlocks.csv. research_family from production_family; start_*: n/a when the side cannot have the class or the class costs 0
    /// (never listed: economy/14 §3, Bounty Hunters), else yes at level 0 and locked above. rule: m5-research §Ladders; economy/14.
    /// </summary>
    private static string Research(Raw raw)
    {
        var rows = new List<IEnumerable<string>>();
        foreach (var (kind, file) in Buildable)
        {
            var t = raw[file];
            rows.AddRange(Rows(t, r =>
            {
                int pf = t.Int(r, "production_family"), level = t.Int(r, "research_order"), cost = t.Int(r, "cost");
                string track = pf switch
                {
                    ShipFamily => "ship", TroopFamily => "troop", FacilityFamily => "facility",
                    _ => throw new ImportException($"{file} row {r + 1}: production family {pf} is not a research track."),
                };
                string Start(string sideCol) => t.Int(r, sideCol) == 0 || cost == 0 ? "n/a" : level == 0 ? "yes" : "locked";
                return new[] { $"0x{pf:x2} {track}", kind, S(t, r, "id"), raw.Name(t, r), S(t, r, "is_alliance"), S(t, r, "is_empire"), S(t, r, "cost"),
                    S(t, r, "research_order"), S(t, r, "research_difficulty"), Start("is_alliance"), Start("is_empire") };
            }));
        }
        return Csv.Write(new[] { "research_family", "kind", "dat_id", "name", "alliance", "empire", "cost", "level", "rdiff", "start_alliance", "start_empire" }, rows);
    }

    private static string MissionRecords(Raw raw)
    {
        var t = raw["MISSNSD"];
        return Csv.Write(new[] { "dat_id", "kind", "alliance", "empire", "timer_min", "timer_spread", "hidden", "repeats", "table", "special_force_mask", "guard_checks", "can_resign", "source" },
            Rows(t, r =>
            {
                int id = t.Int(r, "id");
                if (!MissionKinds.TryGetValue(id, out var k)) throw new ImportException($"MISSNSD row {r + 1}: record id {id} has no mission kind.");
                return new[] { $"0x{id:X2}", k.Kind, S(t, r, "is_alliance"), S(t, r, "is_empire"), S(t, r, "timer_min"), S(t, r, "timer_spread"), S(t, r, "hidden"),
                    S(t, r, "repeats"), k.Table, "0x" + t.Long(r, "special_force_mask").ToString("X4", CultureInfo.InvariantCulture), S(t, r, "guard_checks"), S(t, r, "can_resign"),
                    $"classic: MISSNSD record {id} (importer); kind and table: missions/01" };
            }));
    }

    /// <summary>mission-tables.csv: every row of FOILTB, the ten success tables, the decoy table, RLEVADTB (B2d) and ESCAPETB (B2e). rule: m5-missions §Mission tables.</summary>
    private static string MissionTables(Raw raw)
    {
        var f = raw[DecoyTable]; var twin = raw[DecoyTwin];
        if (f.Rows.Count != twin.Rows.Count || f.Rows.Zip(twin.Rows).Any(p => !p.First.SequenceEqual(p.Second)))
            throw new ImportException($"{DecoyTable} and {DecoyTwin} differ: the rule book treats them as one table.");
        var rows = new List<IEnumerable<string>>();
        foreach (var name in new[] { FoilTable }.Concat(SuccessTables).Append(DecoyTable).Append(EvadeTable).Append(EscapeTable))
        {
            var t = raw[name];
            rows.AddRange(Rows(t, r => new[] { name, S(t, r, "threshold"), S(t, r, "value"), $"classic: {name}.DAT entry {S(t, r, "index")} (importer)" }));
        }
        return Csv.Write(new[] { "table", "threshold", "value", "source" }, rows);
    }

    private static string MissionNumbers(Raw raw)
    {
        var g = raw.Params("GNPRTB");
        return Csv.Write(new[] { "key", "value", "source" },
            MissionNumberKeys.Select(k => new[] { k.Key, g(k.Gnprtb), $"classic: GNPRTB {k.Gnprtb} (importer); {k.Reading}" }));
    }

    /// <summary>
    /// bombardment-defense.csv: the regiment rows (TROOPSD bombardment_defense), then the manufacturing and production facilities (MANFACSD and
    /// PROFACSD word 12, raw column f12) and the Rebel HQ (ALLFACSD word 12), read as the bombardment defence by the Classic strike (class word 12,
    /// vtable +0x1c0). Only the Sensor Station (not a Classic class) stays in data/design. rule: m3-bombardment B7; B7-bombardment §3, §7 (C8).
    /// </summary>
    private static string Bombardment(Raw raw)
    {
        var t = raw["TROOPSD"];
        var rows = new List<IEnumerable<string>>(
            Rows(t, r => new[] { "troop", raw.Name(t, r), S(t, r, "bombardment_defense"), "classic: TROOPSD bombardment_defense (importer); m3-bombardment B7" }));
        foreach (var file in new[] { "MANFACSD", "PROFACSD" })
        {
            var f = raw[file];
            rows.AddRange(Rows(f, r => new[] { "facility", raw.Name(f, r), S(f, r, "f12"), $"classic: {file} word 12 (importer); B7-bombardment §7" }));
        }
        var hq = raw["ALLFACSD"];
        rows.AddRange(Rows(hq, r => new[] { "hq", raw.Name(hq, r), S(hq, r, "f12"), "classic: ALLFACSD word 12 (importer); B7-bombardment §3 and §7 (read only with HqBombardable)" }));
        return Csv.Write(new[] { "kind", "name", "bombard_defense", "tag" }, rows);
    }

    /// <summary>gnprtb.csv (key, v0..v7) and sdprtb.csv (key, v0..v15): every entry, file order. rule: 02-gdata-formats §2.</summary>
    private static string Params(Raw raw, string file)
    {
        var t = raw[file];
        var vs = t.Header.Where(h => h.StartsWith('v')).ToList();
        return Csv.Write(new[] { "key" }.Concat(vs), Rows(t, r => new[] { S(t, r, "key") }.Concat(vs.Select(v => S(t, r, v)))));
    }

    // ------------------------------------------------------------------ helpers

    private static IEnumerable<IEnumerable<string>> Rows(CsvFile t, Func<int, IEnumerable<string>> row) =>
        Enumerable.Range(0, t.Rows.Count).Select(row).ToList();

    private static string S(CsvFile t, int r, string col) => t.Long(r, col).ToString(CultureInfo.InvariantCulture);
    private static string Low(CsvFile t, int r) => (t.Long(r, "word5") & TextIdMask).ToString(CultureInfo.InvariantCulture);
    private static string High(CsvFile t, int r) => (t.Long(r, "word5") >> TextIdBits).ToString(CultureInfo.InvariantCulture);

    private static string OneSide(CsvFile t, int r) => (t.Int(r, "is_alliance"), t.Int(r, "is_empire")) switch
    {
        (1, 0) => "Alliance",
        (0, 1) => "Empire",
        var x => throw new ImportException($"{t.Name} row {r + 1}: is_alliance, is_empire = {x}, expected one side."),
    };

    private static string BothSides(CsvFile t, int r) => (t.Int(r, "is_alliance"), t.Int(r, "is_empire")) switch
    {
        (1, 1) => "both",
        (0, 1) => "empire",
        (1, 0) => "alliance",
        var x => throw new ImportException($"{t.Name} row {r + 1}: is_alliance, is_empire = {x}, no side."),
    };

    /// <summary>The raw folder: tables loaded on first use, and the record names from TEXTSTRA-strings.csv.</summary>
    private sealed class Raw
    {
        private readonly string _dir;
        private readonly SortedDictionary<string, CsvFile> _files = new(StringComparer.Ordinal);
        private SortedDictionary<int, string>? _strings;

        public Raw(string dir)
        {
            if (!Directory.Exists(dir)) throw new ImportException($"{dir}: raw folder not found.");
            _dir = dir;
        }

        public CsvFile this[string file]
        {
            get
            {
                if (_files.TryGetValue(file, out var f)) return f;
                string p = Path.Combine(_dir, file + ".csv");
                if (!File.Exists(p)) throw new ImportException($"{p}: raw file not found.");
                return _files[file] = Csv.Load(p);
            }
        }

        /// <summary>A record's name: strings[text_id], text_id = low 16 bits of word 5. rule: 02-gdata-formats §1, §3.</summary>
        public string Name(CsvFile t, int r)
        {
            if (_strings == null)
            {
                var s = this[RawStage.StringsCsv];
                _strings = new SortedDictionary<int, string>();
                for (int i = 0; i < s.Rows.Count; i++) _strings[s.Int(i, "id")] = s.Get(i, "text");
            }
            int id = (int)(t.Long(r, "word5") & TextIdMask);
            return _strings.TryGetValue(id, out var n) ? n : throw new ImportException($"{t.Name} row {r + 1}: text id {id} is not in {RawStage.StringsCsv}.");
        }

        /// <summary>A GNPRTB/SDPRTB value by key; all columns must agree (else the reader must pick a column). rule: 02-gdata-formats §2.</summary>
        public Func<int, string> Params(string file)
        {
            var t = this[file];
            return key =>
            {
                for (int r = 0; r < t.Rows.Count; r++)
                {
                    if (t.Long(r, "key") != key) continue;
                    var vs = t.Header.Where(h => h.StartsWith('v')).Select(v => t.Long(r, v)).Distinct().ToList();
                    if (vs.Count != 1) throw new ImportException($"{file} key {key}: the columns differ; name the column to read.");
                    return vs[0].ToString(CultureInfo.InvariantCulture);
                }
                throw new ImportException($"{file}: no key {key}.");
            };
        }
    }
}
