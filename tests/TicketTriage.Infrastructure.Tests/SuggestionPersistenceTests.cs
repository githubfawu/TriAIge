using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using TicketTriage.Core.Domain;
using TicketTriage.Infrastructure.Persistence;

namespace TicketTriage.Infrastructure.Tests;

public class SuggestionPersistenceTests
{
    private static readonly DateTime Now = new(2026, 9, 25, 10, 0, 0, DateTimeKind.Utc);

    private static TriageSuggestion Suggestion(string? comment = "Restart the service.", ResolutionStatus? status = ResolutionStatus.Done) => new()
    {
        TicketKey = "DB-7",
        WorkType = WorkType.ServiceRequest,
        AffectedServices = ["Fund Pricing", "Rimes Data Feed"],
        ServiceTeams = ["Valuation & Pricing"],
        Assignee = "jdoe",
        Urgency = Urgency.Medium,
        Impact = Impact.Significant,
        ResolutionStatus = status,
        DraftComment = comment,
        SimilarTicketKeys = ["DB-1", "DB-2"],
    };

    [Fact]
    public async Task RoundTrip_PreservesListsAndFields_PerAC3()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var database = await SqliteTestDatabase.CreateAsync(ct);
        await database.AddTicketAsync(7, "desc", cancellationToken: ct);
        var original = Suggestion();

        await using (var db = await database.Factory.CreateDbContextAsync(ct))
        {
            db.Suggestions.Add(SuggestionMapper.ToEntity(7, original, Now));
            await db.SaveChangesAsync(ct);
        }

        await using var read = await database.Factory.CreateDbContextAsync(ct);
        var entity = await read.Suggestions.AsNoTracking().SingleAsync(ct);
        var restored = SuggestionMapper.ToDomain(entity);

        restored.Should().BeEquivalentTo(original);
        restored.TicketKey.Should().Be("DB-7");
        entity.Decision.Should().Be(ReviewDecision.Pending);
        entity.IsFallback.Should().BeFalse();
    }

    [Fact]
    public async Task RoundTrip_NullStatusAndFallback_PerAC3()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var database = await SqliteTestDatabase.CreateAsync(ct);
        await database.AddTicketAsync(7, "desc", cancellationToken: ct);

        await using (var db = await database.Factory.CreateDbContextAsync(ct))
        {
            db.Suggestions.Add(SuggestionMapper.ToEntity(7, Suggestion(comment: null, status: null) with { AffectedServices = [], ServiceTeams = [] }, Now));
            await db.SaveChangesAsync(ct);
        }

        await using var read = await database.Factory.CreateDbContextAsync(ct);
        var entity = await read.Suggestions.AsNoTracking().SingleAsync(ct);

        entity.ResolutionStatus.Should().BeNull();
        entity.DraftComment.Should().BeNull();
        entity.IsFallback.Should().BeTrue();
        entity.AffectedServices.Should().BeEmpty();
        SuggestionMapper.ToDomain(entity).IsFallback.Should().BeTrue();
    }

    [Fact]
    public void ToEntity_StoresPriorityFromMatrix_PerAC3()
    {
        foreach (var urgency in Enum.GetValues<Urgency>())
        {
            foreach (var impact in Enum.GetValues<Impact>())
            {
                var entity = SuggestionMapper.ToEntity(1, Suggestion() with { Urgency = urgency, Impact = impact }, Now);

                entity.Priority.Should().Be(PriorityMatrix.Resolve(urgency, impact));
            }
        }
    }

    [Fact]
    public async Task DeletingTicket_CascadesToSuggestionAndEdits_PerAC3()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var database = await SqliteTestDatabase.CreateAsync(ct);
        await database.AddTicketAsync(7, "desc", cancellationToken: ct);

        await using (var db = await database.Factory.CreateDbContextAsync(ct))
        {
            db.Suggestions.Add(SuggestionMapper.ToEntity(7, Suggestion(), Now));
            db.SuggestionEdits.Add(new SuggestionEditEntity { TicketId = 7, Field = nameof(SuggestionField.Assignee), AiValue = "a", FinalValue = "b", EditedAtUtc = Now });
            await db.SaveChangesAsync(ct);
        }

        await using (var db = await database.Factory.CreateDbContextAsync(ct))
        {
            await db.Tickets.Where(t => t.Id == 7).ExecuteDeleteAsync(ct);
        }

        await using var read = await database.Factory.CreateDbContextAsync(ct);
        (await read.Suggestions.CountAsync(ct)).Should().Be(0);
        (await read.SuggestionEdits.CountAsync(ct)).Should().Be(0);
    }

    [Fact]
    public async Task SuggestionForUnknownTicket_IsRejected_PerAC3()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var database = await SqliteTestDatabase.CreateAsync(ct);
        await using var db = await database.Factory.CreateDbContextAsync(ct);
        db.Suggestions.Add(SuggestionMapper.ToEntity(99, Suggestion(), Now));

        var act = () => db.SaveChangesAsync(ct);

        await act.Should().ThrowAsync<DbUpdateException>();
    }

    [Fact]
    public async Task SuggestionEdit_DuplicateTicketAndField_ViolatesUniqueIndex_PerAC3()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var database = await SqliteTestDatabase.CreateAsync(ct);
        await database.AddTicketAsync(7, "desc", cancellationToken: ct);
        await using var db = await database.Factory.CreateDbContextAsync(ct);
        db.SuggestionEdits.Add(new SuggestionEditEntity { TicketId = 7, Field = "Assignee", AiValue = "a", FinalValue = "b", EditedAtUtc = Now });
        db.SuggestionEdits.Add(new SuggestionEditEntity { TicketId = 7, Field = "Urgency", AiValue = "High", FinalValue = "Low", EditedAtUtc = Now });
        await db.SaveChangesAsync(ct);

        db.SuggestionEdits.Add(new SuggestionEditEntity { TicketId = 7, Field = "Assignee", AiValue = "a", FinalValue = "c", EditedAtUtc = Now });
        var act = () => db.SaveChangesAsync(ct);

        (await act.Should().ThrowAsync<DbUpdateException>()).WithInnerException<SqliteException>();
    }

    [Fact]
    [Trait("Category", "Smoke")]
    public async Task Smoke_PersistAndReloadSuggestion()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var database = await SqliteTestDatabase.CreateAsync(ct);
        await database.AddTicketAsync(7, "Outlook does not start", cancellationToken: ct);

        await using (var db = await database.Factory.CreateDbContextAsync(ct))
        {
            db.Suggestions.Add(SuggestionMapper.ToEntity(7, Suggestion(), Now));
            await db.SaveChangesAsync(ct);
        }

        await using var read = await database.Factory.CreateDbContextAsync(ct);
        var restored = SuggestionMapper.ToDomain(await read.Suggestions.SingleAsync(ct));

        restored.Priority.Should().Be(PriorityMatrix.Resolve(Urgency.Medium, Impact.Significant));
        restored.AffectedServices.Should().HaveCount(2);
    }
}
