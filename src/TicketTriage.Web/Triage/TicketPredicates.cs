using System.Linq.Expressions;
using TicketTriage.Infrastructure.Persistence;

namespace TicketTriage.Web.Triage;

/// <summary>
/// Reusable predicates over <see cref="TicketEntity"/>. "Has a suggestion" (Leitplanke 5) is the union of all
/// 11 "*Changed" columns being non-null, mirroring the pre-Slice-1 <c>Tickets.razor</c>/<c>Review.razor</c> pages.
/// The writer always sets <see cref="TicketEntity.WorkTypeChangedId"/>, so "has suggestion" always means "was analysed".
/// </summary>
public static class TicketPredicates
{
    public static Expression<Func<TicketEntity, bool>> HasSuggestion { get; } = t =>
        t.WorkTypeChangedId != null || t.AffectedBusinessOrITServiceChangedId != null
        || t.BusinessEntityChangedId != null || t.ServiceTeamChangedId != null || t.AssigneeChanged != null
        || t.PriorityChangedId != null || t.UrgencyChangedId != null || t.ImpactChangedId != null
        || t.StatusChangedId != null || t.ResolutionChanged != null || t.ResolutionDateChanged != null;

    public static Expression<Func<TicketEntity, bool>> HasNoSuggestion { get; } = Negate(HasSuggestion);

    /// <summary>Compiled delegate for in-memory filtering (small result sets already narrowed down in SQL).</summary>
    public static Func<TicketEntity, bool> HasSuggestionCompiled { get; } = HasSuggestion.Compile();

    private static Expression<Func<TicketEntity, bool>> Negate(Expression<Func<TicketEntity, bool>> expression) =>
        Expression.Lambda<Func<TicketEntity, bool>>(Expression.Not(expression.Body), expression.Parameters);
}
