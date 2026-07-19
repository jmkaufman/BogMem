using Bogmem.Harness;
using Bogmem.Slices.Tests.Config;
using Bogmem.Slices.Tests.Embedding;
using Bogmem.Slices.Tests.Chroma;
using Bogmem.Slices.Tests.KnowledgeGraph;
ConfigResolverTests.Run(); EmbedderTests.Run(); ChromaStoreTests.Run(); KnowledgeGraphStoreTests.Run();
var ledger=new TestDispositionLedgerWriter();ledger.Append("config","ConfigResolver","pass","EXACT",slice:"S8");ledger.Append("embedder","Embedder","pass","ULP",slice:"S3");ledger.Write("tests/parity/disposition/smoke.json");Console.WriteLine("Bogmem slice smoke tests passed.");
