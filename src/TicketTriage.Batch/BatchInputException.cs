namespace TicketTriage.Batch;

/// <summary>The input file is unreadable or does not contain a usable ticket array. The message never contains ticket text.</summary>
public sealed class BatchInputException(string message, Exception? innerException = null)
    : Exception(message, innerException);
