using Chat.App;
using Chat.Infrastructure;
using Chat.Infrastructure.Exploration;
using GitHub.Copilot;
using Microsoft.Extensions.Configuration;

namespace Chat.Indexer;

internal static class ExplorationCommand
{
    public static async Task RunAsync(IConfiguration configuration, CancellationToken ct)
    {
        var repository = configuration["SourceRepository:Path"] ?? "";
        var question = configuration["Retrieval:Question"] ?? throw new ArgumentException("--question is required.");
        var options = configuration.GetSection("Copilot").Get<CopilotOptions>() ?? new();
        // Exploration needs several model/tool rounds; existing web defaults are unchanged.
        if (configuration["Copilot:TimeoutSeconds"] is null) options.TimeoutSeconds = 240;
        if (options.TimeoutSeconds is < 10 or > 600 || !Path.IsPathFullyQualified(options.BaseDirectory))
            throw new ArgumentException("Use a 10–600 second Copilot timeout and an absolute Copilot:BaseDirectory.");
        var token = new[] { "COPILOT_GITHUB_TOKEN", "GH_TOKEN", "GITHUB_TOKEN" }
            .Select(Environment.GetEnvironmentVariable).FirstOrDefault(v => !string.IsNullOrWhiteSpace(v));
        await using var client = new CopilotClient(new CopilotClientOptions
        {
            Mode = CopilotClientMode.Empty,
            BaseDirectory = options.BaseDirectory,
            GitHubToken = token,
            UseLoggedInUser = token is null
        });
        Console.Error.WriteLine("Local exploration: live checkout, no index/version validation; only bounded Python/XML/CSV search and reads.");
        try
        {
            var result = await new CopilotSourceExplorer(client, options).ExploreAsync(repository, question, Console.Error.WriteLine, ct);
            Console.WriteLine(result.Answer);
            Console.Error.WriteLine($"Investigation: {result.SearchCalls} searches, {result.ReadCalls} reads, {result.Evidence.Select(e => e.RelativePath).Distinct().Count()} files read.");
            foreach (var evidence in result.Evidence)
                Console.Error.WriteLine($"  {evidence.ChunkId}: {evidence.RelativePath}:{evidence.StartLine}-{evidence.EndLine}");
        }
        catch (AnswerGenerationException ex)
        {
            // The normal indexing CLI prints exception messages; unwrap neither SDK errors nor source data.
            throw new InvalidOperationException(ex.Message);
        }
    }
}
