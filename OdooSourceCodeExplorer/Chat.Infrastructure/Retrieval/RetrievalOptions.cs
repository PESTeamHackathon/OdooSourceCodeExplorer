namespace Chat.Infrastructure.Retrieval;

public sealed class RetrievalOptions
{
    public string OllamaUrl { get; set; } = "http://localhost:11434/";
    public string EmbeddingModel { get; set; } = "nomic-embed-text:v1.5";
    public string QdrantUrl { get; set; } = "http://localhost:6333/";
    public string? QdrantApiKey { get; set; }
    public string IndexPath { get; set; } = "";
    public string OutputPath { get; set; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "BusinessChat", "search-indexes");
    public int BatchSize { get; set; } = 8;
    public int WindowCharacters { get; set; } = 1800;
    public int WindowOverlap { get; set; } = 200;

    public void Validate()
    {
        foreach (var url in new[] { OllamaUrl, QdrantUrl })
            if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || uri.Scheme is not ("http" or "https"))
                throw new ArgumentException("OllamaUrl and QdrantUrl must be absolute HTTP(S) URLs.");
        if (string.IsNullOrWhiteSpace(EmbeddingModel) || EmbeddingModel.Contains("cloud", StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("A local embedding model is required.");
        if (BatchSize is < 1 or > 64 || WindowCharacters is < 128 or > 4000 || WindowOverlap < 0 || WindowOverlap > WindowCharacters / 2)
            throw new ArgumentException("Invalid embedding batch size or window/overlap settings.");
    }
}

public sealed record SearchIndexManifest(int SchemaVersion, string EmbeddingTextVersion,
    Chat.App.Retrieval.KnowledgeIndexInfo Info, string Collection, string Model, string ModelDigest,
    int Dimensions, int PointCount, int WindowCharacters, int WindowOverlap, string SourceChunksSha256, string SqliteSha256);
