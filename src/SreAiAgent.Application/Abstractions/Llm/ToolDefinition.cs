using System.Text.Json;

namespace SreAiAgent.Application.Abstractions.Llm;

/// <summary>
/// A tool as described to a model: name, purpose and argument schema.
/// </summary>
/// <remarks>
/// This is a description, never a capability. The model receives it so it can
/// formulate a typed request; that request is then validated, authorized and
/// executed by the tool gateway. ARCHITECTURE.md 6.2 is explicit that the model
/// never reaches Prometheus, Kubernetes or a database itself, so handing it a
/// schema costs nothing even if the model is compromised by injected content.
/// </remarks>
/// <param name="Name">Registry name, for example <c>prometheus.query_range</c>.</param>
/// <param name="Version">Pinned tool version.</param>
/// <param name="Description">What the tool does, in terms useful to a model.</param>
/// <param name="ParameterSchema">JSON Schema for the arguments.</param>
public sealed record ToolDefinition(
    string Name,
    string Version,
    string Description,
    JsonElement ParameterSchema);
