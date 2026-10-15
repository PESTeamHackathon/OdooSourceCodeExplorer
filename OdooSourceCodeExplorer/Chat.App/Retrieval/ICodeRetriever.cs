using Chat.App.Indexing;

namespace Chat.App.Retrieval;

// Internal evidence contracts. Do not serialize these through a business-user API.
public sealed record RetrievalQuery(string Question, int Limit = 5, string? Module = null, string? ExpectedApplicationVersion = null);
public sealed record KnowledgeIndexInfo(string IndexId, string SourceIndexId, string GitCommit,
    string? ApplicationVersion, bool WorkingTreeDirty, int ChunkCount);
public sealed record RankedChunk(string ChunkId, double Score);
public sealed record RetrievedChunk(CodeChunk Evidence, double FusionScore, int? SemanticRank, int? KeywordRank);
public sealed record RetrievalResult(KnowledgeIndexInfo Index, IReadOnlyList<RetrievedChunk> Hits);

public interface ICodeRetriever
{
    Task<RetrievalResult> RetrieveAsync(RetrievalQuery query, CancellationToken cancellationToken = default);
}

// One infrastructure boundary for the deployed index; no source checkout is needed.
public interface IKnowledgeSearch
{
    Task<KnowledgeIndexInfo> GetInfoAsync(CancellationToken cancellationToken);
    Task<IReadOnlyList<RankedChunk>> SemanticAsync(string question, string? module, int limit, CancellationToken cancellationToken);
    Task<IReadOnlyList<RankedChunk>> KeywordAsync(string question, string? module, int limit, CancellationToken cancellationToken);
    Task<IReadOnlyList<CodeChunk>> LoadAsync(IReadOnlyList<string> ids, CancellationToken cancellationToken);
}
