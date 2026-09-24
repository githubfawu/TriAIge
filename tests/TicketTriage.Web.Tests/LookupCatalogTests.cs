using TicketTriage.Infrastructure.Persistence;
using TicketTriage.Web.Tests.Support;
using TicketTriage.Web.Triage;

namespace TicketTriage.Web.Tests;

/// <summary>Covers the "Finished/Reviewing/Reviewed are optional" contract (CLAUDE.md task): the current seed
/// data (New, Reviewing, Reviewed, HumanRejected, HumanApproved) and an old local DB that still carries
/// "Finished" must both load without throwing, and only New/HumanApproved/HumanRejected are ever required.</summary>
public sealed class LookupCatalogTests : IAsyncLifetime
{
    private TestDatabase _database = null!;

    public async ValueTask InitializeAsync() => _database = await TestDatabase.CreateAsync(Xunit.TestContext.Current.CancellationToken);

    public async ValueTask DisposeAsync() => await _database.DisposeAsync();

    [Fact]
    public async Task EnsureLoadedAsync_CurrentSeed_ResolvesRequiredStatuses_FinishedIsNull()
    {
        var cancellationToken = Xunit.TestContext.Current.CancellationToken;
        var catalog = new LookupCatalog(_database.CreateFactory());

        await catalog.EnsureLoadedAsync(cancellationToken);

        catalog.FinishedStatusId.Should().BeNull();
        catalog.ReviewingStatusId.Should().NotBeNull();
        catalog.ReviewedStatusId.Should().NotBeNull();
        catalog.NonTerminalStatusIds.Should().Contain([catalog.NewStatusId, catalog.ReviewingStatusId!.Value, catalog.ReviewedStatusId!.Value]);
    }

    [Fact]
    public async Task EnsureLoadedAsync_LegacyDbWithFinished_ResolvesFinishedStatusId()
    {
        var cancellationToken = Xunit.TestContext.Current.CancellationToken;
        await _database.AddLegacyFinishedStatusAsync(cancellationToken);
        var catalog = new LookupCatalog(_database.CreateFactory());

        await catalog.EnsureLoadedAsync(cancellationToken);

        catalog.FinishedStatusId.Should().NotBeNull();
        catalog.IsFinished(catalog.FinishedStatusId!.Value).Should().BeTrue();
        catalog.IsFinished(catalog.NewStatusId).Should().BeFalse();
    }

    [Fact]
    public async Task IsDecided_OnlyHumanApprovedOrRejected_PerLifecycleRule()
    {
        var cancellationToken = Xunit.TestContext.Current.CancellationToken;
        var catalog = new LookupCatalog(_database.CreateFactory());
        await catalog.EnsureLoadedAsync(cancellationToken);

        catalog.IsDecided(catalog.HumanApprovedStatusId).Should().BeTrue();
        catalog.IsDecided(catalog.HumanRejectedStatusId).Should().BeTrue();
        catalog.IsDecided(catalog.NewStatusId).Should().BeFalse();
        catalog.IsDecided(catalog.ReviewedStatusId!.Value).Should().BeFalse();
    }
}
