using Chat.App.Retrieval;

namespace Chat.App;

// Internal provider inputs/outputs, deliberately separate from business-facing ChatMessage.
public sealed record AnswerRequest(IReadOnlyList<ChatMessage> Messages, KnowledgeIndexInfo Index,
    IReadOnlyList<RetrievedChunk> Evidence, bool EvidenceOmitted,
    ExplorationAnswerContext? ExplorationContext = null);
public sealed record ExplorationAnswerContext(bool SourceComplete, bool RequiresRuntimeConfirmation,
    IReadOnlyList<string> SourceQuestions, IReadOnlyList<string> RuntimeQuestions);
public sealed record AnswerClaim(string Text, IReadOnlyList<string> EvidenceIds);
public sealed record AnswerDraft(string Status, IReadOnlyList<AnswerClaim> Claims);
public sealed record EvidenceReference(string ChunkId, string RelativePath, int StartLine, int EndLine);
public sealed record ChatEvidenceTrace(KnowledgeIndexInfo Index, IReadOnlyList<EvidenceReference> Evidence,
    IReadOnlyList<string> UsedEvidenceIds, string Status, bool EvidenceOmitted);

public sealed class KnowledgeChatOptions
{
    public bool UseAgenticExploration { get; set; } = false;
    public string ExpectedApplicationVersion { get; set; } = "";
    public int RetrievalLimit { get; set; } = 12;
    public int MaxEvidenceCharacters { get; set; } = 32_000;
    public int RetrievalTimeoutSeconds { get; set; } = 30;
}
