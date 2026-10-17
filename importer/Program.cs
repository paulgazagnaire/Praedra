using Rebellion.Importer;

// rule: B1-own-importer §Work 1. Usage:
//   dotnet run --project importer -- <game folder> [<out folder>, default data/original]      stage 1 then stage 2
//   dotnet run --project importer -- --from-raw <raw folder> [<out folder>, default data/original]   stage 2 only
//   dotnet run --project importer -- --from-golden <golden folder> [<out folder>, default data/original]
//        the reference reader's headerless CSVs (specs/reverse/golden) plus our headers as raw/, then stage 2
const string Usage = "usage: Rebellion.Importer <game folder> [<out folder>]\n" +
                     "       Rebellion.Importer --from-raw <raw folder> [<out folder>]\n" +
                     "       Rebellion.Importer --from-golden <golden folder> [<out folder>]";
try
{
    if (args.Length == 0 || args[0] is "-h" or "--help") { Console.WriteLine(Usage); return args.Length == 0 ? 2 : 0; }
    bool fromRaw = args[0] == "--from-raw", fromGolden = args[0] == "--from-golden";
    var rest = fromRaw || fromGolden ? args.Skip(1).ToArray() : args;
    if (rest.Length is < 1 or > 2) { Console.Error.WriteLine(Usage); return 2; }
    string outDir = rest.Length == 2 ? rest[1] : Importer.DefaultOutDir();
    if (fromRaw) Importer.FromRaw(rest[0], outDir);
    else if (fromGolden) Importer.FromGolden(rest[0], outDir);
    else Importer.FromGame(rest[0], outDir);
    Console.WriteLine($"wrote {outDir}");
    return 0;
}
catch (ImportException e)
{
    Console.Error.WriteLine($"import failed: {e.Message}");
    return 1;
}
