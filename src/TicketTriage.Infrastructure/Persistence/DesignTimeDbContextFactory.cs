using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace TicketTriage.Infrastructure.Persistence;

/// <summary>Used only by <c>dotnet ef</c> to create migrations without starting the web app.</summary>
internal sealed class DesignTimeDbContextFactory : IDesignTimeDbContextFactory<TriageDbContext>
{
    public TriageDbContext CreateDbContext(string[] args) =>
        new(new DbContextOptionsBuilder<TriageDbContext>().UseSqlite("Data Source=design-time.db").Options);
}
