using TicketTriage.Infrastructure.Challenge;

namespace TicketTriage.Web.Upload;

public sealed record IngestCounts(int Created, int Updated, int Unchanged, int Locked);

public sealed record UploadSession(
    ChallengeDocument Document,
    IReadOnlyList<int> TicketIds,
    IngestCounts Counts,
    IReadOnlyList<string> DuplicateKeys,
    DateTimeOffset StartedAt);

public enum WorkerStatus
{
    Alive,
    Starting,
    NotRunning,
}

public sealed record UploadProgress(int Total, int Analysed, int Pending, int Fallbacks, WorkerStatus Worker, bool TimedOut)
{
    public bool IsComplete => Pending == 0;
}

public sealed record UploadExport(IReadOnlyList<ChallengeResultRow> Rows, byte[] Json, int Fallbacks, int NotAnalysed);
