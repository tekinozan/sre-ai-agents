using SreAiAgent.Application.Abstractions.Llm;
using SreAiAgent.Domain.Remediation;

namespace SreAiAgent.Application.Abstractions.Tools;

/// <summary>
/// Everything the platform knows about one tool, loaded from a versioned
/// artifact at startup.
/// </summary>
/// <param name="Name">Registry name, for example <c>prometheus.query_range</c>.</param>
/// <param name="Version">Pinned version.</param>
/// <param name="Description">What it does, in terms useful to a model.</param>
/// <param name="Risk">
/// Its risk class. A read-only registry entry above <see cref="RiskLevel.ReadOnly"/>
/// fails startup validation.
/// </param>
/// <param name="Provider">Backend it talks to, for example <c>prometheus</c>.</param>
/// <param name="RequiredScopes">Authorization scopes the actor must hold.</param>
/// <param name="Timeout">Tool-level timeout, below the agent timeout.</param>
/// <param name="MaxResultBytes">Result-size cap applied before redaction.</param>
/// <param name="Definition">The schema handed to the model.</param>
public sealed record ToolDescriptor(
    string Name,
    string Version,
    string Description,
    RiskLevel Risk,
    string Provider,
    IReadOnlySet<string> RequiredScopes,
    TimeSpan Timeout,
    int MaxResultBytes,
    ToolDefinition Definition);

/// <summary>
/// Tools that cannot change external state (ARCHITECTURE.md 15.2).
/// </summary>
/// <remarks>
/// Read-only and write tools live in separate types rather than behind a flag on
/// a shared registry. A boolean is one inverted condition away from executing a
/// write; a component that has no reference to the write registry cannot run one
/// no matter what it is asked to do. V1 wires only this interface.
/// </remarks>
public interface IReadOnlyToolRegistry
{
    /// <summary>
    /// Looks up a tool. Returns a <see cref="ValueTask{TResult}"/> because the
    /// registry is a frozen dictionary built once at startup and every lookup
    /// completes synchronously; the async shape exists only so a future remote
    /// control plane does not become a breaking change.
    /// </summary>
    ValueTask<ToolDescriptor?> FindAsync(string toolName, string toolVersion, CancellationToken cancellationToken);

    /// <summary>Every registered read-only tool, for startup validation and prompts.</summary>
    IReadOnlyCollection<ToolDescriptor> All { get; }
}

/// <summary>
/// Tools that change external state. Deliberately empty in V1.
/// </summary>
/// <remarks>
/// The interface exists so the separation is structural from the start. Nothing
/// in the V1 composition root registers an implementation, so a write tool
/// cannot be resolved even by a caller that tries.
/// </remarks>
public interface IWriteToolRegistry
{
    ValueTask<ToolDescriptor?> FindAsync(string toolName, string toolVersion, CancellationToken cancellationToken);

    IReadOnlyCollection<ToolDescriptor> All { get; }
}
