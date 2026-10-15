namespace Chat.App.Retrieval;

public sealed class HybridCodeRetriever(IKnowledgeSearch search) : ICodeRetriever
{
    public async Task<RetrievalResult> RetrieveAsync(RetrievalQuery query, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(query.Question) || query.Question.Length > 4000)
            throw new ArgumentException("A question of 1–4000 characters is required.");
        if (query.Limit is < 1 or > 20) throw new ArgumentException("Limit must be between 1 and 20.");
        var info = await search.GetInfoAsync(cancellationToken);
        if (query.ExpectedApplicationVersion is not null && query.ExpectedApplicationVersion != info.ApplicationVersion)
            throw new InvalidOperationException("Knowledge index application version does not match the requested version.");

        var count = Math.Max(20, query.Limit * 4);
        var semanticTask = search.SemanticAsync(query.Question, query.Module, count, cancellationToken);
        var keywordTask = search.KeywordAsync(query.Question, query.Module, count, cancellationToken);
        await Task.WhenAll(semanticTask, keywordTask);
        var semantic = Ranks(await semanticTask);
        var keyword = Ranks(await keywordTask);
        // Reciprocal rank fusion avoids comparing cosine similarity directly with BM25.
        var fused = semantic.Keys.Union(keyword.Keys).Select(id => new
        {
            Id = id,
            Semantic = semantic.GetValueOrDefault(id), Keyword = keyword.GetValueOrDefault(id),
            Score = (semantic.TryGetValue(id, out var s) ? 1d / (60 + s) : 0) +
                    (keyword.TryGetValue(id, out var k) ? 1d / (60 + k) : 0)
        }).OrderByDescending(x => x.Score).ThenBy(x => x.Id, StringComparer.Ordinal).Take(query.Limit).ToArray();
        var evidence = (await search.LoadAsync(fused.Select(x => x.Id).ToArray(), cancellationToken)).ToDictionary(x => x.Id);
        if (fused.Any(x => !evidence.ContainsKey(x.Id))) throw new InvalidOperationException("Index stores are inconsistent: evidence is missing.");
        return new RetrievalResult(info, fused.Select(x => new RetrievedChunk(evidence[x.Id], x.Score,
            x.Semantic == 0 ? null : x.Semantic, x.Keyword == 0 ? null : x.Keyword)).ToArray());
    }

    private static Dictionary<string, int> Ranks(IReadOnlyList<RankedChunk> hits) => hits.Select(h => h.ChunkId)
        .Distinct(StringComparer.Ordinal).Select((id, i) => (id, rank: i + 1)).ToDictionary(x => x.id, x => x.rank);
}
