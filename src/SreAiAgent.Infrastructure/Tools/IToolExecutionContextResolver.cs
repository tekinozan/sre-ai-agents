using SreAiAgent.Application.Abstractions.Tools;
using SreAiAgent.Domain.Evidence;

namespace SreAiAgent.Infrastructure.Tools;

/// <summary>
/// Supplies the gateway with the authorization facts it needs, and receives the
/// evidence it produces.
/// </summary>
/// <remarks>
/// Kept as a seam rather than wired straight into the registries and the
/// persistence layer, for two reasons. The gateway runs inside a workflow
/// activity where the ambient scope is not an HTTP request, so scopes and the
/// agent's allowlist have to be supplied explicitly. And evidence has to be
/// staged in the caller's unit of work: writing it from the gateway in a separate
/// transaction would leave orphaned evidence behind whenever the surrounding
/// activity rolled back.
/// </remarks>
internal interface IToolExecutionContextResolver
{
    /// <summary>Resolves the policy release, actor scopes and agent allowlist.</summary>
    Task<ToolAuthorization> ResolveAsync(
        ToolExecutionRequest request,
        ToolExecutionContext context,
        CancellationToken cancellationToken);

    /// <summary>Stages evidence in the caller's unit of work.</summary>
    Task RecordEvidenceAsync(EvidenceRecord evidence, CancellationToken cancellationToken);
}

/// <summary>The authorization facts for one tool execution.</summary>
/// <param name="Policy">The pinned policy release.</param>
/// <param name="ActorScopes">Scopes the acting principal holds.</param>
/// <param name="AgentAllowedTools">
/// Tools the requesting agent's definition permits. Separate from the actor's
/// scopes: a human operator may hold database:read while the agent asking on
/// their behalf is still not allowed to use a database tool.
/// </param>
internal sealed record ToolAuthorization(
    PolicyRelease Policy,
    IReadOnlySet<string> ActorScopes,
    IReadOnlySet<string> AgentAllowedTools);
