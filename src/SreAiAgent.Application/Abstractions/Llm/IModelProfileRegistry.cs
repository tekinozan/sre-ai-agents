using SreAiAgent.Domain.Shared;

namespace SreAiAgent.Application.Abstractions.Llm;

/// <summary>
/// Resolves model profiles through a pinned release (ARCHITECTURE.md 8.2).
/// </summary>
/// <remarks>
/// Model releases are versioned independently of prompt releases. A model
/// rollout and a prompt change are different risks with different blast radii,
/// and coupling them would mean a prompt fix could not ship without also moving
/// the model.
/// </remarks>
public interface IModelProfileRegistry
{
    ValueTask<Result<ModelProfile>> ResolveAsync(
        string releaseName,
        string profileReference,
        CancellationToken cancellationToken);

    /// <summary>Every loaded profile, for startup validation.</summary>
    IReadOnlyCollection<ModelProfile> All { get; }
}
