using TicketTriage.Core.Domain;
using TicketTriage.Infrastructure.Routing;

namespace TicketTriage.Infrastructure.Pipeline;

internal sealed class TriageValidationException(IReadOnlyList<string> codes)
    : Exception("Suggestion failed validation: " + string.Join(',', codes))
{
    public IReadOnlyList<string> Codes { get; } = codes;
}

/// <summary>Checks a model-derived suggestion against Core rules and the routing statistics; throws <see cref="TriageValidationException"/> when unusable.</summary>
internal static class SuggestionValidator
{
    public const string InvalidWorkType = "InvalidWorkType";
    public const string InvalidUrgency = "InvalidUrgency";
    public const string InvalidImpact = "InvalidImpact";
    public const string NoAffectedServices = "NoAffectedServices";
    public const string UnknownService = "UnknownService";
    public const string MissingTeam = "MissingTeam";
    public const string InconsistentTeam = "InconsistentTeam";
    public const string MissingAssignee = "MissingAssignee";
    public const string InconsistentAssignee = "InconsistentAssignee";
    public const string PriorityMismatch = "PriorityMismatch";
    public const string InvalidResolutionStatus = "InvalidResolutionStatus";
    public const string EmptyComment = "EmptyComment";

    public static void Validate(TriageSuggestion suggestion, RoutingStatistics statistics)
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

        if (suggestion.AffectedServices.Any(n => !string.IsNullOrWhiteSpace(n) && ServiceCatalog.Find(n)?.Name != n))
        {
            codes.Add(UnknownService);
        }

        ValidateRouting(suggestion, statistics, codes);

        // Priority is a computed property today; this guards against a future stored or overridden value.
        if (urgencyValid && impactValid && suggestion.Priority != PriorityMatrix.Resolve(suggestion.Urgency, suggestion.Impact))
        {
            codes.Add(PriorityMismatch);
        }

        if (suggestion.ResolutionStatus is not { } status || !Enum.IsDefined(status))
        {
            codes.Add(InvalidResolutionStatus);
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

    // Team and assignee must be exactly what the statistics yield for the first service; an unknown service yields none.
    private static void ValidateRouting(TriageSuggestion suggestion, RoutingStatistics statistics, List<string> codes)
    {
        var team = string.Empty;
        string? assignee = null;
        var known = suggestion.AffectedServices.Count > 0
            && !string.IsNullOrWhiteSpace(suggestion.AffectedServices[0])
            && statistics.TryGetRoute(suggestion.AffectedServices[0], out team, out assignee);

        if (!known)
        {
            if (suggestion.ServiceTeams.Count > 0)
            {
                codes.Add(InconsistentTeam);
            }
        }
        else if (suggestion.ServiceTeams.Count == 0)
        {
            codes.Add(MissingTeam);
        }
        else if (suggestion.ServiceTeams.Count != 1 || !string.Equals(suggestion.ServiceTeams[0], team, StringComparison.Ordinal))
        {
            codes.Add(InconsistentTeam);
        }

        if (!string.Equals(suggestion.Assignee, assignee, StringComparison.Ordinal))
        {
            codes.Add(assignee is not null && string.IsNullOrWhiteSpace(suggestion.Assignee) ? MissingAssignee : InconsistentAssignee);
        }
    }
}
