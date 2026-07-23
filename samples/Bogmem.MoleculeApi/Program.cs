using Bogmem.MoleculeApi;
using Bogmem.Slices.Storage;

if (args.Contains("--demo", StringComparer.Ordinal))
{
    RunDemo();
    return;
}

var builder = WebApplication.CreateBuilder(args);
var palace = Environment.GetEnvironmentVariable("BOGMEM_MOLECULE_PALACE");
if (string.IsNullOrWhiteSpace(palace))
{
    palace = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
        ".bogmem",
        "molecule-api");
}

builder.Services.AddSingleton<IMemoryStore>(_ => new BogDbMemoryStore(palace));
builder.Services.AddSingleton<MoleculeMemoryService>();

var app = builder.Build();
var memory = app.Services.GetRequiredService<MoleculeMemoryService>();
foreach (var molecule in MoleculeCatalog.HumanDnaDamageProteins)
    memory.Remember(molecule);

app.MapGet("/", () => Results.Ok(new
{
    service = "BogMem molecule capability memory",
    endpoints = new[]
    {
        "GET /molecules/search?q=chromatin recovery",
        "GET /molecules/capabilities?function=DNA repair&function=homologous recombination",
        "GET /functions",
        "PUT /molecules/{symbol}",
    },
}));

app.MapGet("/molecules/search", (string q, int? limit, MoleculeMemoryService service) =>
    Results.Ok(service.Search(q, limit ?? 10)));

app.MapGet("/molecules/capabilities", (HttpRequest request, MoleculeMemoryService service) =>
{
    var functions = request.Query["function"]
        .Where(value => !string.IsNullOrWhiteSpace(value))
        .Select(value => value!)
        .ToArray();
    return functions.Length == 0
        ? Results.BadRequest(new { error = "Supply at least one ?function= value." })
        : Results.Ok(service.FindWithAllFunctions(functions));
});

app.MapGet("/functions", (MoleculeMemoryService service) => Results.Ok(service.KnownFunctions()));

app.MapPut("/molecules/{symbol}", (string symbol, MoleculeRecord molecule, MoleculeMemoryService service) =>
{
    if (!string.Equals(symbol, molecule.Symbol, StringComparison.OrdinalIgnoreCase))
        return Results.BadRequest(new { error = "Route symbol and body symbol must match." });
    var result = service.Remember(molecule);
    return Results.Ok(new { molecule = molecule.Symbol.ToUpperInvariant(), result.Changed, result.Applied });
});

app.Run();

static void RunDemo()
{
    var root = Directory.CreateTempSubdirectory("bogmem-molecule-api-").FullName;
    try
    {
        using var store = new BogDbMemoryStore(Path.Combine(root, "palace"));
        var service = new MoleculeMemoryService(store);
        foreach (var molecule in MoleculeCatalog.HumanDnaDamageProteins)
            service.Remember(molecule);

        Console.WriteLine("Query: molecules with DNA repair AND homologous recombination");
        var repairHits = service.FindWithAllFunctions(["DNA repair", "homologous recombination"]);
        foreach (var hit in repairHits)
            Console.WriteLine($"  {hit.Molecule.Symbol,-6} score={hit.Score:F3}  {hit.Molecule.Summary}");
        EnsureSymbols(repairHits, "BRCA1", "RAD51");

        Console.WriteLine();
        Console.WriteLine("Query: molecules with DNA damage response AND cell cycle checkpoint signaling");
        var checkpointHits = service.FindWithAllFunctions(["DNA damage response", "cell cycle checkpoint signaling"]);
        foreach (var hit in checkpointHits)
            Console.WriteLine($"  {hit.Molecule.Symbol,-6} score={hit.Score:F3}  {hit.Molecule.Summary}");
        EnsureSymbols(checkpointHits, "ATM");

        Console.WriteLine();
        Console.WriteLine("Free-text retrieval: chromatin enzyme involved in recovery from DNA damage");
        foreach (var hit in service.Search("chromatin enzyme recovery from DNA damage", limit: 2))
            Console.WriteLine($"  {hit.Molecule.Symbol,-6} score={hit.Score:F3}  source={hit.Molecule.SourceUrl}");

        Console.WriteLine();
        Console.WriteLine("Semantic retrieval: protein that mends broken genetic material by exchanging strands");
        var semanticHits = service.Search(
            "protein that mends broken genetic material by exchanging strands",
            limit: 2);
        foreach (var hit in semanticHits)
            Console.WriteLine($"  {hit.Molecule.Symbol,-6} score={hit.Score:F3}  {hit.Molecule.Summary}");
        EnsureSymbols(semanticHits, "BRCA1", "RAD51");
    }
    finally
    {
        if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
    }
}

static void EnsureSymbols(IReadOnlyList<MoleculeHit> hits, params string[] expected)
{
    var actual = hits.Select(hit => hit.Molecule.Symbol).OrderBy(value => value, StringComparer.Ordinal).ToArray();
    var wanted = expected.OrderBy(value => value, StringComparer.Ordinal).ToArray();
    if (!actual.SequenceEqual(wanted, StringComparer.Ordinal))
        throw new InvalidOperationException($"Expected [{string.Join(", ", wanted)}], got [{string.Join(", ", actual)}].");
}
