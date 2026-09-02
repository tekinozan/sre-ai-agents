using System.Collections.Concurrent;
using SreAiAgent.Application.Abstractions.Llm;
using SreAiAgent.Application.Errors;
using SreAiAgent.Domain.ModelProviders;
using SreAiAgent.Domain.Shared;

namespace SreAiAgent.Infrastructure.Llm.Providers;

internal sealed class ProviderClientCatalog(
    IHttpClientFactory clients,
    ICredentialResolver credentials,
    ProviderEndpointPolicy endpoints,
    ScriptedChatClient scripted)
{
    public IProviderChatClient Create(ProviderConnectionRevision revision)
    {
        endpoints.Validate(revision.AdapterKind, revision.Endpoint);
        return revision.AdapterKind switch
        {
            ProviderAdapterKind.Scripted => new ScriptedProviderChatClient(scripted),
            ProviderAdapterKind.OpenAi => new OpenAiProviderChatClient(clients, credentials, revision),
            ProviderAdapterKind.OpenAiCompatible => new OpenAiCompatibleProviderChatClient(clients, credentials, revision),
            ProviderAdapterKind.AzureOpenAi => new AzureOpenAiProviderChatClient(clients, credentials, revision),
            ProviderAdapterKind.Anthropic => new AnthropicProviderChatClient(clients, credentials, revision),
            _ => throw new InvalidOperationException("The provider adapter kind is not registered."),
        };
    }

    public static ProviderCapabilityDescriptor Descriptor(ProviderAdapterKind adapterKind)
        => adapterKind switch
        {
            ProviderAdapterKind.Scripted => new(adapterKind,
                ModelCapability.StructuredOutput | ModelCapability.SystemMessage | ModelCapability.Seed
                | ModelCapability.ToolCalling | ModelCapability.ParallelToolCalls),
            ProviderAdapterKind.OpenAi or ProviderAdapterKind.AzureOpenAi => new(adapterKind,
                ModelCapability.StructuredOutput | ModelCapability.SystemMessage | ModelCapability.Seed
                | ModelCapability.ToolCalling | ModelCapability.ParallelToolCalls),
            ProviderAdapterKind.OpenAiCompatible => new(adapterKind,
                ModelCapability.StructuredOutput | ModelCapability.SystemMessage),
            ProviderAdapterKind.Anthropic => new(adapterKind,
                ModelCapability.StructuredOutput | ModelCapability.SystemMessage
                | ModelCapability.ToolCalling | ModelCapability.ParallelToolCalls),
            _ => throw new InvalidOperationException("The provider adapter kind is not registered."),
        };
}

internal sealed class ChatClientResolver(
    IProviderRevisionReader providers,
    ProviderClientCatalog catalog) : IChatClientResolver
{
    private readonly ConcurrentDictionary<ProviderConnectionRevisionId, CachedClient> _clients = new();

    public async Task<Result<IProviderChatClient>> ResolveAsync(
        PinnedModelCandidate candidate,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(candidate);
        if (_clients.TryGetValue(candidate.ProviderConnectionRevisionId, out CachedClient? cached))
        {
            return Validate(candidate, cached);
        }

        ProviderConnectionRevision? revision = await providers.FindRevisionAsync(
            candidate.Tenant,
            candidate.ProviderConnectionRevisionId,
            cancellationToken).ConfigureAwait(false);
        if (revision is null)
        {
            return Result<IProviderChatClient>.Failure(Error.Permanent(
                ApplicationErrorCodes.ProviderRevisionNotFound,
                "The pinned provider connection revision no longer exists."));
        }

        if (!string.Equals(revision.ConfigurationHash, candidate.ProviderConfigurationHash, StringComparison.Ordinal)
            || revision.Revision != candidate.ProviderConnectionRevision
            || revision.AdapterKind != candidate.AdapterKind)
        {
            return Result<IProviderChatClient>.Failure(Error.Permanent(
                DomainErrorCodes.ModelConfigurationInvalid,
                "The pinned provider identity does not match its immutable revision."));
        }

        IProviderChatClient client;
        try
        {
            client = catalog.Create(revision);
        }
        catch (InvalidOperationException exception)
        {
            return Result<IProviderChatClient>.Failure(Error.Permanent(
                DomainErrorCodes.ModelConfigurationInvalid,
                exception.Message));
        }

        CachedClient entry = _clients.GetOrAdd(
            revision.Id,
            static (_, state) => new CachedClient(
                state.Revision.Tenant,
                state.Revision.Revision,
                state.Revision.ConfigurationHash,
                state.Revision.AdapterKind,
                state.Client),
            (Revision: revision, Client: client));
        return Validate(candidate, entry);
    }

    private static Result<IProviderChatClient> Validate(PinnedModelCandidate candidate, CachedClient cached)
    {
        if (!string.Equals(candidate.Tenant, cached.Tenant, StringComparison.Ordinal)
            || candidate.ProviderConnectionRevision != cached.Revision
            || !string.Equals(candidate.ProviderConfigurationHash, cached.ConfigurationHash, StringComparison.Ordinal)
            || candidate.AdapterKind != cached.AdapterKind)
        {
            return Result<IProviderChatClient>.Failure(Error.Permanent(
                DomainErrorCodes.ModelConfigurationInvalid,
                "The pinned provider identity conflicts with the cached immutable revision."));
        }

        ModelCapability available = cached.Client.CapabilityDescriptor.Capabilities;
        if ((available & candidate.Capabilities) != candidate.Capabilities)
        {
            return Result<IProviderChatClient>.Failure(Error.Permanent(
                ApplicationErrorCodes.ModelCapabilityMismatch,
                "The pinned deployment requires capabilities the provider adapter does not support."));
        }

        return Result<IProviderChatClient>.Success(cached.Client);
    }

    private sealed record CachedClient(
        string Tenant,
        int Revision,
        string ConfigurationHash,
        ProviderAdapterKind AdapterKind,
        IProviderChatClient Client);
}
