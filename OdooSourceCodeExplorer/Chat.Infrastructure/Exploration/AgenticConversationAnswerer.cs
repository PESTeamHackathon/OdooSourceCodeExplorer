using Chat.App;
using Microsoft.Extensions.Logging;

namespace Chat.Infrastructure.Exploration;

// Stateless adapter. Each request creates its own source tools, sessions, evidence and shared budget.
public sealed class AgenticConversationAnswerer(CopilotSourceExplorer explorer, string repositoryPath,
    ILogger<AgenticConversationAnswerer> logger) : IConversationAnswerer
{
    public async Task<ConversationAnswer> AnswerAsync(IReadOnlyList<ChatMessage> messages, CancellationToken cancellationToken = default)
    {
        var run = Guid.NewGuid().ToString("N");
        logger.LogInformation("Agentic exploration started ({Run}); prior messages: {Count}.", run, messages.Count - 1);
        var result = await explorer.ExploreAsync(repositoryPath, messages,
            diagnostic => logger.LogDebug("Agentic exploration {Run}: {Diagnostic}", run, diagnostic), cancellationToken);
        logger.LogInformation("Agentic exploration finished ({Run}); searches: {Searches}, reads: {Reads}, status: {Status}, stop: {Stop}.",
            run, result.SearchCalls, result.ReadCalls, result.Trace?.Status, result.StopReason);
        return new(result.Answer, result.Trace);
    }
}
