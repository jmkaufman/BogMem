namespace Bogmem.Slices.Embedding;

public static class MemoryEmbedderFactory
{
    public const string ModelEnvironmentVariable = "BOGMEM_EMBEDDING_MODEL";
    public const string CacheEnvironmentVariable = "BOGMEM_MODEL_CACHE";

    public static IMemoryEmbedder CreateDefault()
    {
        var configured =
            Environment.GetEnvironmentVariable(ModelEnvironmentVariable) ??
            Environment.GetEnvironmentVariable("MEMPALACE_EMBEDDING_MODEL") ??
            "minilm";
        return configured.Trim().ToLowerInvariant() switch
        {
            "minilm" or "all-minilm-l6-v2" => new MiniLmEmbedder(
                Environment.GetEnvironmentVariable(CacheEnvironmentVariable)),
            "lexical" or "lexical-hash" => new LexicalHashEmbedder(),
            _ => throw new InvalidOperationException(
                $"Unsupported embedding model '{configured}'. " +
                $"Set {ModelEnvironmentVariable}=minilm or {ModelEnvironmentVariable}=lexical."),
        };
    }
}
