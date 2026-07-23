namespace Bogmem.MoleculeApi;

public static class MoleculeCatalog
{
    public static IReadOnlyList<MoleculeRecord> HumanDnaDamageProteins { get; } =
    [
        new(
            "BRCA1",
            "protein",
            "Homo sapiens",
            ["DNA repair", "double-strand break repair", "homologous recombination", "genome stability"],
            "A tumor-suppressor protein involved in double-strand break repair, recombination, and genomic stability.",
            "https://www.ncbi.nlm.nih.gov/gene/672"),
        new(
            "RAD51",
            "protein",
            "Homo sapiens",
            ["DNA repair", "homologous recombination", "homologous pairing", "DNA strand transfer"],
            "A recombinase that participates in homologous DNA pairing and strand transfer during repair.",
            "https://www.ncbi.nlm.nih.gov/gene/5888"),
        new(
            "ATM",
            "protein kinase",
            "Homo sapiens",
            ["DNA damage response", "cell cycle checkpoint signaling", "DNA repair", "genome stability"],
            "A checkpoint kinase that coordinates downstream responses to DNA damage and supports genome stability.",
            "https://www.ncbi.nlm.nih.gov/gene/472"),
        new(
            "PARP1",
            "enzyme",
            "Homo sapiens",
            ["DNA damage recovery", "ADP-ribosylation", "chromatin regulation", "damaged DNA binding"],
            "A chromatin-associated ADP-ribosyltransferase involved in molecular events during recovery from DNA damage.",
            "https://www.ncbi.nlm.nih.gov/gene/142"),
    ];
}
