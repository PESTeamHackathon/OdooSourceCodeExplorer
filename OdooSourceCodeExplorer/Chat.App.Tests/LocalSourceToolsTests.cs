using Chat.Infrastructure.Exploration;

namespace Chat.App.Tests;

public sealed class LocalSourceToolsTests : IDisposable
{
    private readonly string _temporary = Path.Combine(OperatingSystem.IsMacOS() ? "/private/tmp" : Path.GetTempPath(), "exploration-tests-" + Guid.NewGuid().ToString("N"));
    private readonly string _root;

    public LocalSourceToolsTests()
    {
        _root = Path.Combine(_temporary, "odoo");
        Directory.CreateDirectory(Path.Combine(_root, "addons", "sale"));
        Directory.CreateDirectory(Path.Combine(_root, "addons", "stock"));
        File.WriteAllText(Path.Combine(_root, "addons", "sale", "order.py"), "class Order:\n    def cancel(self):\n        self.delivery.cancel()\n");
        File.WriteAllText(Path.Combine(_root, "addons", "stock", "delivery.py"), "class Delivery:\n    def cancel(self):\n        self.state = 'cancelled'\n");
        File.WriteAllText(Path.Combine(_root, ".env"), "cancel=private");
        File.WriteAllText(Path.Combine(_temporary, "outside.py"), "cancel = 'outside secret'");
    }

    [Fact]
    public async Task InvestigationCanSearchAndReadAcrossFilesWithInternalEvidence()
    {
        var source = new LocalSourceTools(_root);
        var search = await source.SearchAsync("def cancel");
        Assert.Equal(2, search.Hits.Count);
        Assert.Empty(source.Evidence); // Matches alone cannot support final claims.
        var order = await source.ReadAsync("addons/sale/order.py", 2, 2);
        var delivery = await source.ReadAsync("addons/stock/delivery.py", 2, 2);
        Assert.Equal("source-1", order.EvidenceId);
        Assert.Equal("source-2", delivery.EvidenceId);
        Assert.Contains("self.delivery.cancel()", order.Content);
        Assert.Contains("cancelled", delivery.Content);
        Assert.Equal(2, source.Evidence.Count);
        Assert.Equal(2, source.Evidence[0].Evidence.StartLine);
        Assert.Equal(3, source.Evidence[0].Evidence.EndLine);
    }

    [Theory]
    [InlineData("../outside.py")]
    [InlineData("addons/../../outside.py")]
    [InlineData("/etc/passwd")]
    [InlineData("addons\\sale\\order.py")]
    [InlineData(".env")]
    public async Task ReadsRejectEscapesAndFilesOutsideApprovedCatalogue(string path)
    {
        var source = new LocalSourceTools(_root);
        var result = await source.ReadAsync(path);
        Assert.Null(result.EvidenceId);
        Assert.Empty(result.Content);
        Assert.Empty(source.Evidence);
    }

    [Fact]
    public async Task SearchIsLiteralScopedAndCannotEscapeRoot()
    {
        var source = new LocalSourceTools(_root);
        var sale = await source.SearchAsync("cancel", "addons/sale");
        Assert.All(sale.Hits, h => Assert.StartsWith("addons/sale/", h.Path));
        Assert.Empty((await source.SearchAsync(".*")).Hits);
        Assert.Empty((await source.SearchAsync("cancel", "..")).Hits);
        Assert.DoesNotContain((await source.SearchAsync("cancel")).Hits, h => h.Snippet.Contains("secret") || h.Snippet.Contains("private"));
    }

    [Fact]
    public async Task LinksAreExcludedAndRecheckedAfterDiscovery()
    {
        File.CreateSymbolicLink(Path.Combine(_root, "linked.py"), Path.Combine(_temporary, "outside.py"));
        Directory.CreateSymbolicLink(Path.Combine(_root, "linked-directory"), _temporary);
        var source = new LocalSourceTools(_root);
        Assert.DoesNotContain((await source.SearchAsync("cancel")).Hits, h => h.Path.StartsWith("linked"));
        Assert.Null((await source.ReadAsync("linked.py")).EvidenceId);
        Assert.Null((await source.ReadAsync("linked-directory/outside.py")).EvidenceId);
        var original = Path.Combine(_root, "addons", "sale", "order.py");
        File.Delete(original);
        File.CreateSymbolicLink(original, Path.Combine(_temporary, "outside.py"));
        Assert.Null((await source.ReadAsync("addons/sale/order.py")).EvidenceId);
        Assert.Empty(source.Evidence);
    }

    [Fact]
    public async Task RejectsInvalidRangesAndDoesNotSilentlyClipLongLines()
    {
        File.WriteAllText(Path.Combine(_root, "long.py"), new string('x', 25_000));
        var source = new LocalSourceTools(_root);
        Assert.Null((await source.ReadAsync("addons/sale/order.py", 0)).EvidenceId);
        Assert.Null((await source.ReadAsync("addons/sale/order.py", 1, 201)).EvidenceId);
        Assert.Null((await source.ReadAsync("long.py")).EvidenceId);
        Assert.Empty(source.Evidence);
    }

    [Fact]
    public async Task ReadAndSearchShareCallBudgetAndRespectCancellation()
    {
        var source = new LocalSourceTools(_root);
        for (var i = 0; i < 40; i++) await source.ReadAsync("addons/sale/order.py");
        Assert.Contains("budget exhausted", (await source.SearchAsync("cancel")).Note);
        Assert.Single(source.Evidence); // Repeated reads consume operations but never duplicate evidence.
        using var stop = new CancellationTokenSource();
        var cancelled = new LocalSourceTools(_root, stop.Token);
        stop.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => cancelled.ReadAsync("addons/sale/order.py"));
        Assert.Empty(cancelled.Evidence);
    }


    [Fact]
    public async Task DiagnosticsShowDiscoveryInputsAndReadRangesWithoutSourceText()
    {
        var diagnostics = new List<string>();
        var source = new LocalSourceTools(_root, progress: diagnostics.Add);
        await source.SearchAsync("cancel", "addons/sale");
        await source.ReadAsync("addons/sale/order.py", 1, 3);
        Assert.Contains(diagnostics, d => d.Contains("term=\"cancel\"") && d.Contains("scope=\"addons/sale\""));
        Assert.Contains(diagnostics, d => d.Contains("order.py\":1-3"));
        Assert.DoesNotContain(diagnostics, d => d.Contains("self.delivery") || d.Contains("class Order"));
        Assert.Empty((await source.ReadAsync("addons/sale/order.py", 2, 1)).Content);
        Assert.Empty((await source.SearchAsync("CANCEL", "addons/sale")).Hits);
        Assert.Single(source.Evidence);
    }

    [Fact]
    public async Task OverlappingReadsCannotReregisterAlreadyCoveredLinesIncludingEndOfFile()
    {
        var source = new LocalSourceTools(_root);
        await source.ReadAsync("addons/sale/order.py", 1, 1);
        await source.ReadAsync("addons/sale/order.py", 2, 3);
        Assert.Null((await source.ReadAsync("addons/sale/order.py", 1, 4)).EvidenceId);
        Assert.Null((await source.ReadAsync("addons/sale/order.py", 1, 200)).EvidenceId);
        Assert.Equal(2, source.Evidence.Count);
    }

    public void Dispose() => Directory.Delete(_temporary, true);
}
