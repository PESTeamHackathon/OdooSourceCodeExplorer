using Chat.App.Retrieval;

namespace Chat.App;

// Internal provider inputs/outputs, deliberately separate from business-facing ChatMessage.
public sealed record AnswerRequest(IReadOnlyList<ChatMessage> Messages, KnowledgeIndexInfo Index,
    IReadOnlyList<RetrievedChunk> Evidence, bool EvidenceOmitted,
    ExplorationAnswerContext? ExplorationContext = null, bool PureMode = false);
public sealed record ExplorationAnswerContext(bool SourceComplete, bool RequiresRuntimeConfirmation,
    IReadOnlyList<string> SourceQuestions, IReadOnlyList<string> RuntimeQuestions);
public sealed record AnswerClaim(string Text, IReadOnlyList<string> EvidenceIds);
public sealed record AnswerDraft(string Status, IReadOnlyList<AnswerClaim> Claims)
{
    // Pure mode: Copilot's raw text as a single claim, returned without validation.
    public const string Pure = "pure";
}
public sealed record EvidenceReference(string ChunkId, string RelativePath, int StartLine, int EndLine);
public sealed record ChatEvidenceTrace(KnowledgeIndexInfo Index, IReadOnlyList<EvidenceReference> Evidence,
    IReadOnlyList<string> UsedEvidenceIds, string Status, bool EvidenceOmitted);

public sealed class KnowledgeChatOptions
{
    public bool UseAgenticExploration { get; set; } = false;
    // Nullable overrides preserve legacy single-backend launches.
    public bool? IndexedAnswersEnabled { get; set; }
    public bool? ExplorationEnabled { get; set; }
    public ChatMode? DefaultMode { get; set; }

    public ChatCapabilities GetCapabilities()
    {
        var capabilities = new ChatCapabilities(IndexedAnswersEnabled ?? !UseAgenticExploration,
            ExplorationEnabled ?? UseAgenticExploration,
            DefaultMode ?? (UseAgenticExploration ? ChatMode.Exploration : ChatMode.Indexed));
        if (!capabilities.IsAvailable(capabilities.DefaultMode))
            throw new ArgumentException("KnowledgeChat:DefaultMode must select an enabled answer mode.");
        return capabilities;
    }

    public string ExpectedApplicationVersion { get; set; } = "";
    public int RetrievalLimit { get; set; } = 12;
    public int MaxEvidenceCharacters { get; set; } = 32_000;
    public int RetrievalTimeoutSeconds { get; set; } = 30;
}
