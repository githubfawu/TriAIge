using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage.ValueConversion;

namespace TicketTriage.Infrastructure.Persistence;

public sealed class TriageDbContext(DbContextOptions<TriageDbContext> options) : DbContext(options)
{
    public DbSet<TrainingTicketEntity> TrainingTickets => Set<TrainingTicketEntity>();

    public DbSet<TriageSuggestionEntity> TriageSuggestions => Set<TriageSuggestionEntity>();

    protected override void ConfigureConventions(ModelConfigurationBuilder configurationBuilder)
    {
        // SQLite cannot compare/order DateTimeOffset text; store as a sortable 64-bit value instead.
        configurationBuilder.Properties<DateTimeOffset>().HaveConversion<DateTimeOffsetToBinaryConverter>();
        configurationBuilder.Properties<DateTimeOffset?>().HaveConversion<DateTimeOffsetToBinaryConverter>();
    }

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<TrainingTicketEntity>(ticket =>
        {
            ticket.ToTable("TrainingTickets");
            ticket.HasKey(t => t.Key);
            ticket.Property(t => t.Key).HasMaxLength(64);
            ticket.Property(t => t.Summary).IsRequired();
            // List<string> properties are mapped as primitive collections, i.e. JSON text columns in SQLite.
            ticket.HasIndex(t => t.WorkType);
            ticket.HasIndex(t => t.Assignee);
        });

        modelBuilder.Entity<TriageSuggestionEntity>(suggestion =>
        {
            suggestion.ToTable("TriageSuggestions");
            suggestion.HasKey(s => s.Id);
            suggestion.Property(s => s.TicketKey).HasMaxLength(64).IsRequired();
            suggestion.Property(s => s.SuggestionJson).IsRequired();
            suggestion.Property(s => s.Decision).HasConversion<string>().HasMaxLength(16);
            suggestion.HasIndex(s => s.TicketKey);
            suggestion.HasIndex(s => s.Decision);
        });
    }
}
