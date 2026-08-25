namespace SreAiAgent.Application.Abstractions.Llm;

/// <summary>
/// A versioned model configuration (ARCHITECTURE.md 8.2). Loaded from a YAML
/// artifact and pinned into the incident's configuration snapshot, so a model or
/// temperature change cannot alter an investigation that is already running.
/// </summary>
/// <remarks>
/// <c>ModelName</c> is resolved per environment rather than written into the
/// artifact, which keeps one profile usable across staging and production while
/// the concrete deployment name stays configuration.
/// </remarks>
public sealed record ModelProfile
{
    public required string Name { get; init; }

    public required string Version { get; init; }

    /// <summary>Provider key, for example <c>openai</c>.</summary>
    public required string Provider { get; init; }

    /// <summary>Concrete model resolved for the running environment.</summary>
    public required string ModelName { get; init; }

    /// <summary>Zero for analytical work: the platform wants reproducibility, not variety.</summary>
    public required float Temperature { get; init; }

    public required TimeSpan Timeout { get; init; }

    public required int MaxOutputTokens { get; init; }

    /// <summary>
    /// Whether the provider can be held to a JSON schema. A profile that claims
    /// the capability and cannot deliver it fails startup validation with
    /// MODEL_CAPABILITY_MISMATCH rather than at the first investigation.
    /// </summary>
    public required bool SupportsStructuredOutput { get; init; }

    public required bool SupportsToolCalling { get; init; }

    public required bool SupportsParallelToolCalls { get; init; }

    /// <summary>Cost per million input tokens, for budget accounting.</summary>
    public decimal InputCostPerMillionTokens { get; init; }

    /// <summary>Cost per million output tokens, for budget accounting.</summary>
    public decimal OutputCostPerMillionTokens { get; init; }

    /// <summary>Stable reference in <c>name:version</c> form, as agent definitions use it.</summary>
    public string Reference => $"{Name}:{Version}";

    /// <summary>Derives the spend for one call from the profile's own pricing.</summary>
    public decimal EstimateCost(long inputTokens, long outputTokens)
        => ((inputTokens * InputCostPerMillionTokens) + (outputTokens * OutputCostPerMillionTokens))
           / 1_000_000m;
}
