using Microsoft.Extensions.Options;
using SreAiAgent.Application.Abstractions.Llm;
using SreAiAgent.Application.Abstractions.Persistence;
using SreAiAgent.Application.Agents;
using SreAiAgent.Application.Errors;
using SreAiAgent.Domain.ModelProviders;
using SreAiAgent.Domain.Shared;
using SreAiAgent.Infrastructure.Configuration;
using SreAiAgent.Infrastructure.Persistence.Repositories;

namespace SreAiAgent.Infrastructure.Registries;

internal sealed class ConfigurationPinner(
    IOptions<RegistryOptions> options,
    IAgentFlagEvaluator flags,
    ModelProviderRepository providers) : IConfigurationPinner
{
    private readonly RegistryOptions _options = options.Value;

    public async Task<Result<ConfigurationSnapshot>> PinCurrentAsync(
        string tenant,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        // Incidents carry no Product dimension (D41), so TenantWide is the
        // only scope this caller can honestly ask for — not a default
        // standing in for data it doesn't have, but the whole of what it has.
        PinnedModelAssignment? assignment = await providers
            .GetActiveSnapshotAsync(TenantScope.TenantWide(tenant), cancellationToken)
            .ConfigureAwait(false);
        if (assignment is null)
        {
            return Result<ConfigurationSnapshot>.Failure(Error.Permanent(
                ApplicationErrorCodes.ModelAssignmentNotFound,
                "The tenant has no active model assignment release."));
        }

        return Result<ConfigurationSnapshot>.Success(new ConfigurationSnapshot(
            ConfigurationSnapshotId.New(),
            _options.AgentRelease,
            _options.PromptRelease,
            assignment,
            _options.ToolRelease,
            _options.PolicyRelease,
            flags.DeclaredStates(),
            now));
    }
}
