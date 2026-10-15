using System.Text.Json;
using Chat.App.Retrieval;
using Chat.Infrastructure.Retrieval;
using Microsoft.Extensions.Configuration;

namespace Chat.Indexer;

internal static class RetrievalCommands
{
    public static async Task RunAsync(string command, IConfiguration configuration, CancellationToken ct)
    {
        var options = configuration.GetSection("Retrieval").Get<RetrievalOptions>() ?? new();
        using var embeddings = new OllamaEmbeddingClient(options);
        using var vectors = new QdrantVectorStore(options);
        if (command == "build-search")
        {
            var artifact = configuration["Retrieval:SourceIndexPath"];
            if (string.IsNullOrWhiteSpace(artifact)) throw new ArgumentException("--artifact must select a completed Milestone 2 source index.");
            var path = await new SearchIndexBuilder(options, embeddings, vectors).BuildAsync(artifact, Console.WriteLine, ct);
            Console.WriteLine($"Search index: {path}");
            return;
        }
        var search = new KnowledgeSearch(options, embeddings, vectors);
        var question = configuration["Retrieval:Question"] ?? throw new ArgumentException("--question is required.");
        var limit = configuration.GetValue("Retrieval:Limit", 5);
        var module = configuration["Retrieval:Module"];
        var version = configuration["Retrieval:ExpectedApplicationVersion"];
        var mode = configuration["Retrieval:Mode"] ?? "hybrid";
        RetrievalResult result;
        if (mode == "hybrid") result = await new HybridCodeRetriever(search).RetrieveAsync(new(question, limit, module, version), ct);
        else
        {
            if (string.IsNullOrWhiteSpace(question) || question.Length > 4000 || limit is < 1 or > 20)
                throw new ArgumentException("Question must contain 1–4000 characters and limit must be 1–20.");
            var info = await search.GetInfoAsync(ct);
            if (version is not null && version != info.ApplicationVersion) throw new InvalidOperationException("Application version mismatch.");
            var ranked = mode switch
            {
                "semantic" => await search.SemanticAsync(question, module, limit, ct),
                "keyword" => await search.KeywordAsync(question, module, limit, ct),
                _ => throw new ArgumentException("--mode must be hybrid, semantic or keyword.")
            };
            var chunks = (await search.LoadAsync(ranked.Select(h => h.ChunkId).ToArray(), ct)).ToDictionary(c => c.Id);
            result = new(info, ranked.Select((h, i) => new RetrievedChunk(chunks[h.ChunkId], h.Score,
                mode == "semantic" ? i + 1 : null, mode == "keyword" ? i + 1 : null)).ToArray());
        }
        if (configuration["Retrieval:Format"] == "json") Console.WriteLine(JsonSerializer.Serialize(result, new JsonSerializerOptions(JsonSerializerDefaults.Web) { WriteIndented = true }));
        else
        {
            Console.WriteLine($"Index: {result.Index.IndexId}; source: {result.Index.SourceIndexId}; commit: {result.Index.GitCommit}; version: {result.Index.ApplicationVersion ?? "unknown"}; dirty: {result.Index.WorkingTreeDirty}");
            foreach (var hit in result.Hits)
                Console.WriteLine($"{hit.FusionScore:F6} semantic-rank={hit.SemanticRank?.ToString() ?? "-"} keyword-rank={hit.KeywordRank?.ToString() ?? "-"} {hit.Evidence.QualifiedName ?? hit.Evidence.Kind} {hit.Evidence.RelativePath}:{hit.Evidence.StartLine}-{hit.Evidence.EndLine}");
            if (result.Hits.Count == 0) Console.WriteLine("No matching evidence.");
        }
    }
}
