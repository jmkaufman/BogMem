using System.Text.Json;
using Bogmem.Slices.Config;
namespace Bogmem.Slices.Tests.Config;
public static class ConfigResolverTests
{
 public static void Run(){using var d=JsonDocument.Parse("{\"backend\":\"SQLite_Exact\",\"chunk_size\":600,\"min_chunk_size\":30}");var c=ConfigResolver.Resolve(d.RootElement,new Dictionary<string,string?>{{"MEMPALACE_BACKEND","chroma"}});Assert(c.Backend=="sqlite_exact"&&c.ChunkSize==600&&c.MinChunkSize==30&&c.MinChunkSizeExplicit,"config precedence");}
 private static void Assert(bool value,string message){if(!value)throw new InvalidOperationException(message);}
}
