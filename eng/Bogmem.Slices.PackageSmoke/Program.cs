using Bogmem.Slices.Embedding;
using Bogmem.Slices.Runtime;

var root = Directory.CreateTempSubdirectory("bogmem-slices-package-").FullName;
try
{
    using var palace = PalaceRuntime.Open(
        Path.Combine(root, "palace"),
        "package-smoke",
        new LexicalHashEmbedder());

    var added = palace.Memory.Add(
        "release",
        "smoke",
        "The embedded BogMem package opens a palace and recalls durable memory.",
        addedBy: "package-smoke");
    var hits = palace.Memory.Search("embedded palace durable memory", wing: "release");

    if (!added.Created || hits.All(hit => hit.Drawer.Id != added.Drawer.Id))
        throw new InvalidOperationException(
            "BogMem.Slices package did not complete the embedded memory lifecycle.");

    Console.WriteLine(
        $"BogMem.Slices package smoke passed: {palace.Manifest.PalaceId}, " +
        $"{hits.Count} hit(s).");
}
finally
{
    if (Directory.Exists(root))
        Directory.Delete(root, recursive: true);
}
