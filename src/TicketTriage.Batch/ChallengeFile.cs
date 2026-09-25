using TicketTriage.Infrastructure.Challenge;

namespace TicketTriage.Batch;

/// <summary>Reads the challenge document from a path; failures become <see cref="BatchInputException"/> that name the file.</summary>
internal static class ChallengeFile
{
    public static async Task<ChallengeDocument> ReadAsync(string path, CancellationToken cancellationToken)
    {
        try
        {
            if (new FileInfo(path) is { Exists: true, Length: > ChallengeDocument.MaxFileBytes })
            {
                throw new BatchInputException(
                    $"Input file '{path}' is larger than the limit of {ChallengeDocument.MaxFileBytes} bytes.");
            }

            await using var stream = File.OpenRead(path);
            return await ChallengeDocument.ReadAsync(stream, cancellationToken);
        }
        catch (ChallengeFormatException ex)
        {
            throw new BatchInputException($"Input file '{path}': {ex.Message}", ex);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException)
        {
            throw new BatchInputException($"Cannot read input file '{path}' ({ex.GetType().Name}).", ex);
        }
    }
}
