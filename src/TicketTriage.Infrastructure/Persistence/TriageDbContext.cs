using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace TicketTriage.Infrastructure.Persistence;

public sealed class TriageDbContext(DbContextOptions<TriageDbContext> options) : DbContext(options)
{
    public DbSet<TicketEntity> Tickets => Set<TicketEntity>();

    public DbSet<CommentEntity> Comments => Set<CommentEntity>();

    public DbSet<WorkTypeEntity> WorkTypes => Set<WorkTypeEntity>();

    public DbSet<PriorityEntity> Priorities => Set<PriorityEntity>();

    public DbSet<UrgencyEntity> Urgencies => Set<UrgencyEntity>();

    public DbSet<ImpactEntity> Impacts => Set<ImpactEntity>();

    public DbSet<ServiceTeamEntity> ServiceTeams => Set<ServiceTeamEntity>();

    public DbSet<AffectedBusinessOrITServiceEntity> AffectedBusinessOrITServices => Set<AffectedBusinessOrITServiceEntity>();

    public DbSet<BusinessEntityEntity> BusinessEntities => Set<BusinessEntityEntity>();

    public DbSet<StatusEntity> Statuses => Set<StatusEntity>();

    public DbSet<PriorityMappingEntity> PriorityMappings => Set<PriorityMappingEntity>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        ConfigureLookup(modelBuilder.Entity<WorkTypeEntity>(), "WorkType",
            (0, "Incident"), (1, "Service Request"));

        ConfigureLookup(modelBuilder.Entity<PriorityEntity>(), "Priority",
            (0, "Lowest"), (1, "Low"), (2, "Medium"), (3, "High"), (4, "Highest"));

        ConfigureLookup(modelBuilder.Entity<UrgencyEntity>(), "Urgency",
            (0, "Critical"), (1, "High"), (2, "Medium"), (3, "Low"), (4, "Lowest"));

        ConfigureLookup(modelBuilder.Entity<ImpactEntity>(), "Impact",
            (0, "Lowest"), (1, "Low"), (2, "Medium"), (3, "High"), (4, "Highest"));

        ConfigureLookup(modelBuilder.Entity<StatusEntity>(), "Status",
            (0, "New"), (1, "HumanRejected"), (2, "HumanApproved"), (3, "Finished"));

        ConfigureLookup(modelBuilder.Entity<ServiceTeamEntity>(), "ServiceTeams",
            (0, "Service Desk"), (1, "Enterprise Applications"), (2, "Investment Operations"),
            (3, "Affected Business or IT Services"), (4, "Securities Operations"), (5, "Risk & Controls"),
            (6, "Valuation & Pricing"), (7, "Client Services"), (8, "Market Data Services"),
            (9, "Trading Support"), (10, "Tax & Reporting"), (11, "Treasury & Cash"));

        ConfigureLookup(modelBuilder.Entity<AffectedBusinessOrITServiceEntity>(), "AffectedBusinessOrITServices",
            (0, "Emailed Support Tickets"), (1, "Outlook & Email"), (2, "Trade Matching"), (3, "Trading Platform"),
            (4, "Fund Pricing"), (5, "SimCorp Dimension"), (6, "Corporate Actions"), (7, "Securities Settlement"),
            (8, "Risk & Compliance Monitoring"), (9, "Portfolio Accounting"), (10, "CRM & Client Portal"),
            (11, "Regulatory Reporting"), (12, "SharePoint & File Storage"), (13, "Identity & Access Management"),
            (14, "Rimes Data Feed"), (15, "Client Reporting"), (16, "Order Management"), (17, "NAV Calculation"),
            (18, "Tax Reporting"), (19, "Cash Management"));

        ConfigureLookup(modelBuilder.Entity<BusinessEntityEntity>(), "BusinessEntity",
            (0, "Luxembourg"), (1, "Nordics"), (2, "Switzerland"), (3, "Germany"), (4, "France"));

        modelBuilder.Entity<PriorityMappingEntity>(mapping =>
        {
            mapping.ToTable("PriorityMapping");
            mapping.HasKey(m => m.Id);
            mapping.HasIndex(m => new { m.UrgencyId, m.ImpactId }).IsUnique();
            // Urgency x Impact -> Priority, mirrors Core.Domain.PriorityMatrix (kept in sync manually).
            mapping.HasData(
                new PriorityMappingEntity { Id = 1, UrgencyId = 0, ImpactId = 4, PriorityId = 4 },
                new PriorityMappingEntity { Id = 2, UrgencyId = 0, ImpactId = 3, PriorityId = 4 },
                new PriorityMappingEntity { Id = 3, UrgencyId = 0, ImpactId = 2, PriorityId = 3 },
                new PriorityMappingEntity { Id = 4, UrgencyId = 0, ImpactId = 1, PriorityId = 2 },
                new PriorityMappingEntity { Id = 5, UrgencyId = 0, ImpactId = 0, PriorityId = 2 },
                new PriorityMappingEntity { Id = 6, UrgencyId = 1, ImpactId = 4, PriorityId = 4 },
                new PriorityMappingEntity { Id = 7, UrgencyId = 1, ImpactId = 3, PriorityId = 3 },
                new PriorityMappingEntity { Id = 8, UrgencyId = 1, ImpactId = 2, PriorityId = 3 },
                new PriorityMappingEntity { Id = 9, UrgencyId = 1, ImpactId = 1, PriorityId = 2 },
                new PriorityMappingEntity { Id = 10, UrgencyId = 1, ImpactId = 0, PriorityId = 1 },
                new PriorityMappingEntity { Id = 11, UrgencyId = 2, ImpactId = 4, PriorityId = 3 },
                new PriorityMappingEntity { Id = 12, UrgencyId = 2, ImpactId = 3, PriorityId = 3 },
                new PriorityMappingEntity { Id = 13, UrgencyId = 2, ImpactId = 2, PriorityId = 2 },
                new PriorityMappingEntity { Id = 14, UrgencyId = 2, ImpactId = 1, PriorityId = 1 },
                new PriorityMappingEntity { Id = 15, UrgencyId = 2, ImpactId = 0, PriorityId = 1 },
                new PriorityMappingEntity { Id = 16, UrgencyId = 3, ImpactId = 4, PriorityId = 2 },
                new PriorityMappingEntity { Id = 17, UrgencyId = 3, ImpactId = 3, PriorityId = 2 },
                new PriorityMappingEntity { Id = 18, UrgencyId = 3, ImpactId = 2, PriorityId = 1 },
                new PriorityMappingEntity { Id = 19, UrgencyId = 3, ImpactId = 1, PriorityId = 1 },
                new PriorityMappingEntity { Id = 20, UrgencyId = 3, ImpactId = 0, PriorityId = 0 },
                new PriorityMappingEntity { Id = 21, UrgencyId = 4, ImpactId = 4, PriorityId = 2 },
                new PriorityMappingEntity { Id = 22, UrgencyId = 4, ImpactId = 3, PriorityId = 1 },
                new PriorityMappingEntity { Id = 23, UrgencyId = 4, ImpactId = 2, PriorityId = 1 },
                new PriorityMappingEntity { Id = 24, UrgencyId = 4, ImpactId = 1, PriorityId = 0 },
                new PriorityMappingEntity { Id = 25, UrgencyId = 4, ImpactId = 0, PriorityId = 0 });
        });

        modelBuilder.Entity<TicketEntity>(ticket =>
        {
            ticket.ToTable("Ticket");
            ticket.HasKey(t => t.Id);
            ticket.Property(t => t.Summary).HasMaxLength(250).IsRequired();
            ticket.Property(t => t.Description).HasMaxLength(1000);
            ticket.Property(t => t.Reporter).HasMaxLength(50);
            ticket.Property(t => t.Assignee).HasMaxLength(50);
            ticket.Property(t => t.AssigneeChanged).HasMaxLength(50);
            ticket.Property(t => t.Resolution).HasMaxLength(500);
            ticket.Property(t => t.ResolutionChanged).HasMaxLength(500);
            ticket.HasIndex(t => t.StatusId);
            ticket.HasIndex(t => t.CreatedDate);

            ticket.HasOne<WorkTypeEntity>().WithMany().HasForeignKey(t => t.WorkTypeId).OnDelete(DeleteBehavior.Restrict);
            ticket.HasOne<WorkTypeEntity>().WithMany().HasForeignKey(t => t.WorkTypeChangedId).OnDelete(DeleteBehavior.Restrict);

            ticket.HasOne<AffectedBusinessOrITServiceEntity>().WithMany().HasForeignKey(t => t.AffectedBusinessOrITServiceId).OnDelete(DeleteBehavior.Restrict);
            ticket.HasOne<AffectedBusinessOrITServiceEntity>().WithMany().HasForeignKey(t => t.AffectedBusinessOrITServiceChangedId).OnDelete(DeleteBehavior.Restrict);

            ticket.HasOne<BusinessEntityEntity>().WithMany().HasForeignKey(t => t.BusinessEntityId).OnDelete(DeleteBehavior.Restrict);
            ticket.HasOne<BusinessEntityEntity>().WithMany().HasForeignKey(t => t.BusinessEntityChangedId).OnDelete(DeleteBehavior.Restrict);

            ticket.HasOne<ServiceTeamEntity>().WithMany().HasForeignKey(t => t.ServiceTeamId).OnDelete(DeleteBehavior.Restrict);
            ticket.HasOne<ServiceTeamEntity>().WithMany().HasForeignKey(t => t.ServiceTeamChangedId).OnDelete(DeleteBehavior.Restrict);

            ticket.HasOne<PriorityEntity>().WithMany().HasForeignKey(t => t.PriorityId).OnDelete(DeleteBehavior.Restrict);
            ticket.HasOne<PriorityEntity>().WithMany().HasForeignKey(t => t.PriorityChangedId).OnDelete(DeleteBehavior.Restrict);

            ticket.HasOne<UrgencyEntity>().WithMany().HasForeignKey(t => t.UrgencyId).OnDelete(DeleteBehavior.Restrict);
            ticket.HasOne<UrgencyEntity>().WithMany().HasForeignKey(t => t.UrgencyChangedId).OnDelete(DeleteBehavior.Restrict);

            ticket.HasOne<ImpactEntity>().WithMany().HasForeignKey(t => t.ImpactId).OnDelete(DeleteBehavior.Restrict);
            ticket.HasOne<ImpactEntity>().WithMany().HasForeignKey(t => t.ImpactChangedId).OnDelete(DeleteBehavior.Restrict);

            ticket.HasOne<StatusEntity>().WithMany().HasForeignKey(t => t.StatusId).OnDelete(DeleteBehavior.Restrict);
            ticket.HasOne<StatusEntity>().WithMany().HasForeignKey(t => t.StatusChangedId).OnDelete(DeleteBehavior.Restrict);

            ticket.HasMany(t => t.Comments).WithOne().HasForeignKey(c => c.TicketId).OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<CommentEntity>(comment =>
        {
            comment.ToTable("Comments");
            comment.HasKey(c => c.CommentId);
            comment.Property(c => c.CommentText).HasMaxLength(500).IsRequired();
            comment.HasIndex(c => c.TicketId);
        });
    }

    private static void ConfigureLookup<TEntity>(
        EntityTypeBuilder<TEntity> builder,
        string tableName,
        params (int Id, string Name)[] rows)
        where TEntity : class, ILookupEntity, new()
    {
        builder.ToTable(tableName);
        builder.HasKey(e => e.Id);
        // Ids are fixed business values (0-based), not auto-generated; HasData requires ValueGeneratedNever for that.
        builder.Property(e => e.Id).ValueGeneratedNever();
        builder.Property(e => e.Name).HasMaxLength(100).IsRequired();
        builder.HasData(rows.Select(row => new TEntity { Id = row.Id, Name = row.Name }));
    }
}
