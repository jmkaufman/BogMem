using BogDb.Core.Main;
using Bogmem.Slices.Storage;

namespace Bogmem.Slices.Tests.Storage;

public static class BogDbMemoryStoreTests
{
    public static void Run()
    {
        var path = Directory.CreateTempSubdirectory("bogmem-bogdb-store-").FullName;
        try
        {
            string firstId;
            using (var store = new BogDbMemoryStore(path))
            {
                var first = store.Add("project", "decisions", "We switched the API from REST to GraphQL for typed clients.", "/notes/decision.md", "test");
                TestSupport.AssertTrue(first.Created, "first add should create a drawer");
                firstId = first.Drawer.Id;
                var duplicate = store.Add("project", "decisions", "We switched the API from REST to GraphQL for typed clients.", "/notes/decision.md", "test");
                TestSupport.AssertTrue(!duplicate.Created && duplicate.Drawer.Id == firstId, "content ID should make add idempotent");

                store.Add("personal", "shopping", "Remember to buy oat milk and coffee beans.", "/notes/list.md", "test");
                var results = store.Search("why GraphQL typed API", limit: 2);
                TestSupport.AssertTrue(results.Count == 2, "search should return both candidates");
                TestSupport.AssertTrue(results[0].Drawer.Id == firstId, "hybrid retrieval should rank the GraphQL drawer first");
                TestSupport.AssertTrue(results[0].Distance < results[1].Distance, "vector baseline should discriminate lexical relevance");

                var status = store.Status();
                TestSupport.AssertTrue(status.Backend == "bogdb" && status.Drawers == 2 && status.Wings == 2, "status should reflect persisted drawers");
            }

            using (var reopened = new BogDbMemoryStore(path))
            {
                TestSupport.AssertTrue(reopened.Get(firstId)?.Content.Contains("GraphQL", StringComparison.Ordinal) == true, "drawer should survive reopen");
                TestSupport.AssertTrue(reopened.List(wing: "project").Count == 1, "wing filter should survive reopen");
                var updated = reopened.Update(firstId, content: "GraphQL remains the typed API contract.", room: "architecture");
                TestSupport.AssertTrue(updated?.Room == "architecture", "update should persist new metadata");
                TestSupport.AssertTrue(reopened.DeleteBySource("/notes/list.md") == 1, "delete-by-source should dry-run by default");
                TestSupport.AssertTrue(reopened.Status().Drawers == 2, "dry-run must not delete");
                TestSupport.AssertTrue(reopened.DeleteBySource("/notes/list.md", dryRun: false) == 1, "delete-by-source should report committed count");
                TestSupport.AssertTrue(reopened.Delete(firstId), "delete should remove existing drawer");
                TestSupport.AssertTrue(reopened.Status().Drawers == 0, "all deletes should persist");
            }

            using var finalOpen = new BogDbMemoryStore(path);
            TestSupport.AssertTrue(finalOpen.Status().Drawers == 0, "deletes should survive reopen");
        }
        finally
        {
            if (Directory.Exists(path)) Directory.Delete(path, recursive: true);
        }

        VerifyOriginMigration();
    }

    private static void VerifyOriginMigration()
    {
        var path = Directory.CreateTempSubdirectory("bogmem-bogdb-origin-migration-").FullName;
        try
        {
            using (var database = BogDatabase.Open(path))
            using (var connection = new BogConnection(database))
            {
                var created = connection.Query(
                    "CREATE NODE TABLE Drawer(" +
                    "id STRING PRIMARY KEY, wing STRING, room STRING, content STRING, " +
                    "source_file STRING, added_by STRING, filed_at STRING, id_recipe STRING, embedding FLOAT[])");
                TestSupport.AssertTrue(created.IsSuccess, "legacy drawer schema fixture should be created");
                connection.Graph().AddNode("Drawer", "legacy_drawer", new Dictionary<string, object>
                {
                    ["id"] = "legacy_drawer",
                    ["wing"] = "legacy",
                    ["room"] = "general",
                    ["content"] = "An existing legacy drawer.",
                    ["source_file"] = "/tmp/legacy.md",
                    ["added_by"] = "legacy",
                    ["filed_at"] = "2026-01-01T00:00:00Z",
                    ["id_recipe"] = "v3",
                    ["embedding"] = new[] { 1.0f, 0.0f },
                }).Commit();
            }

            using var migrated = new BogDbMemoryStore(path);
            TestSupport.AssertTrue(migrated.Get("legacy_drawer")?.Origin == "legacy",
                "existing drawer should be protected as legacy after migration");
            var added = migrated.Add("migration", "manual", "Schema migration keeps older palaces readable.");
            TestSupport.AssertTrue(added.Drawer.Origin == "manual", "new drawer should receive manual ownership after migration");
        }
        finally
        {
            if (Directory.Exists(path)) Directory.Delete(path, recursive: true);
        }
    }
}
