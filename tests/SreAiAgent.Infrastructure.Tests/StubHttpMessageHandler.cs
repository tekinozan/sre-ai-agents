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

    /// <summary>The decoded value of a query string parameter from the last call.</summary>
    public string Parameter(string name)
    {
        Requests.ShouldNotBeEmpty();

        foreach (string pair in Requests[^1].Query.TrimStart('?').Split('&'))
        {
            int separator = pair.IndexOf('=', StringComparison.Ordinal);
            if (separator > 0 && string.Equals(pair[..separator], name, StringComparison.Ordinal))
            {
                return Uri.UnescapeDataString(pair[(separator + 1)..]);
            }
        }

        throw new InvalidOperationException($"No '{name}' parameter in '{Requests[^1]}'.");
    }

    protected override Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        Requests.Add(request.RequestUri!);

        return Task.FromResult(_respond(request));
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
