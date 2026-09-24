using Microsoft.EntityFrameworkCore;
using TicketTriage.Infrastructure.Persistence;

namespace TicketTriage.Web.Triage;

/// <summary>
/// Loads every lookup table's Name&lt;-&gt;Id mapping once (seed data never changes at runtime) and exposes it
/// by name (Leitplanke 2: every FK id in this project comes from here, never a hand-picked ordinal).
/// Registered as a singleton; <see cref="EnsureLoadedAsync"/> is idempotent and safe to call from every method
/// that needs a lookup (first caller pays the one-time DB round trip).
/// </summary>
public sealed class LookupCatalog(IDbContextFactory<TriageDbContext> dbFactory)
{
    private static readonly IReadOnlyDictionary<string, int> EmptyByName =
        new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);

    private static readonly IReadOnlyDictionary<int, string> EmptyById = new Dictionary<int, string>();

    private readonly SemaphoreSlim _gate = new(1, 1);
    private volatile bool _loaded;

    private IReadOnlyDictionary<string, int> _workTypeIds = EmptyByName;
    private IReadOnlyDictionary<string, int> _urgencyIds = EmptyByName;
    private IReadOnlyDictionary<string, int> _impactIds = EmptyByName;
    private IReadOnlyDictionary<string, int> _priorityIds = EmptyByName;
    private IReadOnlyDictionary<string, int> _serviceTeamIds = EmptyByName;
    private IReadOnlyDictionary<string, int> _affectedServiceIds = EmptyByName;
    private IReadOnlyDictionary<string, int> _statusIds = EmptyByName;

    public IReadOnlyDictionary<int, string> WorkTypeNames { get; private set; } = EmptyById;

    public IReadOnlyDictionary<int, string> PriorityNames { get; private set; } = EmptyById;

    public int NewStatusId { get; private set; }

    public int HumanApprovedStatusId { get; private set; }

    public int HumanRejectedStatusId { get; private set; }

    public int FinishedStatusId { get; private set; }

    public async Task EnsureLoadedAsync(CancellationToken cancellationToken)
    {
        if (_loaded)
        {
            return;
        }

        await _gate.WaitAsync(cancellationToken);
        try
        {
            if (_loaded)
            {
                return;
            }

            await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);

            _workTypeIds = await LoadByNameAsync(db.WorkTypes, cancellationToken);
            _urgencyIds = await LoadByNameAsync(db.Urgencies, cancellationToken);
            _impactIds = await LoadByNameAsync(db.Impacts, cancellationToken);
            _priorityIds = await LoadByNameAsync(db.Priorities, cancellationToken);
            _serviceTeamIds = await LoadByNameAsync(db.ServiceTeams, cancellationToken);
            _affectedServiceIds = await LoadByNameAsync(db.AffectedBusinessOrITServices, cancellationToken);
            _statusIds = await LoadByNameAsync(db.Statuses, cancellationToken);

            WorkTypeNames = _workTypeIds.ToDictionary(pair => pair.Value, pair => pair.Key);
            PriorityNames = _priorityIds.ToDictionary(pair => pair.Value, pair => pair.Key);

            NewStatusId = RequireStatus("New");
            HumanApprovedStatusId = RequireStatus("HumanApproved");
            HumanRejectedStatusId = RequireStatus("HumanRejected");
            FinishedStatusId = RequireStatus("Finished");

            _loaded = true;
        }
        finally
        {
            _gate.Release();
        }
    }

    public int? FindWorkTypeId(string? name) => Find(_workTypeIds, name);

    public int? FindUrgencyId(string? name) => Find(_urgencyIds, name);

    public int? FindImpactId(string? name) => Find(_impactIds, name);

    public int? FindPriorityId(string? name) => Find(_priorityIds, name);

    public int? FindServiceTeamId(string? name) => Find(_serviceTeamIds, name);

    public int? FindAffectedServiceId(string? name) => Find(_affectedServiceIds, name);

    /// <summary>Fallback work type when the source ticket's value is missing or unknown (mirrors <c>TrainingDataImporter</c>).</summary>
    public int DefaultWorkTypeId => _workTypeIds["Incident"];

    private static int? Find(IReadOnlyDictionary<string, int> lookup, string? name) =>
        name is not null && lookup.TryGetValue(name, out var id) ? id : null;

    private int RequireStatus(string name) =>
        _statusIds.TryGetValue(name, out var id)
            ? id
            : throw new InvalidOperationException(
                $"Seed data is missing the '{name}' status; check TriageDbContext.OnModelCreating.");

    private static async Task<IReadOnlyDictionary<string, int>> LoadByNameAsync<TEntity>(
        IQueryable<TEntity> lookup, CancellationToken cancellationToken)
        where TEntity : class, ILookupEntity =>
        (await lookup.AsNoTracking().ToListAsync(cancellationToken))
            .ToDictionary(e => e.Name, e => e.Id, StringComparer.OrdinalIgnoreCase);
}
