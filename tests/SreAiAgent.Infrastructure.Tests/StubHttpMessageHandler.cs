using System.Net;
using System.Text;

namespace SreAiAgent.Infrastructure.Tests;

/// <summary>
/// Answers HTTP calls from a script and records what was asked.
/// </summary>
/// <remarks>
/// The request matters more than the response here. What an observability adapter
/// sends is a query it built out of values a model supplied, and that string is
/// the thing worth asserting on; a live backend would answer it correctly and
/// tell you nothing about how it was assembled.
/// </remarks>
internal sealed class StubHttpMessageHandler : HttpMessageHandler
{
    private readonly Func<HttpRequestMessage, HttpResponseMessage> _respond;

    public StubHttpMessageHandler(string body, HttpStatusCode status = HttpStatusCode.OK)
        => _respond = _ => new HttpResponseMessage(status)
        {
            Content = new StringContent(body, Encoding.UTF8, "application/json"),
        };

    public StubHttpMessageHandler(Func<HttpRequestMessage, HttpResponseMessage> respond)
        => _respond = respond;

    public List<Uri> Requests { get; } = [];
    public List<HttpRequestMessage> RequestsMessage { get; } = [];
    public List<string> RequestBodies { get; } = [];

    /// <summary>The decoded value of a query string parameter from the last call.</summary>
    public string Parameter(string name) => Parameter(name, Requests.Count - 1);

    /// <summary>
    /// The same, from one particular call. An adapter that asks a backend more
    /// than one question per operation — Loki needs a second request for the
    /// match count — would otherwise only ever be asserted on its last.
    /// </summary>
    public string Parameter(string name, int requestIndex)
    {
        Requests.ShouldNotBeEmpty();

        foreach (string pair in Requests[requestIndex].Query.TrimStart('?').Split('&'))
        {
            int separator = pair.IndexOf('=', StringComparison.Ordinal);
            if (separator > 0 && string.Equals(pair[..separator], name, StringComparison.Ordinal))
            {
                return Uri.UnescapeDataString(pair[(separator + 1)..]);
            }
        }

        throw new InvalidOperationException($"No '{name}' parameter in '{Requests[requestIndex]}'.");
    }

    protected override async Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        Requests.Add(request.RequestUri!);
        RequestsMessage.Add(request);

        // HttpClient disposes the outgoing content once SendAsync returns, so
        // any assertion on it later reads a disposed stream. Buffer the body
        // here while it is still readable.
        string body = request.Content is null
            ? string.Empty
            : await request.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        RequestBodies.Add(body);

        return _respond(request);
    }
}

/// <summary>Hands out clients over one stub handler.</summary>
internal sealed class StubHttpClientFactory : IHttpClientFactory
{
    private readonly HttpMessageHandler _handler;
    private readonly Uri _baseAddress;

    public StubHttpClientFactory(HttpMessageHandler handler, string baseAddress = "http://observability.test/")
    {
        _handler = handler;
        _baseAddress = new Uri(baseAddress, UriKind.Absolute);
    }

    public HttpClient CreateClient(string name)
        => new(_handler, disposeHandler: false) { BaseAddress = _baseAddress };
}
