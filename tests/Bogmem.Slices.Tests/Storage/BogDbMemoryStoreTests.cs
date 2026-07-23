using BogDb.Core.Main;
using Bogmem.Slices.Embedding;
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
                TestSupport.AssertTrue(results[0].Bm25Score > 0, "native BogDB FTS should contribute a BM25 score");

                var status = store.Status();
                TestSupport.AssertTrue(status.Backend == "bogdb" && status.Drawers == 2 && status.Wings == 2, "status should reflect persisted drawers");
                TestSupport.AssertTrue(status.RetrievalMode == "bogdb-hnsw-bm25-hybrid", "status should advertise native BogDB retrieval");
            }

            using (var reopened = new BogDbMemoryStore(path))
            {
                TestSupport.AssertTrue(reopened.Get(firstId)?.Content.Contains("GraphQL", StringComparison.Ordinal) == true, "drawer should survive reopen");
                TestSupport.AssertTrue(reopened.List(wing: "project").Count == 1, "wing filter should survive reopen");
                var updated = reopened.Update(firstId, content: "GraphQL remains the typed API contract.", room: "architecture");
                TestSupport.AssertTrue(updated?.Room == "architecture", "update should persist new metadata");
                var updatedSearch = reopened.Search("typed contract", limit: 1);
                TestSupport.AssertTrue(
                    updatedSearch.Count == 1 && updatedSearch[0].Drawer.Id == firstId && updatedSearch[0].Bm25Score > 0,
                    "vector and FTS indexes should reflect committed updates");
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
        VerifyEmbeddingModelMigration();
        VerifyIndexedSourceReplacement();
        VerifyCandidateSearchBeyondOverfetchWindow();
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
            TestSupport.AssertTrue(
                migrated.Get("legacy_drawer")?.Embedding.Count == Bogmem.Slices.Embedding.LexicalHashEmbedder.Dimensions,
                "legacy embeddings should be normalized before HNSW indexing");
            TestSupport.AssertTrue(
                migrated.Search("existing legacy drawer", limit: 1).Single().Drawer.Id == "legacy_drawer",
                "legacy drawers should be searchable through the migrated native indexes");
            var added = migrated.Add("migration", "manual", "Schema migration keeps older palaces readable.");
            TestSupport.AssertTrue(added.Drawer.Origin == "manual", "new drawer should receive manual ownership after migration");
        }
        finally
        {
            if (Directory.Exists(path)) Directory.Delete(path, recursive: true);
        }
    }

    private static void VerifyEmbeddingModelMigration()
    {
        var path = Directory.CreateTempSubdirectory("bogmem-bogdb-embedding-migration-").FullName;
        try
        {
            string drawerId;
            using (var lexical = new BogDbMemoryStore(path, new LexicalHashEmbedder()))
                drawerId = lexical.Add("models", "migration", "A canine receives veterinary care.").Drawer.Id;

            var semantic = new TestEmbedder("test-semantic-v1");
            using (var migrated = new BogDbMemoryStore(path, semantic))
            {
                TestSupport.AssertTrue(semantic.EmbeddedTexts == 1,
                    "changing embedding identity should re-embed existing drawers once");
                TestSupport.AssertTrue(migrated.Status().EmbeddingModel == semantic.Identity,
                    "status should report the vector producer identity");
                TestSupport.AssertTrue(migrated.Get(drawerId)?.Embedding[1] == 1.0f,
                    "model migration should persist replacement vectors");
            }

            var sameModel = new TestEmbedder("test-semantic-v1");
            using var reopened = new BogDbMemoryStore(path, sameModel);
            TestSupport.AssertTrue(sameModel.EmbeddedTexts == 0,
                "reopening with the recorded embedding identity should not re-embed");
            var preview = reopened.ReplaceSource(
                "models", "migration", "/tmp/preview.md", ["A preview-only chunk."], dryRun: true);
            TestSupport.AssertTrue(preview.Changed && !preview.Applied && sameModel.EmbeddedTexts == 0,
                "dry-run source replacement should not invoke the embedder");
        }
        finally
        {
            if (Directory.Exists(path)) Directory.Delete(path, recursive: true);
        }
    }

    private static void VerifyCandidateSearchBeyondOverfetchWindow()
    {
        var path = Directory.CreateTempSubdirectory("bogmem-bogdb-candidate-search-").FullName;
        try
        {
            using var store = new BogDbMemoryStore(path, new LexicalHashEmbedder());
            var east = Enumerable.Range(0, 70)
                .Select(index => index == 66
                    ? "The heliotrope protocol rotates service credentials without downtime."
                    : $"Eastern architecture note number {index} covers ordinary deployment operations.")
                .ToArray();
            var west = Enumerable.Range(0, 2)
                .Select(index => index == 1
                    ? "The heliotrope garden uses drought-tolerant flowers and drip irrigation."
                    : $"Western biology note number {index} covers ordinary laboratory operations.")
                .ToArray();
            store.ReplaceSource("east", "operations", "/notes/east.md", east);
            store.ReplaceSource("west", "operations", "/notes/west.md", west);

            var unscoped = store.Search("heliotrope credential rotation", limit: 1);
            TestSupport.AssertTrue(
                unscoped.Single().Drawer.Wing == "east",
                "native candidates should find a relevant drawer beyond the 64-result hydration window");

            var scoped = store.Search("heliotrope irrigation", limit: 2, wing: "west");
            TestSupport.AssertTrue(
                scoped.Count > 0 && scoped.All(hit => hit.Drawer.Wing == "west") &&
                scoped[0].Drawer.Content.Contains("drip irrigation", StringComparison.Ordinal),
                "scoped search should remain complete and isolated beyond the overfetch window");
        }
        finally
        {
            if (Directory.Exists(path)) Directory.Delete(path, recursive: true);
        }
    }

    private static void VerifyIndexedSourceReplacement()
    {
        var path = Directory.CreateTempSubdirectory("bogmem-bogdb-indexed-replacement-").FullName;
        const string source = "/notes/indexed-source.md";
        try
        {
            using (var initial = new BogDbMemoryStore(path, new LexicalHashEmbedder()))
                initial.ReplaceSource(
                    "project",
                    "operations",
                    source,
                    Enumerable.Range(0, 5)
                        .Select(index => $"Original indexed source chunk {index} describes ordinary operations.")
                        .ToArray());

            var replacement = Enumerable.Range(0, 3)
                .Select(index => $"Replacement indexed source chunk {index} documents the heliotrope procedure.")
                .ToArray();
            using (var reopened = new BogDbMemoryStore(path, new LexicalHashEmbedder()))
            {
                var result = reopened.ReplaceSource("project", "operations", source, replacement);
                TestSupport.AssertTrue(
                    result.Applied && result.PreviousDrawers == 5 && result.CurrentDrawers == 3,
                    "persisted indexed source replacement should replace every prior drawer");

                var scoped = reopened.Search("heliotrope procedure", limit: 10, sourceFile: source);
                TestSupport.AssertTrue(
                    scoped.Count == 3 &&
                    scoped.Select(hit => hit.Drawer.Id).Distinct(StringComparer.Ordinal).Count() == 3 &&
                    scoped.All(hit => hit.Drawer.Content.StartsWith("Replacement", StringComparison.Ordinal)),
                    "persisted indexed source lookup should contain no stale or duplicate hits after replacement");
            }

            using var final = new BogDbMemoryStore(path, new LexicalHashEmbedder());
            TestSupport.AssertTrue(
                final.DeleteBySource(source, dryRun: false) == 3 && final.Status().Drawers == 0,
                "persisted indexed source deletion should remove every matching drawer");
        }
        finally
        {
            if (Directory.Exists(path)) Directory.Delete(path, recursive: true);
        }
    }

    private sealed class TestEmbedder(string identity) : IMemoryEmbedder
    {
        public string Identity { get; } = identity;
        public int Dimensions => LexicalHashEmbedder.Dimensions;
        public int EmbeddedTexts { get; private set; }

        public float[] Embed(string text)
        {
            EmbeddedTexts++;
            var vector = new float[Dimensions];
            vector[1] = 1.0f;
            return vector;
        }

        public void Dispose()
        {
        }
    }
}
