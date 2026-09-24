using TicketTriage.Core.Domain;

namespace TicketTriage.Infrastructure.Pipeline;

internal sealed class TriageValidationException(IReadOnlyList<string> codes)
    : Exception("Suggestion failed validation: " + string.Join(',', codes))
{
    public IReadOnlyList<string> Codes { get; } = codes;
}

/// <summary>Checks a model-derived suggestion against Core rules; throws <see cref="TriageValidationException"/> when unusable.</summary>
internal static class SuggestionValidator
{
    public const string InvalidWorkType = "InvalidWorkType";
    public const string InvalidUrgency = "InvalidUrgency";
    public const string InvalidImpact = "InvalidImpact";
    public const string NoAffectedServices = "NoAffectedServices";
    public const string EmptyComment = "EmptyComment";

    public static void Validate(TriageSuggestion suggestion)
    {
        List<string> codes = [];

        if (!Enum.IsDefined(suggestion.WorkType))
        {
            codes.Add(InvalidWorkType);
        }

        var urgencyValid = Enum.IsDefined(suggestion.Urgency);
        if (!urgencyValid)
        {
            codes.Add(InvalidUrgency);
        }

        var impactValid = Enum.IsDefined(suggestion.Impact);
        if (!impactValid)
        {
            codes.Add(InvalidImpact);
        }

        if (suggestion.AffectedServices.Count == 0 || suggestion.AffectedServices.Any(string.IsNullOrWhiteSpace))
        {
            codes.Add(NoAffectedServices);
        }

        if (string.IsNullOrWhiteSpace(suggestion.DraftComment))
        {
            codes.Add(EmptyComment);
        }

        if (codes.Count > 0)
        {
            throw new TriageValidationException(codes);
        }
    }
}
