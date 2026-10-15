using System.Diagnostics;
using System.Text.Json;
using Chat.App.Indexing;
using Chat.Indexer;
using Microsoft.Extensions.Configuration;

namespace Chat.App.Tests;

public sealed class PythonChunkerTests
{
    [Fact]
    public void ConfigurationBindingDoesNotRetainDefaultWholeRepositorySelection()
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { ["Indexing:IncludePaths:0"] = "." })
            .AddCommandLine(["--Indexing:IncludePaths:0", "addons/sale", "--Indexing:IncludePaths:1", "addons/stock"])
            .Build();
        using var lifetime = configuration as IDisposable;
        var options = configuration.GetSection("Indexing").Get<IndexOptions>()!;
        Assert.Equal(["addons/sale", "addons/stock"], options.IncludePaths);
    }

    private static IReadOnlyList<CodeChunk> Chunk(string source) => new PythonChunker().Chunk(
        new SourceDocument("addons/sale/models/order.py", source, "sale", "addons/sale"));

    [Fact]
    public void KeepsDecoratorsMultilineSignaturesAndOdooClassContext()
    {
        const string source = """
            import odoo
            class Order(models.Model):
                _name = 'sale.order'
                _inherit = [
                    'mail.thread',
                ]
                state = fields.Selection([('cancel', 'Cancelled')])
                @api.depends(
                    'state',
                )
                def cancel(
                    self,
                ):
                    if self.state == 'sale':
                        raise UserError('Locked')
                    self.state = 'cancel'
                extra = fields.Char()
                async def confirm(self):
                    return True
            """;
        var chunks = Chunk(source);
        var cancel = Assert.Single(chunks, c => c.Symbol == "cancel");
        Assert.Equal("method", cancel.Kind);
        Assert.Equal("Order.cancel", cancel.QualifiedName);
        Assert.Equal("Order", cancel.ClassName);
        Assert.Equal(8, cancel.StartLine);
        Assert.Equal(16, cancel.EndLine);
        Assert.Contains("@api.depends(", cancel.Content);
        Assert.Contains("raise UserError", cancel.Content);
        Assert.Contains("_name = 'sale.order'", cancel.Context);
        Assert.Contains("'mail.thread'", cancel.Context);
        Assert.DoesNotContain("extra =", cancel.Content);
        Assert.Contains(chunks, c => c.Kind == "class-context" && c.Content.Contains("extra ="));
        Assert.Equal("sale", cancel.ModuleName);
        Assert.Equal("addons/sale", cancel.ModulePath);
        Assert.Contains(chunks, c => c.Symbol == "confirm");
        AssertCoverage(source, chunks);
    }

    [Fact]
    public void StringsCommentsAndContinuedExpressionsDoNotCreateFakeSymbols()
    {
        const string source = """"
            text = """
            class Fake:
                def fake(self): pass
            """
            # def comment(): pass
            values = (
                'def another(): pass',
            )
            def real():
                """Documentation with class Imaginary: in it."""
                return "# not a comment"
            """";
        var chunks = Chunk(source);
        Assert.Equal(["real"], chunks.Where(c => c.Kind == "function").Select(c => c.Symbol));
        Assert.DoesNotContain(chunks, c => c.Symbol == "Fake" || c.Symbol == "fake");
        AssertCoverage(source, chunks);
    }

    [Fact]
    public void NestedFunctionsRemainInTheirParentAndNestedClassesHaveQualifiedNames()
    {
        const string source = """
            class Outer:
                class Inner:
                    def work(self):
                        def helper():
                            return 1
                        return helper()
            def outside():
                return 2
            """;
        var chunks = Chunk(source);
        var work = Assert.Single(chunks, c => c.Symbol == "work");
        Assert.Equal("Outer.Inner.work", work.QualifiedName);
        Assert.Contains("def helper", work.Content);
        Assert.DoesNotContain(chunks, c => c.Symbol == "helper");
        Assert.Contains(chunks, c => c.Kind == "function" && c.Symbol == "outside");
        AssertCoverage(source, chunks);
    }

    [Fact]
    public void IdsAreDeterministicAndChangeWithContent()
    {
        Assert.Equal(Chunk("def f():\n    return 1\n"), Chunk("def f():\n    return 1\n"));
        Assert.NotEqual(Chunk("def f():\n    return 1")[0].Id, Chunk("def f():\n    return 2")[0].Id);
        Assert.Equal(Chunk("def f():\n    return 1\n")[0].Id, Chunk("def f():\r\n    return 1\r\n")[0].Id);
        var first = Chunk("class A:\n    _name = 'order.one'\n    def f(self): pass").Single(c => c.Symbol == "f");
        var second = Chunk("class A:\n    _name = 'order.two'\n    def f(self): pass").Single(c => c.Symbol == "f");
        Assert.Equal(first.Content, second.Content);
        Assert.NotEqual(first.Id, second.Id);
    }

    [Fact]
    public void TabsAndEscapedQuotesAreHandledWithoutCuttingMethods()
    {
        const string source = "class A:\n\tdef f(self):\n\t\tvalue = 'it\\'s a value'\n\t\treturn value\n\tdef g(self):\n\t\tpass\n";
        var chunks = Chunk(source);
        Assert.Equal(2, chunks.Count(c => c.Kind == "method"));
        Assert.Contains("return value", chunks.Single(c => c.Symbol == "f").Content);
        AssertCoverage(source, chunks);
    }

    [Fact]
    public void ManifestAndImportOnlyFilesProduceModuleChunks()
    {
        Assert.Equal("module", Assert.Single(Chunk("{'name': 'Sales', 'depends': ['base']}\n")).Kind);
        Assert.Equal("module", Assert.Single(Chunk("from . import models\n")).Kind);
        Assert.Empty(Chunk("\n\n"));
    }

    private static void AssertCoverage(string source, IReadOnlyList<CodeChunk> chunks)
    {
        var lines = source.Replace("\r\n", "\n").TrimEnd('\n').Split('\n');
        for (var i = 0; i < lines.Length; i++)
            if (!string.IsNullOrWhiteSpace(lines[i]))
                Assert.Single(chunks, c => c.StartLine <= i + 1 && c.EndLine >= i + 1);
        foreach (var chunk in chunks)
            Assert.Equal(string.Join('\n', lines[(chunk.StartLine - 1)..chunk.EndLine]), chunk.Content);
    }
}

public sealed class RepositoryIndexerTests : IDisposable
{
    // /tmp and /var are aliases on macOS; exercise the scanner with physical paths.
    private readonly string _temporary = Path.Combine(OperatingSystem.IsMacOS() ? "/private/tmp" : Path.GetTempPath(), "businesschat-test-" + Guid.NewGuid().ToString("N"));
    private readonly string _source;
    private readonly IndexOptions _options;

    public RepositoryIndexerTests()
    {
        _source = Path.Combine(_temporary, "source");
        _options = new IndexOptions { OutputPath = Path.Combine(_temporary, "indexes"), IncludePaths = ["addons/sale"], MaxChunkCharacters = 20 };
        Directory.CreateDirectory(Path.Combine(_source, "addons/sale/models"));
        File.WriteAllText(Path.Combine(_source, "addons/sale/__manifest__.py"), "{'name': 'Sales', 'depends': ['base']}\n");
        File.WriteAllText(Path.Combine(_source, "addons/sale/models/order.py"), "class Order:\n    _name = 'sale.order'\n    def cancel(self):\n        self.state = 'cancel'\n");
        File.WriteAllText(Path.Combine(_source, "addons/sale/view.xml"), "<odoo/>");
        Git("init", "--template=", "-q");
        Git("add", ".");
        Git("-c", "user.name=Indexer Tests", "-c", "user.email=indexer@example.invalid", "commit", "-qm", "Fixture");
    }

    [Fact]
    public async Task IndexRecordsCommitModuleLinesHashesAndOversizedUnitsWithoutTruncation()
    {
        var result = await Build();
        using var manifest = JsonDocument.Parse(await File.ReadAllTextAsync(Path.Combine(result, "manifest.json")));
        var metadata = manifest.RootElement;
        Assert.Equal(Git("rev-parse", "HEAD").Trim(), metadata.GetProperty("gitCommit").GetString());
        Assert.Equal("test-v1", metadata.GetProperty("applicationVersion").GetString());
        Assert.False(metadata.GetProperty("workingTreeDirty").GetBoolean());
        Assert.Equal(2, metadata.GetProperty("files").GetArrayLength());
        Assert.True(metadata.GetProperty("oversizedChunkCount").GetInt32() > 0);
        Assert.Contains(metadata.GetProperty("skippedFiles").EnumerateArray(), s => s.GetProperty("relativePath").GetString()!.EndsWith("view.xml"));
        var chunks = await ReadChunks(result);
        var method = Assert.Single(chunks, c => c.Symbol == "cancel");
        Assert.Equal("sale", method.ModuleName);
        Assert.Equal("addons/sale", method.ModulePath);
        Assert.Equal(3, method.StartLine);
        Assert.Equal(4, method.EndLine);
        Assert.Contains("self.state = 'cancel'", method.Content);
        Assert.DoesNotContain(Directory.GetDirectories(_options.OutputPath), p => Path.GetFileName(p).StartsWith(".building-"));
    }

    [Fact]
    public async Task ReindexCreatesIndependentGenerationAndMarksDirtyChanges()
    {
        var first = await Build();
        var original = await File.ReadAllTextAsync(Path.Combine(first, "chunks.jsonl"));
        await File.AppendAllTextAsync(Path.Combine(_source, "addons/sale/models/order.py"), "\n# changed\n");
        var second = await Build();
        Assert.NotEqual(first, second);
        Assert.Equal(original, await File.ReadAllTextAsync(Path.Combine(first, "chunks.jsonl")));
        Assert.NotEqual(original, await File.ReadAllTextAsync(Path.Combine(second, "chunks.jsonl")));
        using var manifest = JsonDocument.Parse(await File.ReadAllTextAsync(Path.Combine(second, "manifest.json")));
        Assert.True(manifest.RootElement.GetProperty("workingTreeDirty").GetBoolean());
    }

    [Theory]
    [InlineData("../escape")]
    [InlineData("missing")]
    public async Task RejectsInvalidIncludes(string include)
    {
        _options.IncludePaths = [include];
        await Assert.ThrowsAsync<ArgumentException>(Build);
    }

    [Fact]
    public async Task RejectsOutputInsideSourceCheckout()
    {
        _options.OutputPath = Path.Combine(_source, "index");
        await Assert.ThrowsAsync<ArgumentException>(Build);
        Assert.False(Directory.Exists(_options.OutputPath));
    }

    [Fact]
    public async Task ReportsSizeSkipsAndDoesNotIndexUntrackedCode()
    {
        File.WriteAllText(Path.Combine(_source, "addons/sale/untracked.py"), "raise RuntimeError('must not execute')\n");
        _options.MaxFileBytes = 50;
        var result = await Build();
        var chunks = await ReadChunks(result);
        Assert.DoesNotContain(chunks, c => c.RelativePath.EndsWith("order.py") || c.RelativePath.EndsWith("untracked.py"));
        using var manifest = JsonDocument.Parse(await File.ReadAllTextAsync(Path.Combine(result, "manifest.json")));
        Assert.Contains(manifest.RootElement.GetProperty("skippedFiles").EnumerateArray(), s => s.GetProperty("reason").GetString() == "Exceeds MaxFileBytes");
    }

    [Fact]
    public async Task SkipsSymlinksWithoutReadingTheirTargets()
    {
        var outside = Path.Combine(_temporary, "outside.py");
        File.WriteAllText(outside, "SECRET_OUTSIDE_CHECKOUT");
        File.CreateSymbolicLink(Path.Combine(_source, "addons/sale/link.py"), outside);
        Git("add", ".");
        var result = await Build();
        Assert.DoesNotContain("SECRET_OUTSIDE_CHECKOUT", await File.ReadAllTextAsync(Path.Combine(result, "chunks.jsonl")));
        using var manifest = JsonDocument.Parse(await File.ReadAllTextAsync(Path.Combine(result, "manifest.json")));
        Assert.Contains(manifest.RootElement.GetProperty("skippedFiles").EnumerateArray(), s => s.GetProperty("reason").GetString() == "Symbolic link or linked ancestor");
    }

    [Fact]
    public async Task FailedAndCanceledRunsNeverLeaveCompletedOrPartialArtifacts()
    {
        var failing = new RepositoryIndexer([new FailingChunker()]);
        await Assert.ThrowsAsync<InvalidOperationException>(() => failing.BuildAsync(Repository(), _options));
        Assert.Empty(Directory.GetDirectories(_options.OutputPath));
        using var canceled = new CancellationTokenSource();
        canceled.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => new RepositoryIndexer([new PythonChunker()]).BuildAsync(Repository(), _options, canceled.Token));
        Assert.Empty(Directory.GetDirectories(_options.OutputPath));
    }

    private SourceRepositoryOptions Repository() => new() { Path = _source, ApplicationVersion = "test-v1" };
    private Task<string> Build() => new RepositoryIndexer([new PythonChunker()]).BuildAsync(Repository(), _options);
    private static async Task<CodeChunk[]> ReadChunks(string path) => (await File.ReadAllLinesAsync(Path.Combine(path, "chunks.jsonl")))
        .Select(l => JsonSerializer.Deserialize<CodeChunk>(l, new JsonSerializerOptions(JsonSerializerDefaults.Web))!).ToArray();

    private string Git(params string[] args)
    {
        var start = new ProcessStartInfo("git") { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
        foreach (var arg in new[] { "-c", "core.hooksPath=", "-c", "core.fsmonitor=false", "-C", _source }.Concat(args)) start.ArgumentList.Add(arg);
        using var process = Process.Start(start)!;
        var output = process.StandardOutput.ReadToEnd();
        var error = process.StandardError.ReadToEnd();
        process.WaitForExit();
        Assert.True(process.ExitCode == 0, error);
        return output;
    }

    public void Dispose() { if (Directory.Exists(_temporary)) Directory.Delete(_temporary, true); }
    private sealed class FailingChunker : ISourceChunker
    {
        public bool CanHandle(string relativePath) => true;
        public IReadOnlyList<CodeChunk> Chunk(SourceDocument document) => throw new InvalidOperationException("Deliberate test failure");
    }
}
