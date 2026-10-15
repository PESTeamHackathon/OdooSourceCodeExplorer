using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Chat.App.Indexing;
using Chat.App.Retrieval;
using Chat.Infrastructure.Retrieval;

namespace Chat.App.Tests;

public sealed class RetrievalTests : IDisposable
{
    private readonly string _temp = Path.Combine(OperatingSystem.IsMacOS() ? "/private/tmp" : Path.GetTempPath(), "businesschat-retrieval-" + Guid.NewGuid().ToString("N"));
    private static CodeChunk Chunk(string id, string symbol = "action_cancel", string module = "sale", string content = "Cancel a sales order")
        => new(id, "python", $"addons/{module}/models/order.py", module, "addons/" + module,
            "method", symbol, "Order." + symbol, "Order", 1, 2, "class Order:", content);

    [Fact]
    public async Task FusionCombinesBothRankingsAndDeduplicatesWithoutComparingRawScores()
    {
        var search = new StubSearch
        {
            Semantic = [new("a", 0.95), new("b", 0.90), new("a", 0.89)],
            Keyword = [new("b", 1000), new("c", 0.01)]
        };
        var result = await new HybridCodeRetriever(search).RetrieveAsync(new("Cancel order", 3));
        Assert.Equal(["b", "a", "c"], result.Hits.Select(h => h.Evidence.Id));
        Assert.Equal(2, result.Hits[0].SemanticRank);
        Assert.Equal(1, result.Hits[0].KeywordRank);
        Assert.Equal(1d / 62 + 1d / 61, result.Hits[0].FusionScore, 10);
    }

    [Fact]
    public async Task VersionMismatchPreventsSearching()
    {
        var search = new StubSearch();
        await Assert.ThrowsAsync<InvalidOperationException>(() => new HybridCodeRetriever(search).RetrieveAsync(new("Question", ExpectedApplicationVersion: "different")));
        Assert.Equal(0, search.SearchCalls);
    }

    [Theory]
    [InlineData("", 5)]
    [InlineData("question", 0)]
    [InlineData("question", 21)]
    public async Task InvalidQueriesNeverSearch(string question, int limit)
    {
        var search = new StubSearch();
        await Assert.ThrowsAsync<ArgumentException>(() => new HybridCodeRetriever(search).RetrieveAsync(new(question, limit)));
        Assert.Equal(0, search.SearchCalls);
    }

    [Fact]
    public async Task MissingEvidenceFailsInsteadOfReturningMixedIndexes()
    {
        var search = new StubSearch { Semantic = [new("missing", 1)], OmitEvidence = true };
        await Assert.ThrowsAsync<InvalidOperationException>(() => new HybridCodeRetriever(search).RetrieveAsync(new("Question")));
    }

    [Fact]
    public async Task BranchFailureIsNotSilentlyDowngradedToKeywordOnly()
    {
        var search = new StubSearch { FailSemantic = true, Keyword = [new("b", 1)] };
        await Assert.ThrowsAsync<HttpRequestException>(() => new HybridCodeRetriever(search).RetrieveAsync(new("Question")));
    }

    [Fact]
    public async Task EmptyRankingsReturnEmptyEvidence()
    {
        var result = await new HybridCodeRetriever(new StubSearch()).RetrieveAsync(new("Nothing"));
        Assert.Empty(result.Hits);
        Assert.Equal("commit", result.Index.GitCommit);
    }

    [Fact]
    public async Task RealSqliteFtsFindsIdentifiersFiltersModulesAndPreservesFullEvidence()
    {
        Directory.CreateDirectory(_temp);
        var store = new SqliteKnowledgeStore(Path.Combine(_temp, "knowledge.sqlite"));
        var first = Chunk("sale");
        await store.BuildAsync([first, Chunk("stock", "button_validate", "stock", "Validate delivery picking")], default);
        var exact = await store.SearchAsync("action_cancel", null, 5, default);
        Assert.Equal("sale", Assert.Single(exact).ChunkId);
        Assert.Empty(await store.SearchAsync("cancel", "stock", 5, default));
        Assert.Equal(first, Assert.Single(await store.LoadAsync(["sale"], default)));
        Assert.Empty(await store.SearchAsync("\" * : ()", null, 5, default));
        // User text is tokenized into quoted terms; it cannot become FTS or SQL syntax.
        await store.SearchAsync("\" OR 1=1; DROP TABLE chunks; --", null, 5, default);
        Assert.Single(await store.LoadAsync(["sale"], default));
    }

    [Fact]
    public async Task KeywordSearchPrefersTheExactSymbolOverShortRepeatedMentions()
    {
        Directory.CreateDirectory(_temp);
        var store = new SqliteKnowledgeStore(Path.Combine(_temp, "exact.sqlite"));
        await store.BuildAsync([Chunk("definition", "button_validate", content: new string('x', 1000)),
            Chunk("mention", "style", content: "button_validate button_validate button_validate")], default);
        Assert.Equal("definition", (await store.SearchAsync("button_validate", null, 5, default))[0].ChunkId);
    }

    [Fact]
    public async Task CompletedIndexRejectsChangedModelDigestAndCorruptSqlite()
    {
        Directory.CreateDirectory(_temp);
        var db = Path.Combine(_temp, "knowledge.sqlite");
        await new SqliteKnowledgeStore(db).BuildAsync([Chunk("id")], default);
        var checksum = Convert.ToHexString(SHA256.HashData(await File.ReadAllBytesAsync(db))).ToLowerInvariant();
        var info = new KnowledgeIndexInfo("search", "source", "commit", "v1", false, 1);
        var manifest = new SearchIndexManifest(1, EmbeddingDocuments.Version, info, "selected", "nomic-embed-text:v1.5", "original", 2, 1, 1800, 200, "sourcehash", checksum);
        await File.WriteAllTextAsync(Path.Combine(_temp, "search-manifest.json"), JsonSerializer.Serialize(manifest, new JsonSerializerOptions(JsonSerializerDefaults.Web)));
        var options = new RetrievalOptions { IndexPath = _temp };
        var calls = 0;
        using var embeddings = new OllamaEmbeddingClient(options, new HttpClient(new Handler(_ =>
        {
            calls++;
            return Task.FromResult(Response("{\"models\":[{\"name\":\"nomic-embed-text:v1.5\",\"digest\":\"changed\"}]}"));
        })));
        using var vectors = new QdrantVectorStore(options, new HttpClient(new Handler(_ => throw new Exception("Must not search incompatible index"))));
        var search = new KnowledgeSearch(options, embeddings, vectors);
        var mismatch = await Assert.ThrowsAsync<InvalidOperationException>(() => search.GetInfoAsync(default));
        Assert.Contains("digest", mismatch.Message);
        await File.AppendAllTextAsync(db, "tampered");
        var corruption = await Assert.ThrowsAsync<InvalidOperationException>(() => search.GetInfoAsync(default));
        Assert.Contains("checksum", corruption.Message);
        Assert.Equal(1, calls);
    }

    [Fact]
    public void EmbeddingWindowsCoverTheTailWithoutTruncatingOriginalChunk()
    {
        var chunk = Chunk("long", content: new string('x', 10_000) + "TAIL_EXCEPTION_RULE");
        var windows = EmbeddingDocuments.ForChunk(chunk, 256, 32).ToArray();
        Assert.True(windows.Length > 1);
        Assert.All(windows, w => Assert.StartsWith("search_document: ", w));
        Assert.Contains("TAIL EXCEPTION RULE", windows[^1]);
        Assert.EndsWith("TAIL_EXCEPTION_RULE", chunk.Content);
        Assert.Throws<ArgumentException>(() => EmbeddingDocuments.ForChunk(chunk, 256, 255).ToArray());
    }

    [Fact]
    public async Task OllamaUsesBatchApiAndExplicitlyDisablesTruncation()
    {
        using var http = new HttpClient(new Handler(async request =>
        {
            Assert.EndsWith("/api/embed", request.RequestUri!.AbsoluteUri);
            using var body = JsonDocument.Parse(await request.Content!.ReadAsStringAsync());
            Assert.False(body.RootElement.GetProperty("truncate").GetBoolean());
            Assert.Equal(2, body.RootElement.GetProperty("input").GetArrayLength());
            return Response("{\"embeddings\":[[1,0],[0,1]]}");
        }));
        using var client = new OllamaEmbeddingClient(new(), http);
        Assert.Equal(2, (await client.EmbedAsync(["one", "two"], default)).Length);
    }

    [Fact]
    public async Task OllamaRejectsMissingZeroAndInconsistentVectors()
    {
        foreach (var body in new[] { "{\"embeddings\":[]}", "{\"embeddings\":[[0,0]]}", "{\"embeddings\":[[]]}" })
        {
            using var client = new OllamaEmbeddingClient(new(), new HttpClient(new Handler(_ => Task.FromResult(Response(body)))));
            await Assert.ThrowsAsync<InvalidOperationException>(() => client.EmbedAsync(["query"], default));
        }
    }

    [Fact]
    public async Task QdrantQueriesTheSelectedCollectionWithModuleFilterAndCollapsesWindows()
    {
        using var store = new QdrantVectorStore(new(), new HttpClient(new Handler(async request =>
        {
            Assert.EndsWith("/collections/selected/points/query", request.RequestUri!.AbsoluteUri);
            using var body = JsonDocument.Parse(await request.Content!.ReadAsStringAsync());
            Assert.Equal("stock", body.RootElement.GetProperty("filter").GetProperty("must")[0].GetProperty("match").GetProperty("value").GetString());
            return Response("""{"result":{"points":[{"score":0.9,"payload":{"chunk_id":"a"}},{"score":0.8,"payload":{"chunk_id":"a"}},{"score":0.7,"payload":{"chunk_id":"b"}}]}}""");
        })));
        Assert.Equal(["a", "b"], (await store.SearchAsync("selected", [1, 0], "stock", 5, default)).Select(x => x.ChunkId));
    }

    [Fact]
    public async Task FailedVectorWriteCleansItsCollectionAndDoesNotPublishSearchIndex()
    {
        var input = await SourceFixtureAsync();
        var options = new RetrievalOptions { OutputPath = Path.Combine(_temp, "search"), BatchSize = 1 };
        var deleted = false;
        using var embeddings = new OllamaEmbeddingClient(options, new HttpClient(new Handler(request => Task.FromResult(Response(
            request.Method == HttpMethod.Get ? "{\"models\":[{\"name\":\"nomic-embed-text:v1.5\",\"digest\":\"digest\"}]}" : "{\"embeddings\":[[1,0]]}")))));
        using var vectors = new QdrantVectorStore(options, new HttpClient(new Handler(request =>
        {
            if (request.Method == HttpMethod.Delete) deleted = true;
            return Task.FromResult(request.RequestUri!.AbsolutePath.EndsWith("/points")
                ? new HttpResponseMessage(HttpStatusCode.ServiceUnavailable) : Response("{\"result\":true}"));
        })));
        await Assert.ThrowsAsync<HttpRequestException>(() => new SearchIndexBuilder(options, embeddings, vectors).BuildAsync(input));
        Assert.True(deleted);
        Assert.Empty(Directory.GetDirectories(options.OutputPath));
    }

    [Fact]
    public async Task CorruptSourceArtifactIsRejectedBeforeEmbedding()
    {
        var input = await SourceFixtureAsync();
        await File.AppendAllTextAsync(Path.Combine(input, "chunks.jsonl"), "tampered");
        var options = new RetrievalOptions { OutputPath = Path.Combine(_temp, "search") };
        using var embeddings = new OllamaEmbeddingClient(options, new HttpClient(new Handler(_ => throw new Exception("Must not contact Ollama"))));
        using var vectors = new QdrantVectorStore(options);
        await Assert.ThrowsAsync<InvalidOperationException>(() => new SearchIndexBuilder(options, embeddings, vectors).BuildAsync(input));
        Assert.False(Directory.Exists(options.OutputPath));
    }

    private async Task<string> SourceFixtureAsync()
    {
        var input = Path.Combine(_temp, "source-index");
        Directory.CreateDirectory(input);
        var json = new JsonSerializerOptions(JsonSerializerDefaults.Web);
        var text = JsonSerializer.Serialize(Chunk("id"), json) + "\n";
        await File.WriteAllTextAsync(Path.Combine(input, "chunks.jsonl"), text);
        await File.WriteAllTextAsync(Path.Combine(input, "manifest.json"), JsonSerializer.Serialize(new
        {
            schemaVersion = 1, indexId = "source", gitCommit = "commit", workingTreeDirty = false,
            applicationVersion = "v1", repositoryPath = Path.Combine(_temp, "checkout"), chunkCount = 1,
            chunksSha256 = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text))).ToLowerInvariant()
        }, json));
        return input;
    }

    private static HttpResponseMessage Response(string json) => new(HttpStatusCode.OK) { Content = new StringContent(json, Encoding.UTF8, "application/json") };
    private sealed class Handler(Func<HttpRequestMessage, Task<HttpResponseMessage>> callback) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) => callback(request);
    }
    private sealed class StubSearch : IKnowledgeSearch
    {
        public IReadOnlyList<RankedChunk> Semantic { get; init; } = [];
        public IReadOnlyList<RankedChunk> Keyword { get; init; } = [];
        public bool OmitEvidence { get; init; }
        public bool FailSemantic { get; init; }
        public int SearchCalls { get; private set; }
        public Task<KnowledgeIndexInfo> GetInfoAsync(CancellationToken cancellationToken) => Task.FromResult(new KnowledgeIndexInfo("index", "source", "commit", "v1", false, 3));
        public Task<IReadOnlyList<RankedChunk>> SemanticAsync(string question, string? module, int limit, CancellationToken cancellationToken)
        {
            SearchCalls++;
            return FailSemantic ? Task.FromException<IReadOnlyList<RankedChunk>>(new HttpRequestException()) : Task.FromResult(Semantic);
        }
        public Task<IReadOnlyList<RankedChunk>> KeywordAsync(string question, string? module, int limit, CancellationToken cancellationToken)
        { SearchCalls++; return Task.FromResult(Keyword); }
        public Task<IReadOnlyList<CodeChunk>> LoadAsync(IReadOnlyList<string> ids, CancellationToken cancellationToken)
            => Task.FromResult<IReadOnlyList<CodeChunk>>(OmitEvidence ? [] : ids.Select(id => Chunk(id)).ToArray());
    }
    public void Dispose() { if (Directory.Exists(_temp)) Directory.Delete(_temp, true); }
}
