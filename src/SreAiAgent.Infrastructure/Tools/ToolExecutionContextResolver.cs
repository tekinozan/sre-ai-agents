using SreAiAgent.Application.Abstractions.Persistence;
using SreAiAgent.Application.Abstractions.Tools;
using SreAiAgent.Application.Agents;
using SreAiAgent.Domain.Evidence;
using SreAiAgent.Domain.Remediation;
using SreAiAgent.Domain.Shared;

namespace SreAiAgent.Infrastructure.Tools;

/// <summary>
/// Resolves the authorization facts for one tool call from the incident's
/// pinned configuration.
/// </summary>
/// <remarks>
/// Everything is resolved through the snapshot the incident was pinned to rather
/// than through current configuration. A policy release promoted mid-incident
/// must not change what work already in flight is permitted to do, in either
/// direction: loosening it retroactively authorizes calls nobody reviewed, and
/// tightening it makes a half-finished investigation fail in a way that cannot
/// be reproduced afterwards (ARCHITECTURE.md 8.2, 24.5).
///
/// Every failure to resolve produces a denial rather than an exception. The
/// authorization returned when something is missing grants nothing, so the
/// policy engine refuses the call through its normal path and the refusal is
/// recorded like any other, instead of unwinding the agent loop.
/// </remarks>
internal sealed class ToolExecutionContextResolver(
    IIncidentQueries incidents,
    IConfigurationSnapshotStore snapshots,
    IPolicyRegistry policies,
    IAgentRegistry agents,
    IInvestigationStore store) : IToolExecutionContextResolver
{
    /// <summary>
    /// The role an autonomous investigation acts as.
    /// </summary>
    /// <remarks>
    /// Pinned to the investigator role rather than to the operator or admin
    /// roles, because an unattended investigation must not hold scopes a human
    /// deliberately kept for themselves. In V1 the distinction is theoretical,
    /// since every tool is read-only; it stops being theoretical the moment a
    /// remediation scope exists.
    /// </remarks>
    private const string PlatformRole = "SreInvestigator";

    private static readonly IReadOnlySet<string> NoScopes =
        new HashSet<string>(StringComparer.Ordinal);

    /// <summary>The policy used when nothing else can be resolved. It permits nothing.</summary>
    private static readonly PolicyRelease DenyAll = new(
        "unresolved",
        RiskLevel.ReadOnly,
        new Dictionary<string, IReadOnlySet<string>>(StringComparer.Ordinal),
        MaxWindowMinutes: 0,
        MaxResultBytes: 0,
        MinStepSeconds: int.MaxValue,
        new PolicyRateLimits(0, 0, 0),
        new PolicyTimeouts(TimeSpan.Zero, TimeSpan.Zero, TimeSpan.Zero, TimeSpan.Zero),
        new PolicyBudgets(0, 0, 0, 0m, TimeSpan.Zero));

    /// <inheritdoc />
    public async Task<ToolAuthorization> ResolveAsync(
        ToolExecutionRequest request,
        ToolExecutionContext context,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(context);

        Contracts.Incidents.IncidentResponse? incident = await incidents
            .GetIncidentAsync(context.Tenant, request.IncidentId, cancellationToken)
            .ConfigureAwait(false);

        if (incident?.ConfigurationSnapshotId is not { } snapshotId)
        {
            return Denied();
        }

        ConfigurationSnapshot? snapshot = await snapshots
            .GetAsync(snapshotId, cancellationToken)
            .ConfigureAwait(false);

        if (snapshot is null)
        {
            return Denied();
        }

        Result<PolicyRelease> policy = await policies
            .ResolveAsync(snapshot.PolicyRelease, cancellationToken)
            .ConfigureAwait(false);

        if (policy.IsFailure)
        {
            return Denied();
        }

        Result<AgentDefinition> agent = await agents
            .ResolveAsync(snapshot.AgentRelease, request.AgentName, cancellationToken)
            .ConfigureAwait(false);

        return new ToolAuthorization(
            policy.Value,
            ScopesFor(policy.Value),
            agent.IsSuccess ? agent.Value.AllowedTools : NoScopes);
    }

    /// <inheritdoc />
    /// <remarks>
    /// Staged in the caller's unit of work rather than saved here. Evidence
    /// written in its own transaction would survive a rollback of the activity
    /// that produced it, leaving rows nothing references and a citation that
    /// resolves to work the platform decided had not happened.
    /// </remarks>
    public Task RecordEvidenceAsync(EvidenceRecord evidence, CancellationToken cancellationToken)
    {
        store.AddEvidence(evidence);
        return Task.CompletedTask;
    }

    private static ToolAuthorization Denied() => new(DenyAll, NoScopes, NoScopes);

    private static IReadOnlySet<string> ScopesFor(PolicyRelease policy)
        => policy.RoleScopes.TryGetValue(PlatformRole, out IReadOnlySet<string>? scopes)
            ? scopes
            : NoScopes;
}
