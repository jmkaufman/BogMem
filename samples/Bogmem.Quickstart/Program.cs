using Bogmem.Slices.Mining;
using Bogmem.Slices.Storage;
using Bogmem.Slices.Sync;

var demoRoot = Directory.CreateTempSubdirectory("bogmem-quickstart-").FullName;
var project = Path.Combine(demoRoot, "example-project");
var palace = Path.Combine(demoRoot, "palace");
Directory.CreateDirectory(project);

var architecture = Path.Combine(project, "architecture.md");
var retiredPlan = Path.Combine(project, "retired-plan.md");
File.WriteAllText(architecture, LongNote(
    "The service keeps durable local memory in BogDB and exposes retrieval over MCP."));
File.WriteAllText(retiredPlan, LongNote(
    "The retired plan proposed a remote-only memory service."));

try
{
    using var store = new BogDbMemoryStore(palace);

    Console.WriteLine("1. Mine project documents");
    var mine = new ProjectMiner(store).Mine(new ProjectMineRequest(
        project,
        Wing: "quickstart",
        Agent: "sample"));
    Console.WriteLine($"   {mine.FilesProcessed} files, {mine.DrawersWritten} drawers written");

    Console.WriteLine("2. Add an application-owned memory directly");
    var manual = store.Add(
        "quickstart",
        "decisions",
        "Decision: keep memory local by default and expose it through explicit integration boundaries.",
        addedBy: "sample-app");
    Console.WriteLine($"   {manual.Drawer.Id} ({manual.Drawer.Origin})");

    Console.WriteLine("3. Search");
    foreach (var hit in store.Search("durable local memory MCP", wing: "quickstart"))
        Console.WriteLine($"   {hit.Score:F3}  {Path.GetFileName(hit.Drawer.SourceFile),-20} {Preview(hit.Drawer.Content)}");

    Console.WriteLine("4. Delete a source and preview sync");
    File.Delete(retiredPlan);
    var sync = new ProjectSync(store);
    var preview = sync.Run(new ProjectSyncRequest(project, Wing: "quickstart"));
    Console.WriteLine($"   dry-run={preview.DryRun}, missing={preview.Missing}, removed={preview.RemovedDrawers}");
    foreach (var candidate in preview.Candidates)
        Console.WriteLine($"   would remove {candidate.Drawers} drawer(s): {candidate.Reason} {candidate.SourceFile}");

    Console.WriteLine("5. Apply the exact scoped cleanup");
    var applied = sync.Run(new ProjectSyncRequest(project, Wing: "quickstart", Apply: true));
    Console.WriteLine($"   removed={applied.RemovedDrawers}, remaining={store.Status().Drawers}");
    Console.WriteLine($"   manual memory survived={store.Get(manual.Drawer.Id) is not null}");
}
finally
{
    if (Directory.Exists(demoRoot)) Directory.Delete(demoRoot, recursive: true);
}

static string LongNote(string sentence) => string.Join("\n\n", Enumerable.Repeat(sentence, 30));

static string Preview(string content) =>
    content.Length <= 72 ? content : content[..69] + "...";
