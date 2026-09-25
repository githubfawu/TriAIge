namespace TicketTriage.Infrastructure.Persistence;

/// <summary>Fixed ids of the seeded <c>Status</c> rows; pinned to the seed by a test.</summary>
internal static class TicketStatusIds
{
    public const int New = 0;
    public const int Reviewing = 1;
    public const int Reviewed = 2;
    public const int HumanRejected = 3;
    public const int HumanApproved = 4;
}

internal static class SystemMarkerNames
{
    public const string TrainingDataReady = "TrainingDataReady";
    public const string AnalysisWorkerHeartbeat = "AnalysisWorkerHeartbeat";
}
