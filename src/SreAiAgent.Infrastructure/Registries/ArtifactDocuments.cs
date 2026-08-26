namespace SreAiAgent.Infrastructure.Registries;

// The on-disk shapes of the YAML artifacts.
//
// These are separate from the application-layer types they produce. An artifact
// is a file format that has to stay backwards compatible with releases already
// pinned by closed incidents; the application type is free to change with the
// code. Binding one directly to the other would couple a refactor to a file
// format that months-old audit data depends on.
//
// Members are settable and nullable because YamlDotNet materializes them, and
// because a missing key must produce a validation error naming the file rather
// than a deserialization exception naming a property.

internal sealed class PromptDocument
{
    public string? Name { get; set; }

    public string? Version { get; set; }

    /// <summary>Present on shared fragments, which have no system/user split.</summary>
    public string? Content { get; set; }

    public string? System { get; set; }

    public string? User { get; set; }

    public int MaxRenderedCharacters { get; set; }

    public List<string>? RequiredVariables { get; set; }

    public List<string>? OptionalVariables { get; set; }
}

internal sealed class PromptReleaseDocument
{
    public string? Name { get; set; }

    public DateTimeOffset CreatedAt { get; set; }

    public Dictionary<string, string>? Prompts { get; set; }
}

internal sealed class ModelProfileDocument
{
    public string? Name { get; set; }

    public string? Version { get; set; }

    public string? Provider { get; set; }

    public string? Model { get; set; }

    public float Temperature { get; set; }

    public int TimeoutSeconds { get; set; }

    public int MaxOutputTokens { get; set; }

    public bool StructuredOutput { get; set; }

    public bool ToolCalling { get; set; }

    public bool ParallelToolCalls { get; set; }

    public decimal InputCostPerMillionTokens { get; set; }

    public decimal OutputCostPerMillionTokens { get; set; }
}

internal sealed class ModelReleaseDocument
{
    public string? Name { get; set; }

    public DateTimeOffset CreatedAt { get; set; }

    public Dictionary<string, string>? Profiles { get; set; }
}

internal sealed class AgentDocument
{
    public string? Name { get; set; }

    public string? Version { get; set; }

    public bool Enabled { get; set; }

    public string? PromptRef { get; set; }

    public string? ModelProfile { get; set; }

    public List<string>? Capabilities { get; set; }

    public List<string>? IncidentTypes { get; set; }

    public AgentToolsDocument? Tools { get; set; }

    public AgentExecutionDocument? Execution { get; set; }

    public string? OutputSchema { get; set; }
}

internal sealed class AgentToolsDocument
{
    public List<string>? Allow { get; set; }
}

internal sealed class AgentExecutionDocument
{
    public int MaxIterations { get; set; }

    public int TimeoutSeconds { get; set; }

    public int MaxToolCalls { get; set; }

    public int MaxLlmCalls { get; set; }

    public long MaxTotalTokens { get; set; }

    public decimal MaxEstimatedCost { get; set; }
}

internal sealed class AgentReleaseDocument
{
    public string? Name { get; set; }

    public DateTimeOffset CreatedAt { get; set; }

    public Dictionary<string, string>? Agents { get; set; }
}

internal sealed class ToolDocument
{
    public string? Name { get; set; }

    public string? Version { get; set; }

    public string? Risk { get; set; }

    public string? Provider { get; set; }

    public string? Description { get; set; }

    public List<string>? RequiredScopes { get; set; }

    public int TimeoutSeconds { get; set; }

    public int MaxResultBytes { get; set; }

    /// <summary>
    /// The JSON Schema for the arguments, authored as YAML. Kept as an untyped
    /// map and re-serialized to JSON by the registry, because the schema is
    /// handed verbatim to both the model and the validator; parsing it into typed
    /// objects here would mean maintaining a JSON Schema model for no benefit.
    /// </summary>
    public Dictionary<object, object>? Parameters { get; set; }
}

internal sealed class ToolReleaseDocument
{
    public string? Name { get; set; }

    public DateTimeOffset CreatedAt { get; set; }

    public Dictionary<string, string>? Tools { get; set; }
}

internal sealed class PolicyDocument
{
    public string? Name { get; set; }

    public DateTimeOffset CreatedAt { get; set; }

    public string? RiskCeiling { get; set; }

    public Dictionary<string, List<string>>? RoleScopes { get; set; }

    public ArgumentPolicyDocument? ArgumentPolicy { get; set; }

    public RateLimitDocument? RateLimits { get; set; }

    public TimeoutDocument? Timeouts { get; set; }

    public BudgetDocument? Budgets { get; set; }
}

internal sealed class ArgumentPolicyDocument
{
    public int MaxWindowMinutes { get; set; }

    public int MaxResultBytes { get; set; }

    public int MinStepSeconds { get; set; }
}

internal sealed class RateLimitDocument
{
    public int PerTenantPerMinute { get; set; }

    public int PerIncidentPerMinute { get; set; }

    public int PerToolPerMinute { get; set; }
}

internal sealed class TimeoutDocument
{
    public int HttpClientSeconds { get; set; }

    public int ToolSeconds { get; set; }

    public int AgentSeconds { get; set; }

    public int WorkflowSeconds { get; set; }
}

internal sealed class BudgetDocument
{
    public int MaxLlmCallsPerIncident { get; set; }

    public int MaxToolCallsPerIncident { get; set; }

    public long MaxTotalTokensPerIncident { get; set; }

    public decimal MaxEstimatedCostPerIncident { get; set; }

    public int MaxRuntimeSeconds { get; set; }
}

internal sealed class FlagSetDocument
{
    public string? Version { get; set; }

    public FlagDefaultsDocument? Defaults { get; set; }

    public List<AgentFlagDocument>? Agents { get; set; }
}

internal sealed class FlagDefaultsDocument
{
    public string? State { get; set; }

    public Dictionary<string, bool>? Environments { get; set; }
}

internal sealed class AgentFlagDocument
{
    public string? Name { get; set; }

    public string? Version { get; set; }

    public AvailabilityDocument? Availability { get; set; }

    public FlagRoutingDocument? Routing { get; set; }

    public FlagExecutionDocument? Execution { get; set; }

    public RolloutDocument? Rollout { get; set; }
}

internal sealed class AvailabilityDocument
{
    public string? State { get; set; }

    public Dictionary<string, bool>? Environments { get; set; }
}

internal sealed class FlagRoutingDocument
{
    public List<string>? AllowedIncidentTypes { get; set; }
}

internal sealed class FlagExecutionDocument
{
    public string? Mode { get; set; }
}

internal sealed class RolloutDocument
{
    public string? Mode { get; set; }

    public int Value { get; set; }

    public List<string>? Tenants { get; set; }
}

internal sealed class FlagReleaseDocument
{
    public string? Name { get; set; }

    public DateTimeOffset CreatedAt { get; set; }

    public string? FlagSet { get; set; }
}
