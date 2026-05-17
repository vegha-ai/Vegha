using Vegha.App.ViewModels;
using Vegha.Core.Requests;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Vegha.Tests.Unit.Core.ViewModels;

public class CookiesViewModelTests : IDisposable
{
    private readonly string _tempDb;

    public CookiesViewModelTests()
    {
        _tempDb = Path.Combine(Path.GetTempPath(), $"vegha-cookies-{Guid.NewGuid():N}.db");
    }

    public void Dispose()
    {
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        if (File.Exists(_tempDb)) try { File.Delete(_tempDb); } catch { }
    }

    private CookieJarStore NewStore() => new(_tempDb);

    private (CookiesViewModel Vm, CookieJarStore Store) NewVmWithStore()
    {
        var store = NewStore();
        return (new CookiesViewModel(store, NullLogger<CookiesViewModel>.Instance), store);
    }

    private CookiesViewModel NewVm() =>
        new(NewStore(), NullLogger<CookiesViewModel>.Instance);

    [Fact]
    public async Task AddCookie_ThenSetFields_PersistsViaUpsert()
    {
        var (vm, store) = NewVmWithStore();
        await vm.AddCookieCommand.ExecuteAsync(null);

        var row = vm.Items.Single();
        row.Domain = "example.test";
        row.Path = "/";
        row.Name = "session";
        row.Value = "abc123";

        // The OnRowChanged handler is async-void; give it a moment to flush to disk.
        await Task.Delay(250);

        // Read directly from the same store instance so we don't need to round-trip the
        // SQLite file (which can race with the async-void handler).
        var stored = store.GetAll().FirstOrDefault(c => c.Name == "session" && c.Domain == "example.test");
        stored.Should().NotBeNull();
        stored!.Value.Should().Be("abc123");
    }

    [Fact]
    public async Task DomainFilter_OnlyShowsRowsMatchingFilter()
    {
        var vm = NewVm();
        await vm.AddCookieCommand.ExecuteAsync(null);
        var row1 = vm.Items.Last();
        row1.Domain = "alpha.test"; row1.Name = "a"; row1.Value = "1";

        await vm.AddCookieCommand.ExecuteAsync(null);
        var row2 = vm.Items.Last();
        row2.Domain = "beta.test"; row2.Name = "b"; row2.Value = "2";

        await Task.Delay(150);
        vm.Refresh();
        vm.DomainFilter = "alpha";
        vm.Items.Select(r => r.Domain).Should().AllSatisfy(d => d.Should().Contain("alpha"));

        vm.DomainFilter = string.Empty;
        vm.Items.Should().HaveCountGreaterOrEqualTo(2);
    }

    [Fact]
    public async Task Delete_RemovesRowAndPersists()
    {
        var (vm, store) = NewVmWithStore();
        await vm.AddCookieCommand.ExecuteAsync(null);
        var row = vm.Items.Single();
        row.Domain = "deleteme.test"; row.Name = "x"; row.Value = "v";

        await Task.Delay(250);
        await vm.DeleteCommand.ExecuteAsync(row);

        // Verify against the live store instance.
        store.GetAll().Should().NotContain(c => c.Name == "x" && c.Domain == "deleteme.test");
    }
}
