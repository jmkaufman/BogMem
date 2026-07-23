using System.Text.Json;
using Bogmem.Slices.Storage;

namespace Bogmem.MoleculeApi;

public sealed class MoleculeMemoryService(IMemoryStore store)
{
    private const string Wing = "human_biology";
    private const string Room = "molecules";
    private static readonly JsonSerializerOptions Json = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
    };

    public SourceReplaceResult Remember(MoleculeRecord molecule)
    {
        Validate(molecule);
        var normalized = molecule with
        {
            Symbol = molecule.Symbol.Trim().ToUpperInvariant(),
            MoleculeType = molecule.MoleculeType.Trim(),
            Organism = molecule.Organism.Trim(),
            Functions = molecule.Functions
                .Where(value => !string.IsNullOrWhiteSpace(value))
                .Select(value => value.Trim())
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToArray(),
            Summary = molecule.Summary.Trim(),
            SourceUrl = molecule.SourceUrl.Trim(),
        };
        var document = JsonSerializer.Serialize(normalized, Json);
        return store.ReplaceSource(
            Wing,
            Room,
            SourceFor(normalized.Symbol),
            [document],
            addedBy: "molecule-api");
    }

    public IReadOnlyList<MoleculeHit> Search(string query, int limit = 10)
    {
        if (string.IsNullOrWhiteSpace(query))
            throw new ArgumentException("A search query is required.", nameof(query));
        return store.Search(query.Trim(), Math.Clamp(limit, 1, 100), Wing, Room)
            .Select(hit => ReadHit(hit.Drawer.Content, hit.Score))
            .Where(hit => hit is not null)
            .Select(hit => hit!)
            .ToArray();
    }

    public IReadOnlyList<MoleculeHit> FindWithAllFunctions(IReadOnlyCollection<string> functions)
    {
        ArgumentNullException.ThrowIfNull(functions);
        var required = functions
            .Where(value => !string.IsNullOrWhiteSpace(value))
            .Select(value => value.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        if (required.Length == 0)
            throw new ArgumentException("At least one function is required.", nameof(functions));

        return Search(string.Join(' ', required), limit: 100)
            .Where(hit => required.All(requiredFunction =>
                hit.Molecule.Functions.Contains(requiredFunction, StringComparer.OrdinalIgnoreCase)))
            .ToArray();
    }

    public IReadOnlyList<string> KnownFunctions() =>
        store.List(Wing, Room, limit: 100)
            .Select(drawer => Read(drawer.Content))
            .Where(molecule => molecule is not null)
            .SelectMany(molecule => molecule!.Functions)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(value => value, StringComparer.OrdinalIgnoreCase)
            .ToArray();

    private static MoleculeHit? ReadHit(string content, double score)
    {
        var molecule = Read(content);
        return molecule is null ? null : new MoleculeHit(molecule, score);
    }

    private static MoleculeRecord? Read(string content)
    {
        try
        {
            return JsonSerializer.Deserialize<MoleculeRecord>(content, Json);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static string SourceFor(string symbol) => $"molecule://human/{symbol.Trim().ToUpperInvariant()}";

    private static void Validate(MoleculeRecord molecule)
    {
        ArgumentNullException.ThrowIfNull(molecule);
        if (string.IsNullOrWhiteSpace(molecule.Symbol)) throw new ArgumentException("A symbol is required.", nameof(molecule));
        if (string.IsNullOrWhiteSpace(molecule.MoleculeType)) throw new ArgumentException("A molecule type is required.", nameof(molecule));
        if (string.IsNullOrWhiteSpace(molecule.Organism)) throw new ArgumentException("An organism is required.", nameof(molecule));
        if (molecule.Functions is null || molecule.Functions.Count == 0)
            throw new ArgumentException("At least one function is required.", nameof(molecule));
        if (string.IsNullOrWhiteSpace(molecule.Summary)) throw new ArgumentException("A summary is required.", nameof(molecule));
        if (!Uri.TryCreate(molecule.SourceUrl, UriKind.Absolute, out _))
            throw new ArgumentException("An absolute source URL is required.", nameof(molecule));
    }
}
