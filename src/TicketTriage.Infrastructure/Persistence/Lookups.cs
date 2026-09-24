namespace TicketTriage.Infrastructure.Persistence;

// Plain ID/Name reference tables (seeded via HasData). Independent of TicketTriage.Core enums:
// these back the Ticket table's FK columns and are not guaranteed to share ordinals with Core.Domain enums.

// No "required" on Name: instances are also constructed generically by TriageDbContext's seed helper.
public interface ILookupEntity
{
    int Id { get; set; }

    string Name { get; set; }
}

public sealed class WorkTypeEntity : ILookupEntity
{
    public int Id { get; set; }

    public string Name { get; set; } = "";
}

public sealed class PriorityEntity : ILookupEntity
{
    public int Id { get; set; }

    public string Name { get; set; } = "";
}

public sealed class UrgencyEntity : ILookupEntity
{
    public int Id { get; set; }

    public string Name { get; set; } = "";
}

public sealed class ImpactEntity : ILookupEntity
{
    public int Id { get; set; }

    public string Name { get; set; } = "";
}

public sealed class ServiceTeamEntity : ILookupEntity
{
    public int Id { get; set; }

    public string Name { get; set; } = "";
}

public sealed class AffectedBusinessOrITServiceEntity : ILookupEntity
{
    public int Id { get; set; }

    public string Name { get; set; } = "";
}

public sealed class BusinessEntityEntity : ILookupEntity
{
    public int Id { get; set; }

    public string Name { get; set; } = "";
}

public sealed class StatusEntity : ILookupEntity
{
    public int Id { get; set; }

    public string Name { get; set; } = "";
}
