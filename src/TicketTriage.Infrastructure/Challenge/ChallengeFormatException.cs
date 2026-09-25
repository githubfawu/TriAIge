namespace TicketTriage.Infrastructure.Challenge;

/// <summary>The challenge document is unreadable or not a usable ticket array. The message never contains ticket text or paths.</summary>
public sealed class ChallengeFormatException(string message, Exception? innerException = null)
    : Exception(message, innerException);
