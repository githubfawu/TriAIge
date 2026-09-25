using System.Runtime.CompilerServices;
using TicketTriage.Core.Abstractions;
using TicketTriage.Core.Domain;

namespace TicketTriage.Batch.Tests;

/// <summary>Returns a canned suggestion per ticket; keys in <c>fallbackKeys</c> get a null draft like the real fallback.</summary>
internal sealed class FakeTriagePipeline(
    IEnumerable<string>? fallbackKeys = null,
    int? cancelAtIndex = null) : ITriagePipeline
{
    private readonly HashSet<string> _fallbackKeys = [.. fallbackKeys ?? []];

    public Task<TriageSuggestion> TriageAsync(Ticket ticket, CancellationToken cancellationToken) =>
        Task.FromResult(Suggest(ticket));

    public async IAsyncEnumerable<TriageSuggestion> TriageAsync(
        IAsyncEnumerable<Ticket> tickets,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        var index = 0;
        await foreach (var ticket in tickets.WithCancellation(cancellationToken))
        {
            if (index++ == cancelAtIndex)
            {
                throw new OperationCanceledException();
            }

            yield return Suggest(ticket);
        }
    }

    private TriageSuggestion Suggest(Ticket ticket) => new()
    {
        TicketKey = ticket.Key,
        WorkType = WorkType.ServiceRequest,
        AffectedServices = ["Service A"],
        ServiceTeams = ["Team A"],
        Assignee = "Jane Doe",
        Urgency = Urgency.High,
        Impact = Impact.NoImpact,
        ResolutionStatus = (ResolutionStatus)(ticket.Summary.Length % 4),
        DraftComment = _fallbackKeys.Contains(ticket.Key) ? null : "Draft.",
    };
}

internal sealed class TempDir : IDisposable
{
    public string Path { get; } = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "batch-tests-" + Guid.NewGuid().ToString("N"));

    public TempDir() => Directory.CreateDirectory(Path);

    public string Combine(string name) => System.IO.Path.Combine(Path, name);

    public void Dispose() => Directory.Delete(Path, recursive: true);
}

/// <summary>Cycles through all 25 (urgency, impact) pairs in input order.</summary>
internal sealed class CyclingPipeline : ITriagePipeline
{
    public Task<TriageSuggestion> TriageAsync(Ticket ticket, CancellationToken cancellationToken) =>
        throw new NotSupportedException();

    public async IAsyncEnumerable<TriageSuggestion> TriageAsync(
        IAsyncEnumerable<Ticket> tickets,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        var i = 0;
        await foreach (var ticket in tickets.WithCancellation(cancellationToken))
        {
            yield return new TriageSuggestion
            {
                TicketKey = ticket.Key,
                WorkType = WorkType.Incident,
                AffectedServices = ["Service A"],
                ServiceTeams = ["Team A"],
                Assignee = "Jane Doe",
                Urgency = (Urgency)(i / 5 % 5),
                Impact = (Impact)(i % 5),
                ResolutionStatus = (ResolutionStatus)(i % 4),
                DraftComment = "Draft.",
            };
            i++;
        }
    }
}

/// <summary>Yields the given number of results (or one extra-cancel hook) regardless of the input size.</summary>
internal sealed class ScriptedPipeline(int resultCount, Func<CancellationTokenSource?>? afterLast = null) : ITriagePipeline
{
    public Task<TriageSuggestion> TriageAsync(Ticket ticket, CancellationToken cancellationToken) =>
        throw new NotSupportedException();

    public async IAsyncEnumerable<TriageSuggestion> TriageAsync(
        IAsyncEnumerable<Ticket> tickets,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        var i = 0;
        await foreach (var ticket in tickets.WithCancellation(cancellationToken))
        {
            if (i++ >= resultCount)
            {
                break;
            }

            yield return new TriageSuggestion
            {
                TicketKey = ticket.Key,
                WorkType = WorkType.Incident,
                AffectedServices = [],
                ServiceTeams = [],
                Assignee = "x",
                Urgency = Urgency.Low,
                Impact = Impact.Minor,
                ResolutionStatus = ResolutionStatus.Done,
                DraftComment = "Draft.",
            };
        }

        if (afterLast?.Invoke() is { } cts)
        {
            await cts.CancelAsync();
        }
    }
}
