using SreAiAgent.Application.Abstractions.Llm;
using SreAiAgent.Application.Abstractions.Persistence;
using SreAiAgent.Application.Abstractions.Tools;
using SreAiAgent.Application.Abstractions.Workflow;
using SreAiAgent.Application.Agents;
using SreAiAgent.Application.Errors;
using SreAiAgent.Application.Prompting;
using SreAiAgent.Domain.Evidence;
using SreAiAgent.Domain.Remediation;
using SreAiAgent.Domain.Shared;

namespace SreAiAgent.Application.Workflows;

// Resolution helpers: turning a pinned release into the concrete prompt, model
// profile and tool descriptors an activity needs.
//
// Everything resolves through the incident's configuration snapshot, never
// through current configuration. That is what makes an investigation reproducible
// and means a release promoted mid-incident cannot change work already running
// (ARCHITECTURE.md 8.2, 24.5).

public sealed partial class InvestigationActivities
{
    private const int MaxPriorEvidenceForAgent = 20;

    /// <summary>Resolves a prompt and its model profile through the pinned snapshot.</summary>
    private async Task<Result<ResolvedPrompt>> ResolvePromptAsync(
        ConfigurationSnapshotId snapshotId,
        string promptName,
        string profileName,
        CancellationToken cancellationToken)
    {
        ConfigurationSnapshot? snapshot = await _snapshots
            .GetAsync(snapshotId, cancellationToken)
            .ConfigureAwait(false);

        if (snapshot is null)
        {
            return Result<ResolvedPrompt>.Failure(Error.Permanent(
                ApplicationErrorCodes.ConfigurationSnapshotNotFound, "The pinned snapshot is missing."));
        }

        Result<PromptDefinition> prompt = await _prompts
            .ResolveAsync(snapshot.PromptRelease, promptName, cancellationToken)
            .ConfigureAwait(false);

        if (prompt.IsFailure)
        {
            return Result<ResolvedPrompt>.Failure(prompt.Error);
        }

        Result<ModelProfile> profile = await _models
            .ResolveAsync(snapshot.ModelRelease, profileName, cancellationToken)
            .ConfigureAwait(false);

        return profile.IsFailure
            ? Result<ResolvedPrompt>.Failure(profile.Error)
            : Result<ResolvedPrompt>.Success(new ResolvedPrompt(prompt.Value, profile.Value));
    }

    /// <summary>Renders a prompt and calls the model, in one step.</summary>
    /// <typeparam name="T">The structured response type.</typeparam>
    private async Task<Result<LlmResult<T>>> CallModelAsync<T>(
        ResolvedPrompt resolved,
        IReadOnlyDictionary<string, string> variables,
        string schemaName,
        IncidentId incidentId,
        string tenant,
        string correlationId,
        string agentName,
        DateTimeOffset deadline,
        CancellationToken cancellationToken)
        where T : class
    {
        Result<RenderedPrompt> rendered = _renderer.Render(resolved.Prompt, variables);
        if (rendered.IsFailure)
        {
            return Result<LlmResult<T>>.Failure(rendered.Error);
        }

        // The call timeout is bounded by whatever remains before the deadline, so
        // a step near the end of an investigation cannot overrun it.
        TimeSpan remaining = deadline - _timeProvider.GetUtcNow();
        if (remaining <= TimeSpan.Zero)
        {
            return Result<LlmResult<T>>.Failure(Error.Permanent(
                ApplicationErrorCodes.WorkflowDeadlineExceeded,
                "The investigation deadline elapsed before the call could start."));
        }

        LlmRequest<T> request = new()
        {
            SystemPrompt = rendered.Value.System,
            UserPrompt = rendered.Value.User,
            ResponseSchemaName = schemaName,
            ModelProfile = resolved.Profile,
            Tools = [],
            Timeout = resolved.Profile.Timeout < remaining ? resolved.Profile.Timeout : remaining,
            Metadata = new LlmCallMetadata
            {
                IncidentId = incidentId,
                AgentName = agentName,
                PromptName = resolved.Prompt.Name,
                PromptVersion = resolved.Prompt.Version,
                PromptHash = rendered.Value.Hash,
                Tenant = tenant,
                CorrelationId = correlationId,
            },
        };

        return await _llm.GenerateStructuredAsync(request, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Assembles everything one agent execution needs, all resolved through the
    /// pinned snapshot.
    /// </summary>
    /// <remarks>
    /// Resolution happens once, here, rather than inside the agent loop. That is
    /// what guarantees every model call within a run used the same prompt and
    /// model versions even if a release is promoted while it executes.
    /// </remarks>
    private async Task<Result<AgentContext>> BuildAgentContextAsync(
        RunAgentInput input,
        CancellationToken cancellationToken)
    {
        ConfigurationSnapshot? snapshot = await _snapshots
            .GetAsync(input.ConfigurationSnapshotId, cancellationToken)
            .ConfigureAwait(false);

        if (snapshot is null)
        {
            return Result<AgentContext>.Failure(Error.Permanent(
                ApplicationErrorCodes.ConfigurationSnapshotNotFound, "The pinned snapshot is missing."));
        }

        Result<AgentDefinition> definition = await _agents
            .ResolveAsync(snapshot.AgentRelease, input.Agent.AgentName, cancellationToken)
            .ConfigureAwait(false);

        if (definition.IsFailure)
        {
            return Result<AgentContext>.Failure(definition.Error);
        }

        Result<PromptDefinition> prompt = await _prompts
            .ResolveAsync(snapshot.PromptRelease, PromptNameOf(definition.Value.PromptRef), cancellationToken)
            .ConfigureAwait(false);

        if (prompt.IsFailure)
        {
            return Result<AgentContext>.Failure(prompt.Error);
        }

        Result<ModelProfile> profile = await _models
            .ResolveAsync(snapshot.ModelRelease, definition.Value.ModelProfileRef, cancellationToken)
            .ConfigureAwait(false);

        if (profile.IsFailure)
        {
            return Result<AgentContext>.Failure(profile.Error);
        }

        // The agent sees only the tools its definition allows, intersected with
        // what the pinned release actually contains. Handing it a schema for a
        // tool it may not use would waste prompt budget and invite requests the
        // gateway will refuse.
        List<ToolDefinition> available = [];
        foreach (string toolName in definition.Value.AllowedTools)
        {
            ToolDescriptor? descriptor = await _tools
                .FindAsync(toolName, "v1", cancellationToken)
                .ConfigureAwait(false);

            if (descriptor is not null)
            {
                available.Add(descriptor.Definition);
            }
        }

        IReadOnlyList<EvidenceRecord> prior = await _store
            .GetUsableEvidenceAsync(input.Incident.IncidentId, MaxPriorEvidenceForAgent, cancellationToken)
            .ConfigureAwait(false);

        return Result<AgentContext>.Success(new AgentContext
        {
            Incident = input.Incident,
            Definition = definition.Value,
            Prompt = prompt.Value,
            ModelProfile = profile.Value,
            AvailableTools = available,
            ExistingEvidence = prior,
            // The agent deadline is the smaller of its own budget and what remains
            // of the investigation, so section 11.3's hierarchy holds even for an
            // agent that starts late in a fan-out.
            Deadline = Earlier(_timeProvider.GetUtcNow() + definition.Value.Budget.Timeout, input.Deadline),
            CorrelationId = input.CorrelationId,
            Tenant = input.Incident.Tenant,
            Environment = input.Incident.Environment,
            // V1 pins this to read-only regardless of anything else. The tool
            // gateway checks it again, and the policy release caps it a third time.
            RiskCeiling = RiskLevel.ReadOnly,
            IsShadow = input.Agent.IsShadow,
            ConfigurationSnapshotId = input.ConfigurationSnapshotId,
        });
    }

    private static string PromptNameOf(string reference)
    {
        int separator = reference.IndexOf(':', StringComparison.Ordinal);
        return separator < 0 ? reference : reference[..separator];
    }

    private static DateTimeOffset Earlier(DateTimeOffset left, DateTimeOffset right)
        => left < right ? left : right;
}
