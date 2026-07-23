namespace Bogmem.MoleculeApi;

public sealed record MoleculeRecord(
    string Symbol,
    string MoleculeType,
    string Organism,
    IReadOnlyList<string> Functions,
    string Summary,
    string SourceUrl);

public sealed record MoleculeHit(MoleculeRecord Molecule, double Score);
