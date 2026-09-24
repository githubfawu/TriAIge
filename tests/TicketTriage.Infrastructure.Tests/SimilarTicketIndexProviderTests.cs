using Microsoft.Extensions.Logging.Abstractions;
using TicketTriage.Infrastructure.Retrieval;

namespace TicketTriage.Infrastructure.Tests;

public class SimilarTicketIndexProviderTests
{
    private static readonly IReadOnlyList<CorpusDocument> Corpus = [new(1, "printer paper jam"), new(2, "vpn login failure")];

    [Fact]
    public async Task GetAsync_ConcurrentFirstCalls_LoadOnce_AndShareInstance_PerAC6()
    {
        var ct = TestContext.Current.CancellationToken;
        var loads = 0;
        TaskCompletionSource entered = new(TaskCreationOptions.RunContinuationsAsynchronously);
        TaskCompletionSource release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        using var provider = new SimilarTicketIndexProvider(
            async token =>
            {
                Interlocked.Increment(ref loads);
                entered.TrySetResult();
                await release.Task.WaitAsync(token);
                return Corpus;
            },
            NullLogger<SimilarTicketIndexProvider>.Instance);

        var first = provider.GetAsync(ct).AsTask();
        await entered.Task.WaitAsync(ct);
        var queued = Enumerable.Range(0, 7).Select(_ => provider.GetAsync(ct).AsTask()).ToArray();
        queued.Should().OnlyContain(t => !t.IsCompleted);
        release.SetResult();
        var indexes = await Task.WhenAll([first, .. queued]);

        loads.Should().Be(1);
        indexes.Distinct().Should().ContainSingle();
        indexes[0].DocumentCount.Should().Be(2);
    }

    [Fact]
    public async Task GetAsync_CancelledDuringFirstBuild_Throws_AndNextCallRetries()
    {
        var ct = TestContext.Current.CancellationToken;
        var loads = 0;
        TaskCompletionSource entered = new(TaskCreationOptions.RunContinuationsAsynchronously);
        using var provider = new SimilarTicketIndexProvider(
            async token =>
            {
                if (Interlocked.Increment(ref loads) == 1)
                {
                    entered.SetResult();
                    await Task.Delay(Timeout.Infinite, token);
                }

                return Corpus;
            },
            NullLogger<SimilarTicketIndexProvider>.Instance);
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var first = provider.GetAsync(cts.Token).AsTask();
        await entered.Task.WaitAsync(ct);
        await cts.CancelAsync();

        var act = async () => await first;
        await act.Should().ThrowAsync<OperationCanceledException>();

        (await provider.GetAsync(ct)).DocumentCount.Should().Be(2);
        loads.Should().Be(2);
    }

    [Fact]
    public async Task GetAsync_LoaderThrows_IsNotCached_NextCallRetries()
    {
        var ct = TestContext.Current.CancellationToken;
        var loads = 0;
        using var provider = new SimilarTicketIndexProvider(
            _ => ++loads == 1 ? throw new InvalidOperationException("db down") : Task.FromResult(Corpus),
            NullLogger<SimilarTicketIndexProvider>.Instance);

        var act = async () => await provider.GetAsync(ct);
        await act.Should().ThrowAsync<InvalidOperationException>();

        (await provider.GetAsync(ct)).DocumentCount.Should().Be(2);
        (await provider.GetAsync(ct)).DocumentCount.Should().Be(2);
        loads.Should().Be(2);
    }

    [Fact]
    public async Task GetAsync_SecondCallerCancelledWhileWaitingForGate_Throws_AndFirstBuildStillCompletes()
    {
        var ct = TestContext.Current.CancellationToken;
        var loads = 0;
        TaskCompletionSource entered = new(TaskCreationOptions.RunContinuationsAsynchronously);
        TaskCompletionSource release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        using var provider = new SimilarTicketIndexProvider(
            async token =>
            {
                Interlocked.Increment(ref loads);
                entered.TrySetResult();
                await release.Task.WaitAsync(token);
                return Corpus;
            },
            NullLogger<SimilarTicketIndexProvider>.Instance);

        var first = provider.GetAsync(ct).AsTask();
        await entered.Task.WaitAsync(ct);
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var second = provider.GetAsync(cts.Token).AsTask();
        await cts.CancelAsync();

        var act = async () => await second;
        await act.Should().ThrowAsync<OperationCanceledException>();

        release.SetResult();
        (await first).DocumentCount.Should().Be(2);
        (await provider.GetAsync(ct)).Should().BeSameAs(await first);
        loads.Should().Be(1);
    }
}
