using Microsoft.Extensions.Logging.Abstractions;
using TicketTriage.Core.Domain;
using TicketTriage.Web.Tests.Support;
using TicketTriage.Web.Triage;

namespace TicketTriage.Web.Tests;

public sealed class SessionMetricsTests
{
    private static Ticket MakeTicket(string key) => new() { Key = key, Summary = $"Summary for {key}" };

    private static TriageSessionStore CreateStore(ManualTimeProvider time) => new(NullLogger<TriageSessionStore>.Instance, time);

    [Fact]
    public void Compute_NoDecisions_AcceptanceRateIsNull_PerAC8()
    {
        var store = CreateStore(new ManualTimeProvider(DateTimeOffset.UtcNow));
        store.Register(1, "TT-1", MakeTicket("TT-1"), uploadId: 1);

        var metrics = SessionMetrics.Compute(store.Snapshot());

        metrics.ApprovedCount.Should().Be(0);
        metrics.RejectedCount.Should().Be(0);
        metrics.AcceptanceRate.Should().BeNull();
    }

    [Fact]
    public void Compute_AcceptWithZeroEdits_AcceptanceRateIsOne_PerAC8()
    {
        var store = CreateStore(new ManualTimeProvider(DateTimeOffset.UtcNow));
        store.Register(1, "TT-1", MakeTicket("TT-1"), uploadId: 1);

        store.RecordDecision(1, ReviewDecision.Approved, editedFields: [], rejectReason: null);

        var metrics = SessionMetrics.Compute(store.Snapshot());

        metrics.ApprovedCount.Should().Be(1);
        metrics.AcceptanceRate.Should().Be(1.0);
    }

    [Fact]
    public void Compute_SaveWithOneEditedField_LowersAcceptanceRate_AndCountsEditPerField_PerAC8()
    {
        var store = CreateStore(new ManualTimeProvider(DateTimeOffset.UtcNow));
        store.Register(1, "TT-1", MakeTicket("TT-1"), uploadId: 1);
        store.Register(2, "TT-2", MakeTicket("TT-2"), uploadId: 1);

        store.RecordDecision(1, ReviewDecision.Approved, editedFields: [], rejectReason: null);
        store.RecordDecision(2, ReviewDecision.Approved, editedFields: [ReviewField.Urgency], rejectReason: null);

        var metrics = SessionMetrics.Compute(store.Snapshot());

        metrics.ApprovedCount.Should().Be(2);
        metrics.AcceptanceRate.Should().Be(0.5);
        metrics.EditsPerField[ReviewField.Urgency].Should().Be(1);
        metrics.EditsPerField[ReviewField.Impact].Should().Be(0);
    }

    [Fact]
    public void Compute_MultipleEditsSameField_AccumulatesCount_PerAC8()
    {
        var store = CreateStore(new ManualTimeProvider(DateTimeOffset.UtcNow));
        store.Register(1, "TT-1", MakeTicket("TT-1"), uploadId: 1);
        store.Register(2, "TT-2", MakeTicket("TT-2"), uploadId: 1);

        store.RecordDecision(1, ReviewDecision.Approved, editedFields: [ReviewField.Urgency], rejectReason: null);
        store.RecordDecision(2, ReviewDecision.Approved, editedFields: [ReviewField.Urgency, ReviewField.Impact], rejectReason: null);

        var metrics = SessionMetrics.Compute(store.Snapshot());

        metrics.EditsPerField[ReviewField.Urgency].Should().Be(2);
        metrics.EditsPerField[ReviewField.Impact].Should().Be(1);
    }

    [Fact]
    public void Compute_RejectedTicket_ExcludedFromAcceptanceRateAndEdits_PerAC8()
    {
        var store = CreateStore(new ManualTimeProvider(DateTimeOffset.UtcNow));
        store.Register(1, "TT-1", MakeTicket("TT-1"), uploadId: 1);

        store.RecordDecision(1, ReviewDecision.Rejected, editedFields: [], rejectReason: "duplicate of TT-9");

        var metrics = SessionMetrics.Compute(store.Snapshot());

        metrics.ApprovedCount.Should().Be(0);
        metrics.RejectedCount.Should().Be(1);
        metrics.AcceptanceRate.Should().BeNull();
    }

    [Fact]
    public void Compute_UploadToFirstOpen_AveragesElapsedTime_PerAC13()
    {
        var time = new ManualTimeProvider(DateTimeOffset.UtcNow);
        var store = CreateStore(time);
        store.Register(1, "TT-1", MakeTicket("TT-1"), uploadId: 1);

        time.Advance(TimeSpan.FromSeconds(10));
        store.MarkOpened(1);

        var metrics = SessionMetrics.Compute(store.Snapshot());

        metrics.AverageUploadToFirstOpen.Should().Be(TimeSpan.FromSeconds(10));
    }

    [Fact]
    public void Compute_FirstOpenToDecision_AveragesElapsedTime_PerAC13()
    {
        var time = new ManualTimeProvider(DateTimeOffset.UtcNow);
        var store = CreateStore(time);
        store.Register(1, "TT-1", MakeTicket("TT-1"), uploadId: 1);
        store.MarkOpened(1);

        time.Advance(TimeSpan.FromSeconds(30));
        store.RecordDecision(1, ReviewDecision.Approved, editedFields: [], rejectReason: null);

        var metrics = SessionMetrics.Compute(store.Snapshot());

        metrics.AverageFirstOpenToDecision.Should().Be(TimeSpan.FromSeconds(30));
    }

    [Fact]
    public void Compute_NothingOpenedOrDecided_AveragesAreNull()
    {
        var metrics = SessionMetrics.Compute([]);

        metrics.AverageUploadToFirstOpen.Should().BeNull();
        metrics.AverageFirstOpenToDecision.Should().BeNull();
    }
}
