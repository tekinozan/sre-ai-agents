using Microsoft.Extensions.Options;
using SreAiAgent.Application.Abstractions.Observability;
using SreAiAgent.Infrastructure.Configuration;
using SreAiAgent.Infrastructure.Observability.Providers.Fakes;
using SreAiAgent.Infrastructure.Observability.Providers.Loki;

namespace SreAiAgent.ContractTests;

/// <summary>
/// What every <see cref="ILogProvider"/> has to do, whichever one it is.
/// </summary>
public abstract class LogProviderContract
{
    protected abstract ILogProvider Provider { get; }

    protected abstract string Service { get; }

    [Fact]
    public async Task EntriesAreNewestFirst()
    {
        LogSearchResult result = await Provider.SearchAsync(
            Search(limit: 50), TestContext.Current.CancellationToken);

        result.Entries.ShouldNotBeEmpty();

        // The port says newest first, and an agent building a causal story from
        // an ordered window reads the opposite conclusion out of the opposite
        // order. This is the assertion that caught the fake returning them
        // oldest-first while Loki returned them newest-first — every
        // investigation run against the fakes until then had read the story
        // backwards relative to production.
        result.Entries.Select(static entry => entry.Timestamp)
            .ShouldBe(result.Entries.Select(static entry => entry.Timestamp).OrderDescending());
    }

    [Fact]
    public async Task EveryEntryFallsInsideTheRequestedWindow()
    {
        LogSearchQuery query = Search(limit: 50);

        LogSearchResult result = await Provider.SearchAsync(query, TestContext.Current.CancellationToken);

        foreach (LogEntry entry in result.Entries)
        {
            entry.Timestamp.ShouldBeGreaterThanOrEqualTo(query.Window.Start);
            entry.Timestamp.ShouldBeLessThanOrEqualTo(query.Window.End);
        }
    }

    [Fact]
    public async Task TheMatchCountIsNeverSmallerThanThePage()
    {
        LogSearchResult result = await Provider.SearchAsync(
            Search(limit: 50), TestContext.Current.CancellationToken);

        // TotalMatched exists so an agent can say "at least N" instead of
        // reasoning over a truncated sample as though it were complete. A total
        // below the page it accompanies would make that statement false.
        result.TotalMatched.ShouldBeGreaterThanOrEqualTo(result.Entries.Count);
    }

    [Fact]
    public async Task TheLimitIsNotExceeded()
    {
        LogSearchResult result = await Provider.SearchAsync(
            Search(limit: 1), TestContext.Current.CancellationToken);

        result.Entries.Count.ShouldBeLessThanOrEqualTo(1);
    }

    [Fact]
    public async Task TheResultNamesTheBackendThatAnswered()
    {
        LogSearchResult result = await Provider.SearchAsync(
            Search(limit: 50), TestContext.Current.CancellationToken);

        result.Provider.ShouldNotBeNullOrWhiteSpace();
    }

    [Fact]
    public async Task ACancelledTokenIsHonoured()
    {
        using CancellationTokenSource cancelled = new();
        await cancelled.CancelAsync();

        await Should.ThrowAsync<OperationCanceledException>(async () =>
            await Provider.SearchAsync(Search(limit: 50), cancelled.Token));
    }

    private LogSearchQuery Search(int limit)
        => new(
            Service,
            "production",
            TimeWindow.EndingAt(DateTimeOffset.UtcNow, TimeSpan.FromMinutes(30)),
            "error",
            [],
            limit);
}

/// <summary>The contract, against the deterministic fake.</summary>
public sealed class FakeLogContractTests : LogProviderContract
{
    protected override ILogProvider Provider { get; } = new FakeLogProvider();

    protected override string Service => ObservabilityFixture.SeededService;
}

/// <summary>The same contract, against a real Loki holding real lines.</summary>
[Collection(ObservabilityCollection.Name)]
[Trait("Category", "Integration")]
public sealed class LokiLogContractTests : LogProviderContract
{
    public LokiLogContractTests(ObservabilityFixture fixture)
    {
        ArgumentNullException.ThrowIfNull(fixture);

        Provider = new LokiLogProvider(
            new FixedAddressHttpClientFactory(fixture.LokiBaseAddress),
            Options.Create(new ObservabilityOptions
            {
                LogBackendBaseAddress = fixture.LokiBaseAddress.ToString(),
            }));
    }

    protected override ILogProvider Provider { get; }

    protected override string Service => ObservabilityFixture.SeededService;
}
