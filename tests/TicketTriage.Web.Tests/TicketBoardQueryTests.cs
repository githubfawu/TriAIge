using Microsoft.Extensions.Options;
using TicketTriage.Core.Domain;
using TicketTriage.Infrastructure.Persistence;
using TicketTriage.Infrastructure.Pipeline;
using TicketTriage.Web.Tests.Support;
using TicketTriage.Web.Triage;

namespace TicketTriage.Web.Tests;

public sealed class TicketBoardQueryTests : IAsyncLifetime
{
    private TestDatabase _database = null!;

    public async ValueTask InitializeAsync() => _database = await TestDatabase.CreateAsync(Xunit.TestContext.Current.CancellationToken);

    public async ValueTask DisposeAsync() => await _database.DisposeAsync();

    [Theory]
    [InlineData(0, null, 0, 3, TicketDisplayState.Queued)]
    [InlineData(0, "claimed", 0, 3, TicketDisplayState.Analysing)]
    [InlineData(0, null, 3, 3, TicketDisplayState.Failed)]
    [InlineData(1, null, 0, 3, TicketDisplayState.Pending)]
    [InlineData(2, null, 0, 3, TicketDisplayState.Pending)]
    [InlineData(3, null, 0, 3, TicketDisplayState.Rejected)]
    [InlineData(4, null, 0, 3, TicketDisplayState.Approved)]
    public void DeriveState_MapsStatusClaimAndRetries_PerFR11(int statusId, string? claimedMarker, int retries, int retryCount, TicketDisplayState expected)
    {
        var claimedAt = claimedMarker is null ? (DateTime?)null : DateTime.UtcNow;

        TicketBoardQuery.DeriveState(statusId, claimedAt, retries, retryCount).Should().Be(expected);
    }

    private TicketBoardQuery CreateQuery(int retryCount = 3) =>
        new(_database.CreateFactory(), Options.Create(new TriageOptions { RetryCount = retryCount }));

    private static TicketEntity NewTicket(string summary, TicketOrigin origin, int statusId, DateTime? claimedAt = null, int retries = 0, long version = 0) => new()
    {
        Summary = summary,
        WorkTypeId = 0,
        CreatedDate = DateTime.UtcNow,
        Origin = origin,
        StatusId = statusId,
        ClaimedAt = claimedAt,
        Retries = retries,
        Version = version,
        SourceKey = origin == TicketOrigin.Training ? null : Guid.NewGuid().ToString(),
    };

    [Fact]
    public async Task GetRowsAsync_ExcludesTrainingTickets_AndDerivesEachState_PerFR13()
    {
        var ct = Xunit.TestContext.Current.CancellationToken;
        await using (var db = _database.CreateContext())
        {
            db.Tickets.AddRange(
                NewTicket("training ticket", TicketOrigin.Training, statusId: 0),
                NewTicket("queued", TicketOrigin.Challenge, statusId: 0),
                NewTicket("failed", TicketOrigin.Challenge, statusId: 0, retries: 3));
            await db.SaveChangesAsync(ct);

            var failed = db.Tickets.Single(t => t.Summary == "failed");
            db.TriageFailures.Add(new TriageFailureEntity
            {
                TicketId = failed.Id,
                TicketKey = "DB-" + failed.Id,
                Attempt = 3,
                Reason = "boom",
                ExceptionType = "InvalidOperationException",
                OccurredAtUtc = DateTime.UtcNow,
            });
            await db.SaveChangesAsync(ct);
        }

        var rows = await CreateQuery().GetRowsAsync(ct);

        rows.Select(r => r.Summary).Should().BeEquivalentTo(["queued", "failed"]);
        rows.Single(r => r.Summary == "queued").State.Should().Be(TicketDisplayState.Queued);
        var failedRow = rows.Single(r => r.Summary == "failed");
        failedRow.State.Should().Be(TicketDisplayState.Failed);
        failedRow.FailureReason.Should().Be("boom");
    }

    [Fact]
    public async Task GetNextPendingIdAsync_FindsAnotherReviewingOrReviewedTicket_ExcludingSelf_PerFR19()
    {
        var ct = Xunit.TestContext.Current.CancellationToken;
        int excludeId, pendingId;
        await using (var db = _database.CreateContext())
        {
            var exclude = NewTicket("self", TicketOrigin.Challenge, statusId: 1);
            var pending = NewTicket("other", TicketOrigin.Challenge, statusId: 2);
            db.Tickets.AddRange(exclude, pending);
            await db.SaveChangesAsync(ct);
            excludeId = exclude.Id;
            pendingId = pending.Id;
        }

        var query = CreateQuery();
        (await query.GetNextPendingIdAsync(excludeId, ct)).Should().Be(pendingId);
        (await query.GetNextPendingIdAsync(pendingId, ct)).Should().Be(excludeId);
    }

    [Fact]
    public async Task GetLatestFailureReasonAsync_ReturnsMostRecentReason()
    {
        var ct = Xunit.TestContext.Current.CancellationToken;
        int ticketId;
        await using (var db = _database.CreateContext())
        {
            var ticket = NewTicket("failed twice", TicketOrigin.Challenge, statusId: 0, retries: 3);
            db.Tickets.Add(ticket);
            await db.SaveChangesAsync(ct);
            ticketId = ticket.Id;

            db.TriageFailures.AddRange(
                new TriageFailureEntity { TicketId = ticketId, TicketKey = "DB-" + ticketId, Attempt = 1, Reason = "first", ExceptionType = "X", OccurredAtUtc = DateTime.UtcNow.AddMinutes(-5) },
                new TriageFailureEntity { TicketId = ticketId, TicketKey = "DB-" + ticketId, Attempt = 2, Reason = "second", ExceptionType = "X", OccurredAtUtc = DateTime.UtcNow });
            await db.SaveChangesAsync(ct);
        }

        var reason = await CreateQuery().GetLatestFailureReasonAsync(ticketId, ct);

        reason.Should().Be("second");
    }
}
