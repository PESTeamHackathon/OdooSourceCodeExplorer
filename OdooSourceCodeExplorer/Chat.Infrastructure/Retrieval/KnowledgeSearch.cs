using System.Text.Json;
using Chat.App.Indexing;
using Chat.App.Retrieval;

namespace Chat.Infrastructure.Retrieval;

public sealed class KnowledgeSearch : IKnowledgeSearch
{
    private readonly OllamaEmbeddingClient _embeddings;
    private readonly QdrantVectorStore _vectors;
    private sealed record IndexState(SearchIndexManifest Manifest, string Database, SqliteKnowledgeStore Store);
    private readonly Lazy<IndexState> _state;
    private SearchIndexManifest _manifest => _state.Value.Manifest;
    private SqliteKnowledgeStore _sqlite => _state.Value.Store;
    private string _db => _state.Value.Database;

    public KnowledgeSearch(RetrievalOptions options, OllamaEmbeddingClient embeddings, QdrantVectorStore vectors)
    {
        options.Validate();
        _embeddings = embeddings; _vectors = vectors;
        // Defer file access until retrieval, so missing configuration yields a safe chat error.
        _state = new Lazy<IndexState>(() =>
        {
            if (string.IsNullOrWhiteSpace(options.IndexPath)) throw new ArgumentException("Retrieval:IndexPath must select a completed search generation.");
            var manifest = JsonSerializer.Deserialize<SearchIndexManifest>(File.ReadAllText(Path.Combine(options.IndexPath, "search-manifest.json")), SearchIndexBuilder.Json)
                ?? throw new InvalidOperationException("Missing search manifest.");
            if (manifest.SchemaVersion != 1 || manifest.EmbeddingTextVersion != EmbeddingDocuments.Version || manifest.Model != options.EmbeddingModel)
                throw new InvalidOperationException("Incompatible search index schema, embedding format or model. Select matching configuration or rebuild.");
            var db = Path.Combine(options.IndexPath, "knowledge.sqlite");
            return new IndexState(manifest, db, new SqliteKnowledgeStore(db));
        });
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
