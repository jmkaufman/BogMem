using Bogmem.Slices.Mining;
using Bogmem.Slices.Storage;

namespace Bogmem.Slices.Tests.Mining;

public static class ProjectConfigTests
{
    public static void Run()
    {
        RoutesConfiguredProject();
        RejectsDuplicateRooms();
    }

    private static void RoutesConfiguredProject()
    {
        var scratch = Directory.CreateTempSubdirectory("bogmem-project-config-").FullName;
        var palace = Directory.CreateTempSubdirectory("bogmem-project-config-palace-").FullName;
        try
        {
            File.WriteAllText(
                Path.Combine(scratch, "mempalace.yaml"),
                """
                wing: configured_project
                rooms:
                  - name: architecture
                    description: Design records
                    keywords: [docs, design]
                  - name: backend
                    keywords: [api, database]
                  - name: research
                    keywords: [view]
                  - name: general
                    keywords: []
                """);
            Directory.CreateDirectory(Path.Combine(scratch, "docs"));
            Directory.CreateDirectory(Path.Combine(scratch, "interviews"));
            File.WriteAllText(
                Path.Combine(scratch, "docs", "decision.md"),
                LongText("The system boundary is recorded here."));
            File.WriteAllText(
                Path.Combine(scratch, "service-api.cs"),
                LongText("This source intentionally has no classification words."));
            File.WriteAllText(
                Path.Combine(scratch, "storage.txt"),
                LongText("The database database layer owns persistence."));
            File.WriteAllText(
                Path.Combine(scratch, "interviews", "notes.txt"),
                LongText("These neutral notes should use the fallback."));

            var configuration = ProjectConfig.Load(scratch);
            TestSupport.AssertEqual("configured_project", configuration.Wing, "YAML wing should load");
            TestSupport.AssertTrue(configuration.ConfigurationPath?.EndsWith("mempalace.yaml", StringComparison.Ordinal) == true,
                "configuration path should identify the selected file");

            using var store = new BogDbMemoryStore(palace);
            var result = new ProjectMiner(store).Mine(new ProjectMineRequest(scratch, Agent: "test"));
            TestSupport.AssertEqual("auto", result.Room, "missing room override should enable routing");
            TestSupport.AssertTrue(result.FilesDiscovered == 4, "configuration file should not be mined");
            TestSupport.AssertTrue(result.FilesByRoom.GetValueOrDefault("architecture") == 1, "folder keyword should route first");
            TestSupport.AssertTrue(result.FilesByRoom.GetValueOrDefault("backend") == 2, "filename and content keywords should route");
            TestSupport.AssertTrue(result.FilesByRoom.GetValueOrDefault("general") == 1,
                "partial path token must not route interviews to view");
            TestSupport.AssertTrue(store.List(wing: "configured_project", room: "architecture", limit: 100).Count > 0,
                "configured wing and routed room should reach storage");

            var overridden = new ProjectMiner(store).Mine(new ProjectMineRequest(
                scratch,
                Wing: "override_wing",
                Room: "single_room",
                Agent: "test"));
            TestSupport.AssertEqual("single_room", overridden.Room, "explicit room should be reported");
            TestSupport.AssertTrue(
                overridden.FilesByRoom.Count == 1 && overridden.FilesByRoom.GetValueOrDefault("single_room") == 4,
                "explicit room should override routing");
            TestSupport.AssertTrue(store.List(wing: "override_wing", room: "single_room", limit: 100).Count > 0,
                "explicit wing and room should reach storage");
        }
        finally
        {
            if (Directory.Exists(scratch)) Directory.Delete(scratch, recursive: true);
            if (Directory.Exists(palace)) Directory.Delete(palace, recursive: true);
        }
    }

    private static void RejectsDuplicateRooms()
    {
        var scratch = Directory.CreateTempSubdirectory("bogmem-project-config-invalid-").FullName;
        try
        {
            File.WriteAllText(
                Path.Combine(scratch, "mempalace.yaml"),
                """
                wing: example
                rooms:
                  - name: backend
                  - name: BACKEND
                """);
            TestSupport.AssertThrows<InvalidDataException>(
                () => ProjectConfig.Load(scratch),
                "duplicate room names should fail configuration loading");
        }
        finally
        {
            if (Directory.Exists(scratch)) Directory.Delete(scratch, recursive: true);
        }
    }

    private static string LongText(string sentence) => string.Join("\n\n", Enumerable.Repeat(sentence, 60));
}
