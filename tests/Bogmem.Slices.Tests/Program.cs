using Bogmem.Harness;
using Bogmem.Slices.Tests.Config;
using Bogmem.Slices.Tests.Embedding;
using Bogmem.Slices.Tests.Chroma;
using Bogmem.Slices.Tests.KnowledgeGraph;
using Bogmem.Slices.Tests.Ids;
using Bogmem.Slices.Tests.Wal;
using Bogmem.Slices.Tests.Search;
using Bogmem.Slices.Tests.Dedup;
using Bogmem.Slices.Tests.Mcp;
using Bogmem.Slices.Tests.DeferredBackends;

// Prior-wave slices (already in worktree).
ConfigResolverTests.Run();
EmbedderTests.Run();
ChromaStoreTests.Run();
KnowledgeGraphStoreTests.Run();

// This seat's assigned slices.
IdRecipesTests.Run();
WalWriterTests.Run();
SearcherTests.Run();
DedupGrouperTests.Run();
McpServerTests.Run();
DeferredIdOracleTests.Run();

var ledger = new TestDispositionLedgerWriter();
ledger.Append("config", "ConfigResolver", "pass", "EXACT", slice: "S8");
ledger.Append("embedder", "Embedder", "pass", "ULP", slice: "S3");
ledger.Append("ids", "IdRecipes", "pass", "EXACT", slice: "S1");
ledger.Append("wal", "WalWriter", "pass", "EXACT", slice: "S10");
ledger.Append("search", "Searcher", "pass", "EXACT", slice: "S4");
ledger.Append("dedup", "DedupGrouper", "pass", "EXACT", slice: "S5a");
ledger.Append("mcp", "McpServer", "pass", "EXACT", slice: "S9");
ledger.Append("deferred_ids", "DeferredIdOracle", "not_applicable", "PLACEHOLDER", "oracle staged", "S13");
ledger.Write("tests/parity/disposition/smoke.json");
Console.WriteLine("Bogmem slice smoke tests passed.");
