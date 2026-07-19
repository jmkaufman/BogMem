using System.Text.Json;
namespace Bogmem.Slices.Config;
public sealed record BogmemConfig(string Backend="chroma",int ChunkSize=800,int ChunkOverlap=100,int MinChunkSize=50,bool MinChunkSizeExplicit=false,string? PalacePath=null);
public static class ConfigResolver
{
 public static BogmemConfig Resolve(JsonElement? file=null,IReadOnlyDictionary<string,string?>? environment=null)
 { var f=file is {ValueKind:JsonValueKind.Object} x?x:new JsonElement(); var e=environment??Environment.GetEnvironmentVariables().Cast<System.Collections.DictionaryEntry>().ToDictionary(x=>x.Key.ToString()!,x=>x.Value?.ToString());
  string? Get(string key)=>f.ValueKind==JsonValueKind.Object&&f.TryGetProperty(key,out var p)&&p.ValueKind==JsonValueKind.String?p.GetString():null;
  var backend=(Get("backend")??GetEnv(e,"MEMPALACE_BACKEND")??"chroma").ToLowerInvariant(); if(backend is not("chroma" or "sqlite_exact" or "milvus" or "qdrant" or "pgvector" or "bogdb")) backend="chroma";
  int Number(string key,int fallback){if(f.ValueKind==JsonValueKind.Object&&f.TryGetProperty(key,out var p)&&p.ValueKind==JsonValueKind.Number&&p.TryGetInt32(out var n)&&n>0)return n;return fallback;}
  var explicitMin=f.ValueKind==JsonValueKind.Object&&f.TryGetProperty("min_chunk_size",out _); var palace=GetEnv(e,"MEMPALACE_PALACE_PATH")??Get("palace_path");
  return new(backend,Number("chunk_size",800),Number("chunk_overlap",100),Number("min_chunk_size",50),explicitMin,palace);
 }
 private static string? GetEnv(IReadOnlyDictionary<string,string?> e,string k)=>e.TryGetValue(k,out var v)?v:null;
}
