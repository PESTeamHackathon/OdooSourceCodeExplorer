using System.Text.Json;
using Chat.App.Indexing;
using Chat.Indexer;
using Microsoft.Extensions.Configuration;

if (args.Contains("--help"))
{
    Console.WriteLine("""
        BusinessChat indexing and retrieval diagnostics (Milestones 2–3)
        dotnet run --project Chat.Indexer -- [options]
        dotnet run --project Chat.Indexer -- build-search --artifact <source-index> --search-output <external-folder>
        dotnet run --project Chat.Indexer -- search --index <search-index> --question <text> [--mode hybrid|semantic|keyword]
        dotnet run --project Chat.Indexer -- explore --source <odoo-root> --question <business-question>
          Isolated Copilot investigation; no Ollama, Qdrant or index required. Existing chat is unchanged.
          --module <odoo-module> --limit <1-20> --expected-version <tag> --format json
          --source <checkout-root>     SourceRepository:Path
          --output <external-folder>   Indexing:OutputPath (outside both repositories)
          --include <relative-path>    Replace IncludePaths with one directory or file
          --version <application-tag>  Optional application version
          --config <absolute-json>     Optional configuration file override
        Standard .NET keys are also accepted, for example:
          --Indexing:IncludePaths:0 addons/sale --Indexing:IncludePaths:1 addons/stock
        Environment prefix: BUSINESSCHAT_ (e.g. BUSINESSCHAT_SourceRepository__Path).
        Indexing selects tracked UTF-8 .py files. Exploration reads local .py/.xml/.csv files.
        No source code is executed.
        """);
    return 0;
}

using var stop = new CancellationTokenSource();
Console.CancelKeyPress += (_, e) => { e.Cancel = true; stop.Cancel(); };
try
{
    var configArgs = args.ToList();
    var command = configArgs.FirstOrDefault() is "build-search" or "search" or "explore" ? configArgs[0] : "index";
    if (command != "index") configArgs.RemoveAt(0);
    string? configPath = null;
    string? include = null;
    for (var i = 0; i < configArgs.Count; i++)
    {
        if (configArgs[i] is not ("--config" or "--include")) continue;
        if (i + 1 >= configArgs.Count || configArgs[i + 1].StartsWith("--"))
            throw new ArgumentException($"{configArgs[i]} requires a value.");
        if (configArgs[i] == "--config") configPath = Path.GetFullPath(configArgs[i + 1]);
        else include = configArgs[i + 1];
        configArgs.RemoveRange(i, 2);
        i--;
    }
    var builder = new ConfigurationBuilder()
        .AddJsonFile(Path.Combine(AppContext.BaseDirectory, "appsettings.json"), optional: false);
    if (configPath is not null) builder.AddJsonFile(configPath, optional: false);
    var configuration = builder.AddEnvironmentVariables("BUSINESSCHAT_")
        .AddCommandLine(configArgs.ToArray(), new Dictionary<string, string>
        {
            ["--source"] = "SourceRepository:Path",
            ["--output"] = "Indexing:OutputPath",
            ["--version"] = "SourceRepository:ApplicationVersion",
            ["--artifact"] = "Retrieval:SourceIndexPath",
            ["--search-output"] = "Retrieval:OutputPath",
            ["--index"] = "Retrieval:IndexPath",
            ["--question"] = "Retrieval:Question",
            ["--module"] = "Retrieval:Module",
            ["--limit"] = "Retrieval:Limit",
            ["--expected-version"] = "Retrieval:ExpectedApplicationVersion",
            ["--mode"] = "Retrieval:Mode",
            ["--format"] = "Retrieval:Format"
        }).Build();
    using var configurationLifetime = configuration as IDisposable;
    if (command == "explore")
    {
        await ExplorationCommand.RunAsync(configuration, stop.Token);
        return 0;
    }
    if (command != "index")
    {
        await RetrievalCommands.RunAsync(command, configuration, stop.Token);
        return 0;
    }
    var repository = configuration.GetSection("SourceRepository").Get<SourceRepositoryOptions>() ?? new();
    var options = configuration.GetSection("Indexing").Get<IndexOptions>() ?? new();
    if (!configuration.GetSection("Indexing:IncludePaths").Exists()) options.IncludePaths = ["."];
    if (include is not null) options.IncludePaths = [include];
    var result = await new RepositoryIndexer([new PythonChunker()]).BuildAsync(repository, options, stop.Token);
    using var manifest = JsonDocument.Parse(await File.ReadAllTextAsync(Path.Combine(result, "manifest.json"), stop.Token));
    var metadata = manifest.RootElement;
    Console.WriteLine($"Index: {result}");
    Console.WriteLine($"Commit: {metadata.GetProperty("gitCommit").GetString()}");
    Console.WriteLine($"Files: {metadata.GetProperty("files").GetArrayLength()}, chunks: {metadata.GetProperty("chunkCount").GetInt32()}, skipped files: {metadata.GetProperty("skippedFiles").GetArrayLength()}");
    Console.WriteLine($"Oversized chunks retained whole: {metadata.GetProperty("oversizedChunkCount").GetInt32()}");
    if (metadata.GetProperty("workingTreeDirty").GetBoolean())
        Console.WriteLine("WARNING: Working tree is dirty; this index includes local edits and is not an exact commit snapshot.");
    return 0;
}
catch (OperationCanceledException)
{
    Console.Error.WriteLine(args.FirstOrDefault() == "explore"
        ? "Investigation canceled; no answer was published."
        : "Operation canceled; no completed index was published by an interrupted build.");
    return 130;
}
catch (Exception ex) when (ex is ArgumentException or IOException or InvalidOperationException or System.ComponentModel.Win32Exception or UnauthorizedAccessException or System.FormatException or HttpRequestException or JsonException or Microsoft.Data.Sqlite.SqliteException)
{
    Console.Error.WriteLine($"Operation failed: {ex.Message}");
    return 1;
}
