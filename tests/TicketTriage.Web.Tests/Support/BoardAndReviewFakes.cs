using TicketTriage.Core.Abstractions;
using TicketTriage.Core.Domain;
using TicketTriage.Web.Triage;

namespace TicketTriage.Web.Tests.Support;

internal sealed class FakeTicketBoardQuery : ITicketBoardQuery
{
    public IReadOnlyList<TicketBoardRow> Rows { get; set; } = [];

    public int? NextPendingId { get; set; }

    public string? FailureReason { get; set; }

    public Task<IReadOnlyList<TicketBoardRow>> GetRowsAsync(CancellationToken cancellationToken) => Task.FromResult(Rows);

    public Task<int?> GetNextPendingIdAsync(int excludeId, CancellationToken cancellationToken) => Task.FromResult(NextPendingId);

    public Task<string?> GetLatestFailureReasonAsync(int ticketId, CancellationToken cancellationToken) => Task.FromResult(FailureReason);
}

/// <summary>Test double for <see cref="IReviewService"/> (never a real DB in page unit tests): returns a scripted
/// <see cref="Review"/> and records every decision call so tests can assert what the page sent.</summary>
internal sealed class FakeReviewService : IReviewService
{
    public TicketReview? Review { get; set; }

    public ReviewResult NextResult { get; set; } = new(ReviewOutcome.Success, 2, null);

    public List<(int TicketId, ReviewEdits? Edits)> ApproveCalls { get; } = [];

    public List<(int TicketId, string Reason)> RejectCalls { get; } = [];

    public List<int> RequeueCalls { get; } = [];

    public Task<TicketReview?> OpenAsync(int ticketId, CancellationToken cancellationToken) => Task.FromResult(Review);

    public Task<ReviewResult> SaveEditsAsync(int ticketId, long expectedVersion, ReviewEdits edits, CancellationToken cancellationToken) =>
        Task.FromResult(NextResult);

    public Task<ReviewResult> ApproveAsync(int ticketId, long expectedVersion, ReviewEdits? edits, CancellationToken cancellationToken)
    {
        ApproveCalls.Add((ticketId, edits));
        return Task.FromResult(NextResult);
    }

    public Task<ReviewResult> RejectAsync(int ticketId, long expectedVersion, string reason, CancellationToken cancellationToken)
    {
        RejectCalls.Add((ticketId, reason));
        return Task.FromResult(NextResult);
    }

    public Task<ReviewResult> RequeueFailedAsync(int ticketId, long expectedVersion, CancellationToken cancellationToken)
    {
        RequeueCalls.Add(ticketId);
        return Task.FromResult(NextResult);
    }
}

internal sealed class FakeTriageMetricsService : ITriageMetricsService
{
    public TriageMetrics Metrics { get; set; } = new(0, 0, 0, 0, 0, null, new Dictionary<SuggestionField, int>(), null, null);

    public Task<TriageMetrics> GetAsync(CancellationToken cancellationToken) => Task.FromResult(Metrics);
}
