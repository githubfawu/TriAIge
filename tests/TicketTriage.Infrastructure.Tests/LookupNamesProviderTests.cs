using Microsoft.EntityFrameworkCore;
using TicketTriage.Infrastructure.Persistence;
using TicketTriage.Infrastructure.Sources;

namespace TicketTriage.Infrastructure.Tests;

public class LookupNamesProviderTests
{
    [Fact]
    public async Task GetAsync_LoadsOnce_AndCachesInstance()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var db = await SqliteTestDatabase.CreateAsync(ct);
        var counting = new CountingFactory(db.Factory);
        using var provider = new LookupNamesProvider(counting);

        var first = await provider.GetAsync(ct);
        var second = await provider.GetAsync(ct);

        second.Should().BeSameAs(first);
        first.WorkTypes.Should().NotBeEmpty();
        counting.Creates.Should().Be(1);
    }

    [Fact]
    public async Task GetAsync_FactoryThrows_IsNotCached_NextCallRetries()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var db = await SqliteTestDatabase.CreateAsync(ct);
        var counting = new CountingFactory(db.Factory) { FailNext = true };
        using var provider = new LookupNamesProvider(counting);

        var act = async () => await provider.GetAsync(ct);
        await act.Should().ThrowAsync<InvalidOperationException>();

        (await provider.GetAsync(ct)).WorkTypes.Should().NotBeEmpty();
    }

    private sealed class CountingFactory(IDbContextFactory<TriageDbContext> inner) : IDbContextFactory<TriageDbContext>
    {
        public int Creates { get; private set; }

        public bool FailNext { get; set; }

        public TriageDbContext CreateDbContext() => throw new NotSupportedException();

        public Task<TriageDbContext> CreateDbContextAsync(CancellationToken cancellationToken = default)
        {
            if (FailNext)
            {
                FailNext = false;
                throw new InvalidOperationException("db down");
            }

            Creates++;
            return inner.CreateDbContextAsync(cancellationToken);
        }
    }
}
