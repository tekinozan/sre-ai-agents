using System.Net.Http.Headers;
using Microsoft.Extensions.Hosting;
using Polly.CircuitBreaker;
using Polly.Timeout;
using SreAiAgent.Application.Abstractions.Llm;
using SreAiAgent.Application.Errors;
using SreAiAgent.Domain.ModelProviders;
using SreAiAgent.Domain.Shared;

namespace SreAiAgent.Infrastructure.Llm.Providers;

internal sealed record ProviderVerificationProbe(ModelCapability Capabilities, string RequestId);

internal sealed class ProviderRevisionVerifier(
    IHttpClientFactory clients,
    ICredentialResolver credentials,
    ProviderEndpointPolicy endpoints,
    IHostEnvironment environment)
{
    public async Task<Result<ProviderVerificationProbe>> VerifyAsync(
        ProviderConnectionRevision revision,
        CancellationToken cancellationToken)
    {
        Uri baseUri;
        try
        {
            baseUri = endpoints.Validate(revision.AdapterKind, revision.Endpoint);
        }
        catch (InvalidOperationException exception)
        {
            return Result<ProviderVerificationProbe>.Failure(Error.Permanent(
                ApplicationErrorCodes.ConfigInvalid,
                exception.Message));
        }

        ProviderCapabilityDescriptor descriptor = ProviderClientCatalog.Descriptor(revision.AdapterKind);
        if (revision.AdapterKind == ProviderAdapterKind.Scripted)
        {
            return environment.IsProduction()
                ? Result<ProviderVerificationProbe>.Failure(Error.Permanent(
                    ApplicationErrorCodes.ModelCapabilityMismatch,
                    "Scripted model providers cannot be verified or activated in production."))
                : Result<ProviderVerificationProbe>.Success(new ProviderVerificationProbe(
                    descriptor.Capabilities,
                    "scripted-verification"));
        }

        Result<CredentialValue> credential = await credentials
            .ResolveAsync(revision.CredentialRef, cancellationToken)
            .ConfigureAwait(false);
        if (credential.IsFailure)
        {
            return Result<ProviderVerificationProbe>.Failure(credential.Error);
        }

        string relative = revision.AdapterKind switch
        {
            ProviderAdapterKind.AzureOpenAi => "openai/models?api-version="
                + Uri.EscapeDataString(revision.ApiVersion ?? string.Empty),
            _ => "models",
        };
        using HttpRequestMessage request = new(
            HttpMethod.Get,
            OpenAiCompatibleProviderChatClient.BuildEndpoint(baseUri.ToString(), relative));
        if (revision.AdapterKind == ProviderAdapterKind.AzureOpenAi)
        {
            request.Headers.TryAddWithoutValidation("api-key", credential.Value.Value);
        }
        else if (revision.AdapterKind == ProviderAdapterKind.Anthropic)
        {
            request.Headers.TryAddWithoutValidation("x-api-key", credential.Value.Value);
            request.Headers.TryAddWithoutValidation("anthropic-version", revision.ApiVersion ?? "2023-06-01");
        }
        else
        {
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", credential.Value.Value);
            if (!string.IsNullOrWhiteSpace(revision.Organization))
            {
                request.Headers.TryAddWithoutValidation("OpenAI-Organization", revision.Organization);
            }
        }

        try
        {
            HttpClient client = clients.CreateClient(ProviderHttpClients.Chat);
            using HttpResponseMessage response = await client
                .SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken)
                .ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                return Result<ProviderVerificationProbe>.Failure(
                    OpenAiCompatibleProviderChatClient.MapStatus(response.StatusCode));
            }

            string requestId = OpenAiCompatibleProviderChatClient.Header(response, "x-request-id")
                ?? OpenAiCompatibleProviderChatClient.Header(response, "request-id")
                ?? string.Empty;
            return Result<ProviderVerificationProbe>.Success(new ProviderVerificationProbe(
                descriptor.Capabilities,
                requestId));
        }
        catch (HttpRequestException)
        {
            return Result<ProviderVerificationProbe>.Failure(Error.Transient(
                ApplicationErrorCodes.LlmUnavailable,
                "The model provider is unavailable."));
        }
        catch (BrokenCircuitException)
        {
            return Result<ProviderVerificationProbe>.Failure(Error.Transient(
                ApplicationErrorCodes.LlmUnavailable,
                "The model provider is unavailable."));
        }
        catch (TimeoutRejectedException)
        {
            return Result<ProviderVerificationProbe>.Failure(Error.Transient(
                ApplicationErrorCodes.LlmTimeout,
                "The model provider request timed out."));
        }
    }
}
