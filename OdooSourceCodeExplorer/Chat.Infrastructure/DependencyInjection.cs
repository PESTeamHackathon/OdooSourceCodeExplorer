using Chat.App;
using Chat.Infrastructure.Exploration;
using Chat.Infrastructure.Retrieval;
using Microsoft.Extensions.Logging;
using GitHub.Copilot;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace Chat.Infrastructure;

public static class DependencyInjection
{
    public static IServiceCollection AddCopilotChat(this IServiceCollection services, IConfiguration configuration)
    {
        services.AddOptions<CopilotOptions>()
            .Bind(configuration.GetSection(CopilotOptions.SectionName))
            .Validate(o => o.TimeoutSeconds is >= 10 and <= 600, "Copilot:TimeoutSeconds must be between 10 and 600.")
            .Validate(o => !string.IsNullOrWhiteSpace(o.BaseDirectory) && Path.IsPathFullyQualified(o.BaseDirectory),
                "Copilot:BaseDirectory must be an absolute path.")
            .ValidateOnStart();

        // DI owns async disposal. Startup is lazy, so the UI also works before login.
        services.AddSingleton(sp =>
        {
            // The managed runtime needs an explicit SDK token; do not rely on
            // the interactive CLI's environment-variable discovery.
            var token = new[] { "COPILOT_GITHUB_TOKEN", "GH_TOKEN", "GITHUB_TOKEN" }
                .Select(Environment.GetEnvironmentVariable)
                .FirstOrDefault(value => !string.IsNullOrWhiteSpace(value));

            return new CopilotClient(new CopilotClientOptions
            {
                Mode = CopilotClientMode.Empty,
                BaseDirectory = sp.GetRequiredService<IOptions<CopilotOptions>>().Value.BaseDirectory,
                GitHubToken = token,
                UseLoggedInUser = token is null
            });
        });
        services.AddSingleton<IAnswerGenerator, CopilotAnswerGenerator>();
        var knowledgeOptions = configuration.GetSection("KnowledgeChat").Get<KnowledgeChatOptions>() ?? new();
        if (!knowledgeOptions.UseAgenticExploration && (knowledgeOptions.RetrievalLimit is < 1 or > 20 || knowledgeOptions.MaxEvidenceCharacters is < 1000 or > 100_000 || knowledgeOptions.RetrievalTimeoutSeconds is < 1 or > 120))
            throw new ArgumentException("Invalid KnowledgeChat retrieval limit, evidence budget or timeout.");
        services.AddSingleton(knowledgeOptions);
        if (knowledgeOptions.UseAgenticExploration)
        {
            var repositoryPath = configuration["SourceRepository:Path"] ?? "";
            if (!Path.IsPathFullyQualified(repositoryPath) || !Directory.Exists(repositoryPath))
                throw new ArgumentException("Agentic exploration requires an existing absolute SourceRepository:Path.");
            services.AddSingleton(sp => new CopilotSourceExplorer(sp.GetRequiredService<CopilotClient>(),
                sp.GetRequiredService<IOptions<CopilotOptions>>().Value, sp.GetRequiredService<IAnswerGenerator>()));
            services.AddSingleton<IConversationAnswerer>(sp => new AgenticConversationAnswerer(
                sp.GetRequiredService<CopilotSourceExplorer>(), repositoryPath, sp.GetRequiredService<ILogger<AgenticConversationAnswerer>>()));
            services.AddScoped<IChatService>(sp => new ChatService(sp.GetRequiredService<IConversationAnswerer>()));
        }
        else
        {
            services.AddCodeRetrieval(configuration);
            services.AddScoped<IChatService>(sp => new ChatService(sp.GetRequiredService<IAnswerGenerator>(),
                sp.GetRequiredService<Chat.App.Retrieval.ICodeRetriever>(), knowledgeOptions));
        }
        return services;
    }
}
