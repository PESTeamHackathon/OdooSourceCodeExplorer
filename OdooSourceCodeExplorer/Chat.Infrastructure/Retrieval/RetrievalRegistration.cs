using Chat.App.Retrieval;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Chat.Infrastructure.Retrieval;

public static class RetrievalRegistration
{
    // Opt-in for a future host. Chat.Web deliberately does not call this in Milestone 3.
    public static IServiceCollection AddCodeRetrieval(this IServiceCollection services, IConfiguration configuration)
    {
        var options = configuration.GetSection("Retrieval").Get<RetrievalOptions>() ?? new();
        options.Validate();
        services.AddSingleton(options);
        services.AddSingleton<OllamaEmbeddingClient>();
        services.AddSingleton<QdrantVectorStore>();
        services.AddSingleton<IKnowledgeSearch, KnowledgeSearch>();
        services.AddTransient<ICodeRetriever, HybridCodeRetriever>();
        return services;
    }
}
