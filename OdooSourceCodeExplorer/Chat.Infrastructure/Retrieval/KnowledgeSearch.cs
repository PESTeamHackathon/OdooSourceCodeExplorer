using System.Text.Json;
using Chat.App.Indexing;
using Chat.App.Retrieval;

namespace Chat.Infrastructure.Retrieval;

public sealed class KnowledgeSearch : IKnowledgeSearch
{
    private readonly OllamaEmbeddingClient _embeddings;
    private readonly QdrantVectorStore _vectors;
    private readonly SearchIndexManifest _manifest;
    private readonly SqliteKnowledgeStore _sqlite;
    private readonly string _db;

    public KnowledgeSearch(RetrievalOptions options, OllamaEmbeddingClient embeddings, QdrantVectorStore vectors)
    {
        options.Validate();
        if (string.IsNullOrWhiteSpace(options.IndexPath)) throw new ArgumentException("Retrieval:IndexPath must select a completed search generation.");
        _embeddings = embeddings; _vectors = vectors;
        _manifest = JsonSerializer.Deserialize<SearchIndexManifest>(File.ReadAllText(Path.Combine(options.IndexPath, "search-manifest.json")), SearchIndexBuilder.Json)
            ?? throw new InvalidOperationException("Missing search manifest.");
        if (_manifest.SchemaVersion != 1 || _manifest.EmbeddingTextVersion != EmbeddingDocuments.Version || _manifest.Model != options.EmbeddingModel)
            throw new InvalidOperationException("Incompatible search index schema, embedding format or model. Select matching configuration or rebuild.");
        _db = Path.Combine(options.IndexPath, "knowledge.sqlite");
        _sqlite = new SqliteKnowledgeStore(_db);
    }
    public async Task<KnowledgeIndexInfo> GetInfoAsync(CancellationToken cancellationToken)
    {
        if (await SearchIndexBuilder.FileHashAsync(_db, cancellationToken) != _manifest.SqliteSha256)
            throw new InvalidOperationException("SQLite knowledge index checksum mismatch.");
        if (await _embeddings.GetDigestAsync(cancellationToken) != _manifest.ModelDigest)
            throw new InvalidOperationException("Embedding model digest differs from the indexed model. Restore that model or rebuild the index.");
        await _vectors.ValidateAsync(_manifest.Collection, _manifest.Dimensions, _manifest.PointCount, cancellationToken);
        return _manifest.Info;
    }
    public async Task<IReadOnlyList<RankedChunk>> SemanticAsync(string question, string? module, int limit, CancellationToken cancellationToken)
    {
        var vectors = await _embeddings.EmbedAsync(["search_query: " + question], cancellationToken);
        if (vectors[0].Length != _manifest.Dimensions) throw new InvalidOperationException("Query embedding dimensions differ from the index.");
        return await _vectors.SearchAsync(_manifest.Collection, vectors[0], module, limit, cancellationToken);
    }
    public Task<IReadOnlyList<RankedChunk>> KeywordAsync(string question, string? module, int limit, CancellationToken cancellationToken)
        => _sqlite.SearchAsync(question, module, limit, cancellationToken);
    public Task<IReadOnlyList<CodeChunk>> LoadAsync(IReadOnlyList<string> ids, CancellationToken cancellationToken)
        => _sqlite.LoadAsync(ids, cancellationToken);
}
