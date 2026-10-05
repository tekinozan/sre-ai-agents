using System.Net;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using SreAiAgent.Domain.ModelProviders;
using SreAiAgent.Infrastructure.Configuration;

namespace SreAiAgent.Infrastructure.Llm;

internal sealed class ProviderEndpointPolicy
{
    private readonly bool _production;
    private readonly HashSet<string> _customHosts;

    public ProviderEndpointPolicy(IHostEnvironment environment, IOptions<LlmOptions> options)
    {
        _production = environment.IsProduction();
        _customHosts = new HashSet<string>(options.Value.AllowedCustomEndpointHosts, StringComparer.OrdinalIgnoreCase);
    }

    public Uri Validate(ProviderAdapterKind adapterKind, string endpoint)
    {
        if (!Uri.TryCreate(endpoint, UriKind.Absolute, out Uri? uri) || string.IsNullOrWhiteSpace(uri.Host))
        {
            throw new InvalidOperationException("The provider endpoint must be an absolute URI.");
        }

        if (!string.Equals(uri.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase))
        {
            bool safeDevelopmentLoopback = !_production
                && string.Equals(uri.Scheme, Uri.UriSchemeHttp, StringComparison.OrdinalIgnoreCase)
                && IsLoopback(uri.Host);
            if (!safeDevelopmentLoopback)
            {
                throw new InvalidOperationException("Provider endpoints must use HTTPS; development HTTP is limited to loopback.");
            }
        }

        if (_production)
        {
            ValidateProductionHost(adapterKind, uri);
        }

        return uri;
    }

    private void ValidateProductionHost(ProviderAdapterKind adapterKind, Uri uri)
    {
        bool allowed = adapterKind switch
        {
            ProviderAdapterKind.OpenAi => string.Equals(uri.Host, "api.openai.com", StringComparison.OrdinalIgnoreCase),
            ProviderAdapterKind.Anthropic => string.Equals(uri.Host, "api.anthropic.com", StringComparison.OrdinalIgnoreCase),
            ProviderAdapterKind.Gemini => string.Equals(uri.Host, "generativelanguage.googleapis.com", StringComparison.OrdinalIgnoreCase),
            ProviderAdapterKind.AzureOpenAi => uri.Host.EndsWith(".openai.azure.com", StringComparison.OrdinalIgnoreCase),
            ProviderAdapterKind.OpenAiCompatible => _customHosts.Contains(uri.Host),
            ProviderAdapterKind.Scripted => false,
            _ => false,
        };
        if (!allowed || IPAddress.TryParse(uri.Host, out _))
        {
            throw new InvalidOperationException("The provider endpoint host is not allowed in production.");
        }
    }

    private static bool IsLoopback(string host)
        => string.Equals(host, "localhost", StringComparison.OrdinalIgnoreCase)
           || (IPAddress.TryParse(host, out IPAddress? address) && IPAddress.IsLoopback(address));
}
