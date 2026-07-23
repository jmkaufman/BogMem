using Bogmem.Harness;
using Bogmem.Slices.Tests;
using Bogmem.Slices.Tests.Chroma;
using Bogmem.Slices.Tests.Chunkers;
using Bogmem.Slices.Tests.Cli;
using Bogmem.Slices.Tests.Config;
using Bogmem.Slices.Tests.Dates;
using Bogmem.Slices.Tests.Dedup;
using Bogmem.Slices.Tests.DeferredBackends;
using Bogmem.Slices.Tests.Dynamics;
using Bogmem.Slices.Tests.Embedding;
using Bogmem.Slices.Tests.Ids;
using Bogmem.Slices.Tests.KnowledgeGraph;
using Bogmem.Slices.Tests.Locking;
using Bogmem.Slices.Tests.Mcp;
using Bogmem.Slices.Tests.Mining;
using Bogmem.Slices.Tests.Model;
using Bogmem.Slices.Tests.Sanitizer;
using Bogmem.Slices.Tests.Search;
using Bogmem.Slices.Tests.Spellcheck;
using Bogmem.Slices.Tests.Storage;
using Bogmem.Slices.Tests.Sync;
using Bogmem.Slices.Tests.Wal;

// The product default exercises local MiniLM. The deterministic compatibility
// suite opts into the dependency-free lexical fallback and never downloads a model.
Environment.SetEnvironmentVariable("BOGMEM_EMBEDDING_MODEL", "lexical");

var failures = new List<Exception>();

// Smoke suites from the initial lane plus deterministic modules that write
// their own per-module disposition ledgers.
RunModule("config", ConfigResolverTests.Run);
RunModule("embedder", EmbedderTests.Run);
RunModule("chroma", ChromaStoreTests.Run);
RunModule("knowledge_graph", KnowledgeGraphStoreTests.Run);
RunModule("bogdb_memory_store", BogDbMemoryStoreTests.Run);
RunModule("project_config", ProjectConfigTests.Run);
RunModule("project_miner", ProjectMinerTests.Run);
RunModule("project_sync", ProjectSyncTests.Run);
RunModule("ids", IdRecipesTests.Run);
RunModule("wal", WalWriterTests.Run);
RunModule("search", SearcherTests.Run);
RunModule("dedup", DedupGrouperTests.Run);
RunModule("mcp", McpServerTests.Run);
RunModule("deferred_ids", DeferredIdOracleTests.Run);
RunModule("sanitizer", QuerySanitizerTests.Run);
RunModule("dates", ContentDateExtractorTests.Run);
RunModule("model", ModelInventoryTests.Run);
RunModule("cli_exit_codes", CliExitCodeTests.Run);

var smoke = new TestDispositionLedgerWriter();
smoke.Append("config", "ConfigResolver", "pass", "EXACT", slice: "S8");
smoke.Append("embedder", "Embedder", "pass", "ULP", slice: "S3");
smoke.Append("chroma", "ChromaStore", "pass", "BOUNDED", slice: "S7");
smoke.Append("knowledge_graph", "KnowledgeGraphStore", "pass", "EXACT", slice: "S5c");
smoke.Append("bogdb_memory_store", "BogDbMemoryStore", "pass", "PRODUCT", "persistent CRUD + native HNSW/BM25 hybrid retrieval", "P1");
smoke.Append("project_config", "ProjectConfig", "pass", "PRODUCT", "YAML room routing with explicit overrides", "P4");
smoke.Append("project_miner", "ProjectMiner", "pass", "PRODUCT", "git-aware idempotent project ingestion", "P2");
smoke.Append("project_sync", "ProjectSync", "pass", "PRODUCT", "safe dry-run/apply project pruning", "P3");
smoke.Append("ids", "IdRecipes", "pass", "EXACT", slice: "S1");
smoke.Append("wal", "WalWriter", "pass", "EXACT", slice: "S10");
smoke.Append("search", "Searcher", "pass", "EXACT", slice: "S4");
smoke.Append("dedup", "DedupGrouper", "pass", "EXACT", slice: "S5a");
smoke.Append("mcp", "McpServer", "pass", "EXACT", slice: "S9");
smoke.Append("deferred_ids", "DeferredIdOracle", "not_applicable", "PLACEHOLDER", "oracle staged", "S13");
smoke.Write(Path.Combine(TestKit.Root, "tests/parity/disposition/smoke.json"));

// Golden-corpus replay suites from the Claude lane: each writes its disposition
// ledger even when a suite fails, so partial evidence survives a red run.
RunSuite("chunkers", ChunkerTests.Run);
RunSuite("locking", FileLockTests.Run);
RunSuite("storage", GraphStoreTests.Run);
RunSuite("dynamics", DynamicsScorerTests.Run);
RunSuite("spellcheck", SpellerTests.Run);

if (failures.Count > 0)
{
    foreach (var f in failures) Console.Error.WriteLine(f.Message);
    Console.Error.WriteLine($"FAILED: {failures.Count} suite(s) red.");
    return 1;
}

Console.WriteLine("Bogmem slice tests passed (integrated modern-writer lanes).");
return 0;

void RunModule(string module, Action run)
{
    try
    {
        run();
        Console.WriteLine($"  {module}: ok");
    }
    catch (Exception ex)
    {
        failures.Add(new InvalidOperationException($"{module}: {ex.Message}", ex));
        Console.Error.WriteLine($"  {module}: FAIL — {ex.Message}");
    }
}

void RunSuite(string module, Action<TestDispositionLedgerWriter> run)
{
    var ledger = new TestDispositionLedgerWriter();
    try { run(ledger); }
    catch (Exception ex) { failures.Add(new InvalidOperationException($"{module}: {ex.Message}", ex)); }
    finally { ledger.Write(Path.Combine(TestKit.Root, $"tests/parity/disposition/{module}.json")); }
}
