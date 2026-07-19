using Bogmem.Harness;
using Bogmem.Slices.Tests;
using Bogmem.Slices.Tests.Chroma;
using Bogmem.Slices.Tests.Chunkers;
using Bogmem.Slices.Tests.Config;
using Bogmem.Slices.Tests.Dynamics;
using Bogmem.Slices.Tests.Embedding;
using Bogmem.Slices.Tests.KnowledgeGraph;
using Bogmem.Slices.Tests.Locking;
using Bogmem.Slices.Tests.Spellcheck;
using Bogmem.Slices.Tests.Storage;

// Prior-lane smoke suites.
ConfigResolverTests.Run(); EmbedderTests.Run(); ChromaStoreTests.Run(); KnowledgeGraphStoreTests.Run();
var smoke = new TestDispositionLedgerWriter();
smoke.Append("config", "ConfigResolver", "pass", "EXACT", slice: "S8");
smoke.Append("embedder", "Embedder", "pass", "ULP", slice: "S3");
smoke.Write(Path.Combine(TestKit.Root, "tests/parity/disposition/smoke.json"));

// Golden-corpus replay suites (this lane): each writes its disposition ledger
// even when a suite fails, so partial evidence survives a red run.
var failures = new List<Exception>();
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
Console.WriteLine("Bogmem slice tests passed (S2, S11, S6, S5b, S12 + smoke).");
return 0;

void RunSuite(string module, Action<TestDispositionLedgerWriter> run)
{
    var ledger = new TestDispositionLedgerWriter();
    try { run(ledger); }
    catch (Exception ex) { failures.Add(ex); }
    finally { ledger.Write(Path.Combine(TestKit.Root, $"tests/parity/disposition/{module}.json")); }
}
