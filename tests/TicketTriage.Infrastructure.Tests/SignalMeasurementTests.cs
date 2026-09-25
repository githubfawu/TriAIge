using System.Text.Json;
using TicketTriage.Core.Domain;
using TicketTriage.Infrastructure.Retrieval;

namespace TicketTriage.Infrastructure.Tests;

/// <summary>
/// FR9 measurement (not a regression test): is resolution status / assignee predictable from ticket text on the real
/// training file? Results go to the test output; only sanity assertions guard the run.
/// </summary>
public class SignalMeasurementTests
{
    private const int Neighbours = 10;
    private const int HoldOutModulus = 5;

    [Fact]
    [Trait("Category", "Integration")]
    public async Task MeasureStatusAndAssigneeSignal_ReportsAccuracyVersusBaselines_PerAC7()
    {
        var path = FindTrainingFile();
        Assert.SkipUnless(path is not null, "Real training file not present (data/ or TRIAGE_TRAINING_FILE).");
        var ct = TestContext.Current.CancellationToken;
        var output = TestContext.Current.TestOutputHelper!;

        await using var stream = File.OpenRead(path!);
        var all = await JsonSerializer.DeserializeAsync<List<Ticket>>(stream, cancellationToken: ct) ?? [];

        // Row number in the file doubles as the corpus id; index % 5 == 0 of the resolved tickets is held out.
        var resolved = all
            .Select((ticket, row) => (Ticket: ticket, Row: row))
            .Where(x => !string.IsNullOrWhiteSpace(x.Ticket.Resolution) && !string.IsNullOrWhiteSpace(x.Ticket.Description))
            .ToList();
        var train = resolved.Where((_, i) => i % HoldOutModulus != 0).ToList();
        var test = resolved.Where((_, i) => i % HoldOutModulus == 0).ToList();
        var byRow = resolved.ToDictionary(x => x.Row, x => x.Ticket);

        var index = TfIdfIndex.Build([.. train.Select(x => new CorpusDocument(x.Row, x.Ticket.Description!))]);

        static string Status(Ticket t) => t.Resolution!.Trim().ToLowerInvariant();
        static string? Service(Ticket t) => t.AffectedServices.FirstOrDefault();

        var classes = train.Select(x => Status(x.Ticket)).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToList();
        var majorityStatus = Majority(train.Select(x => Status(x.Ticket)))!;
        var statusByWorkType = train
            .GroupBy(x => x.Ticket.WorkType ?? "")
            .ToDictionary(g => g.Key, g => Majority(g.Select(x => Status(x.Ticket)))!);
        var assigneeByService = train
            .Where(x => Service(x.Ticket) is not null && x.Ticket.Assignee is not null)
            .GroupBy(x => Service(x.Ticket)!)
            .ToDictionary(g => g.Key, g => Majority(g.Select(x => x.Ticket.Assignee!))!);
        var assignees = train.Select(x => x.Ticket.Assignee).Where(a => a is not null).Distinct().Count();

        int knnStatus = 0, majorityClass = 0, workTypeMajority = 0;
        int knnAssignee = 0, serviceAssignee = 0, assigneeN = 0;
        foreach (var (ticket, _) in test)
        {
            var hits = index.Search(ticket.Description, Neighbours, excludeId: null);

            if (VoteByScore(hits.Select(h => (Status(byRow[h.Id]), h.Score))) == Status(ticket))
            {
                knnStatus++;
            }

            if (majorityStatus == Status(ticket))
            {
                majorityClass++;
            }

            if (statusByWorkType.GetValueOrDefault(ticket.WorkType ?? "") == Status(ticket))
            {
                workTypeMajority++;
            }

            if (ticket.Assignee is null)
            {
                continue;
            }

            assigneeN++;
            var neighbourAssignee = VoteByScore(hits
                .Where(h => byRow[h.Id].Assignee is not null)
                .Select(h => (byRow[h.Id].Assignee!, h.Score)));
            if (neighbourAssignee == ticket.Assignee)
            {
                knnAssignee++;
            }

            if (Service(ticket) is { } service && assigneeByService.GetValueOrDefault(service) == ticket.Assignee)
            {
                serviceAssignee++;
            }
        }

        var uniform = 1.0 / classes.Count;
        var n = test.Count;
        output.WriteLine($"Resolved tickets with description: {resolved.Count}; train {train.Count}, held-out n={n}; status classes: {string.Join(", ", classes)}");
        output.WriteLine($"STATUS uniform baseline = {uniform:P1}");
        Report(output, "STATUS kNN top-10 majority", knnStatus, n, uniform);
        Report(output, $"STATUS majority-class baseline ('{majorityStatus}')", majorityClass, n, uniform);
        Report(output, "STATUS work-type-majority baseline", workTypeMajority, n, uniform);
        output.WriteLine($"ASSIGNEE chance = 1/{assignees} = {1.0 / assignees:P1}; held-out with assignee n={assigneeN}");
        Report(output, "ASSIGNEE service-majority (routing)", serviceAssignee, assigneeN, 1.0 / assignees);
        Report(output, "ASSIGNEE kNN top-10 majority", knnAssignee, assigneeN, 1.0 / assignees);

        // Service -> team determinism over the whole file.
        var pairs = all
            .Where(t => t.AffectedServices.Count > 0 && t.ServiceTeams.Count > 0)
            .GroupBy(t => t.AffectedServices[0])
            .Select(g => (Total: g.Count(), Top: g.GroupBy(t => t.ServiceTeams[0]).Max(x => x.Count())))
            .ToList();
        output.WriteLine($"SERVICE->TEAM determinism: {pairs.Sum(p => p.Top)}/{pairs.Sum(p => p.Total)} tickets carry their service's majority team ({pairs.Count} services)");

        n.Should().BeGreaterThan(1000);
        classes.Count.Should().BeInRange(2, 10);
        knnStatus.Should().BeInRange(0, n);
    }

    private static void Report(ITestOutputHelper output, string label, int correct, int n, double baseline)
    {
        var p = (double)correct / n;
        var (lo, hi) = Wilson(correct, n);
        var verdict = baseline < lo ? "ABOVE baseline" : baseline > hi ? "BELOW baseline" : "within CI of baseline (no signal)";
        output.WriteLine($"{label}: {p:P1} ({correct}/{n}), 95% CI [{lo:P1}, {hi:P1}] vs {baseline:P1} -> {verdict}");
    }

    private static (double Lo, double Hi) Wilson(int k, int n)
    {
        const double z = 1.96;
        var p = (double)k / n;
        var denom = 1 + z * z / n;
        var centre = (p + z * z / (2 * n)) / denom;
        var half = z * Math.Sqrt(p * (1 - p) / n + z * z / (4.0 * n * n)) / denom;
        return (centre - half, centre + half);
    }

    // Most frequent value; ties resolved ordinally so the split is deterministic.
    private static string? Majority(IEnumerable<string> values) => values
        .GroupBy(v => v, StringComparer.Ordinal)
        .OrderByDescending(g => g.Count())
        .ThenBy(g => g.Key, StringComparer.Ordinal)
        .Select(g => g.Key)
        .FirstOrDefault();

    // Similarity-summed vote; ties resolved ordinally.
    private static string? VoteByScore(IEnumerable<(string Label, double Score)> votes) => votes
        .GroupBy(v => v.Label, StringComparer.Ordinal)
        .OrderByDescending(g => g.Sum(v => v.Score))
        .ThenBy(g => g.Key, StringComparer.Ordinal)
        .Select(g => g.Key)
        .FirstOrDefault();

    private static string? FindTrainingFile()
    {
        var fromEnv = Environment.GetEnvironmentVariable("TRIAGE_TRAINING_FILE");
        if (!string.IsNullOrWhiteSpace(fromEnv))
        {
            return File.Exists(fromEnv) ? fromEnv : null;
        }

        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
        {
            if (File.Exists(Path.Combine(dir.FullName, "TicketTriage.slnx")))
            {
                var file = Path.Combine(dir.FullName, "data", "jira_first_20000_requested_fields_synthetic.json");
                return File.Exists(file) ? file : null;
            }
        }

        return null;
    }
}
