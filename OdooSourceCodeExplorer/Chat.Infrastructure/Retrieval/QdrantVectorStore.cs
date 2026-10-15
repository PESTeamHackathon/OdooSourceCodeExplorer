using System.Net.Http.Json;
using System.Text.Json;
using Chat.App.Retrieval;

namespace Chat.Infrastructure.Retrieval;

public sealed record VectorPoint(string Id, float[] Vector, string ChunkId, string? Module);

public sealed class QdrantVectorStore : IDisposable
{
    private readonly HttpClient _http;
    public QdrantVectorStore(RetrievalOptions options, HttpClient? http = null)
    {
        options.Validate();
        _http = http ?? new HttpClient { Timeout = TimeSpan.FromMinutes(2) };
        _http.BaseAddress = new Uri(options.QdrantUrl.TrimEnd('/') + "/");
        if (!string.IsNullOrWhiteSpace(options.QdrantApiKey)) _http.DefaultRequestHeaders.Add("api-key", options.QdrantApiKey);
    }
    private static string Route(string collection) => "collections/" + Uri.EscapeDataString(collection);
    public async Task CreateAsync(string collection, int dimensions, CancellationToken ct)
    {
        using var response = await _http.PutAsJsonAsync(Route(collection), new { vectors = new { size = dimensions, distance = "Cosine" } }, ct);
        response.EnsureSuccessStatusCode();
    }
    public async Task UpsertAsync(string collection, IReadOnlyList<VectorPoint> points, CancellationToken ct)
    {
        using var response = await _http.PutAsJsonAsync(Route(collection) + "/points?wait=true", new
        {
            points = points.Select(p => new { id = p.Id, vector = p.Vector, payload = new { chunk_id = p.ChunkId, module = p.Module } })
        }, ct);
        response.EnsureSuccessStatusCode();
    }
    public async Task ValidateAsync(string collection, int dimensions, int count, CancellationToken ct)
    {
        using var response = await _http.GetAsync(Route(collection), ct);
        response.EnsureSuccessStatusCode();
        using var json = await JsonDocument.ParseAsync(await response.Content.ReadAsStreamAsync(ct), cancellationToken: ct);
        var vectors = json.RootElement.GetProperty("result").GetProperty("config").GetProperty("params").GetProperty("vectors");
        if (vectors.GetProperty("size").GetInt32() != dimensions || vectors.GetProperty("distance").GetString() != "Cosine")
            throw new InvalidOperationException("Qdrant vector configuration differs from the knowledge manifest.");
        using var counted = await _http.PostAsJsonAsync(Route(collection) + "/points/count", new { exact = true }, ct);
        counted.EnsureSuccessStatusCode();
        using var result = await JsonDocument.ParseAsync(await counted.Content.ReadAsStreamAsync(ct), cancellationToken: ct);
        if (result.RootElement.GetProperty("result").GetProperty("count").GetInt32() != count)
            throw new InvalidOperationException("Qdrant point count differs from the knowledge manifest.");
    }
    public async Task<IReadOnlyList<RankedChunk>> SearchAsync(string collection, float[] vector, string? module, int limit, CancellationToken ct)
    {
        using var response = await _http.PostAsJsonAsync(Route(collection) + "/points/query", new
        {
            query = vector, limit = limit * 4, with_payload = true,
            filter = module is null ? null : new { must = new[] { new { key = "module", match = new { value = module } } } }
        }, ct);
        response.EnsureSuccessStatusCode();
        using var json = await JsonDocument.ParseAsync(await response.Content.ReadAsStreamAsync(ct), cancellationToken: ct);
        return json.RootElement.GetProperty("result").GetProperty("points").EnumerateArray()
            .Select(p => new RankedChunk(p.GetProperty("payload").GetProperty("chunk_id").GetString()!, p.GetProperty("score").GetDouble()))
            .DistinctBy(p => p.ChunkId).Take(limit).ToArray();
    }
    public async Task DeleteAsync(string collection, CancellationToken ct)
    {
        using var response = await _http.DeleteAsync(Route(collection), ct);
        response.EnsureSuccessStatusCode();
    }
    public void Dispose() => _http.Dispose();
}
