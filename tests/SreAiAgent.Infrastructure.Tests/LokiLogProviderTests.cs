using System.Net;
using System.Text;
using Microsoft.Extensions.Options;
using SreAiAgent.Application.Abstractions.Observability;
using SreAiAgent.Infrastructure.Configuration;
using SreAiAgent.Infrastructure.Observability;

namespace SreAiAgent.Infrastructure.Tests;

/// <summary>
/// What the Loki adapter asks for, and what it reports back.
/// </summary>
/// <remarks>
/// The port takes literal terms rather than a query expression, and this suite is
/// what holds that line: a term is escaped and combined, never interpolated. Log
/// content is untrusted and a model reasoning over it can be steered, so the
/// worst case of a hostile term has to be an unhelpful search.
/// </remarks>
public sealed class LokiLogProviderTests
{
    private static readonly DateTimeOffset End = new(2026, 8, 28, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task TheLevelBecomesAMatcherOverEverySeverityAtOrAboveIt()
    {
        StubHttpMessageHandler handler = Answering(entries: 0, matched: 0);

        await Provider(handler).SearchAsync(Search("warn"), TestContext.Current.CancellationToken);

        handler.Parameter("query", 0).ShouldBe(
            """{service="checkout",environment="production",level=~"warn|error|fatal"}""");
    }

    [Fact]
    public async Task ATermIsEscapedIntoALineFilterRatherThanInterpolated()
    {
        StubHttpMessageHandler handler = Answering(entries: 0, matched: 0);

        // A term that closes the filter and appends its own pipeline stage is the
        // obvious thing to try, and `terms` is exactly where injected log content
        // would arrive after a model repeated it back.
        const string BreakOut = """pool" | json | line_format "{{.x}}""";

        await Provider(handler).SearchAsync(
            Search("error", BreakOut),
            TestContext.Current.CancellationToken);

        string query = handler.Parameter("query", 0);
        string escaped = BreakOut.Replace("\"", "\\\"", StringComparison.Ordinal);

        // The escaped form present and the raw form absent is what says every
        // quote the term carried is still inside the term.
        query.ShouldContain($"|= \"{escaped}\"");
        query.ShouldNotContain(BreakOut);
    }

    [Fact]
    public async Task TotalMatchedComesFromACountRatherThanTheReturnedPage()
    {
        StubHttpMessageHandler handler = Answering(entries: 2, matched: 947);

        LogSearchResult result = await Provider(handler)
            .SearchAsync(Search("error"), TestContext.Current.CancellationToken);

        // Deriving the total from the page would make it agree with the sample by
        // construction, and an agent would read a truncated window as the whole
        // story. Two entries out of 947 is a materially different fact.
        result.Entries.Count.ShouldBe(2);
        result.TotalMatched.ShouldBe(947);
        result.IsTruncated.ShouldBeTrue();
        result.Provider.ShouldBe("loki");
    }

    [Fact]
    public async Task EntriesAreOrderedNewestFirstAcrossStreams()
    {
        // Loki orders within a stream but not between them. An agent reading a
        // window out of order will build a causal story out of nothing.
        StubHttpMessageHandler handler = new(request =>
            Json(request.RequestUri!.AbsolutePath.Contains("query_range", StringComparison.Ordinal)
                ? """
                  {"status":"success","data":{"resultType":"streams","result":[
                    {"stream":{"level":"error"},"values":[["1756382400000000000","older"]]},
                    {"stream":{"level":"error"},"values":[["1756382460000000000","newer"]]}]}}
                  """
                : Count(2)));

        LogSearchResult result = await Provider(handler)
            .SearchAsync(Search("error"), TestContext.Current.CancellationToken);

        result.Entries.Select(static entry => entry.Message).ShouldBe(["newer", "older"]);
    }

    [Fact]
    public async Task AnUnknownMinimumLevelIsRefusedRatherThanMatchingNothing()
    {
        StubHttpMessageHandler handler = Answering(entries: 0, matched: 0);

        // A level that silently matched nothing would look exactly like a quiet
        // service, which is a conclusion an agent would happily draw.
        await Should.ThrowAsync<ObservabilityQueryException>(async () =>
            await Provider(handler).SearchAsync(Search("critical"), TestContext.Current.CancellationToken));

        handler.Requests.ShouldBeEmpty();
    }

    [Fact]
    public async Task AnHttpFailureIsNotReadAsAnEmptyLogWindow()
    {
        StubHttpMessageHandler handler = new("{}", HttpStatusCode.GatewayTimeout);

        // Returning zero entries here is indistinguishable from a service that
        // logged nothing, which is evidence an agent would cite.
        await Should.ThrowAsync<ObservabilityQueryException>(async () =>
            await Provider(handler).SearchAsync(Search("error"), TestContext.Current.CancellationToken));
    }

    private static LogSearchQuery Search(string minimumLevel, params string[] terms)
        => new(
            "checkout",
            "production",
            TimeWindow.EndingAt(End, TimeSpan.FromMinutes(30)),
            minimumLevel,
            terms,
            50);

    private static LokiLogProvider Provider(StubHttpMessageHandler handler)
        => new(new StubHttpClientFactory(handler), Options.Create(new ObservabilityOptions()));

    private static StubHttpMessageHandler Answering(int entries, long matched)
        => new(request => Json(
            request.RequestUri!.AbsolutePath.Contains("query_range", StringComparison.Ordinal)
                ? Streams(entries)
                : Count(matched)));

    private static string Streams(int entries)
    {
        StringBuilder values = new();
        for (int index = 0; index < entries; index++)
        {
            values.Append(values.Length == 0 ? string.Empty : ",")
                .Append("[\"")
                .Append((1756382400L + index) * 1_000_000_000L)
                .Append("\",\"line ")
                .Append(index)
                .Append("\"]");
        }

        // Concatenated rather than an interpolated raw string: the JSON is dense
        // with braces and the interpolation delimiters collide with them.
        return "{\"status\":\"success\",\"data\":{\"resultType\":\"streams\",\"result\":["
            + "{\"stream\":{\"level\":\"error\"},\"values\":[" + values + "]}]}}";
    }

    private static string Count(long matched)
        => "{\"status\":\"success\",\"data\":{\"resultType\":\"vector\",\"result\":["
           + "{\"metric\":{},\"value\":[1756384200,\""
           + matched.ToString(CultureInfo.InvariantCulture)
           + "\"]}]}}";

    private static HttpResponseMessage Json(string body)
        => new(HttpStatusCode.OK) { Content = new StringContent(body, Encoding.UTF8, "application/json") };
}
