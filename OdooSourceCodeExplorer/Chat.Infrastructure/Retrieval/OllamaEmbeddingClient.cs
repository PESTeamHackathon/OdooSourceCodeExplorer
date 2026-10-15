using System.Net.Http.Json;
using System.Text.Json;

namespace Chat.Infrastructure.Retrieval;

public sealed class OllamaEmbeddingClient : IDisposable
{
    private readonly HttpClient _http;
    private readonly string _model;
    public OllamaEmbeddingClient(RetrievalOptions options, HttpClient? http = null)
    {
        options.Validate();
        _model = options.EmbeddingModel;
        _http = http ?? new HttpClient { Timeout = TimeSpan.FromMinutes(5) };
        _http.BaseAddress = new Uri(options.OllamaUrl.TrimEnd('/') + "/");
    }

    public async Task<string> GetDigestAsync(CancellationToken ct)
    {
        using var response = await _http.GetAsync("api/tags", ct);
        response.EnsureSuccessStatusCode();
        using var json = await JsonDocument.ParseAsync(await response.Content.ReadAsStreamAsync(ct), cancellationToken: ct);
        var name = _model.Contains(':') ? _model : _model + ":latest";
        foreach (var model in json.RootElement.GetProperty("models").EnumerateArray())
            if (model.GetProperty("name").GetString() == name)
                return model.GetProperty("digest").GetString() ?? throw new InvalidOperationException("Missing model digest.");
        throw new InvalidOperationException($"Local embedding model {_model} is not installed. Run: ollama pull {_model}");
    }

    public async Task<float[][]> EmbedAsync(IReadOnlyList<string> texts, CancellationToken ct)
    {
        using var response = await _http.PostAsJsonAsync("api/embed", new { model = _model, input = texts, truncate = false }, ct);
        if (!response.IsSuccessStatusCode)
            throw new InvalidOperationException($"Ollama embeddings failed (HTTP {(int)response.StatusCode}). Check model support/context size; reduce WindowCharacters if needed. Inputs are never silently truncated.");
        using var json = await JsonDocument.ParseAsync(await response.Content.ReadAsStreamAsync(ct), cancellationToken: ct);
        var vectors = json.RootElement.GetProperty("embeddings").Deserialize<float[][]>()!;
        if (vectors.Length != texts.Count || vectors.Any(v => v.Length == 0 || v.Any(x => !float.IsFinite(x)) || v.All(x => x == 0)) ||
            vectors.Any(v => v.Length != vectors[0].Length))
            throw new InvalidOperationException("Ollama returned an invalid embedding batch.");
        return vectors;
    }
    public void Dispose() => _http.Dispose();
}
