using SreAiAgent.Domain.Shared;

namespace SreAiAgent.Application.Prompting;

/// <summary>
/// An immutable set of prompt versions promoted together
/// (ARCHITECTURE.md 8.1).
/// </summary>
/// <param name="Name">Release name, for example <c>sre-prompts-2026.08.24.1</c>.</param>
/// <param name="Prompts">Prompt name to the exact version pinned by this release.</param>
/// <param name="CreatedAt">When the release was cut, UTC.</param>
public sealed record PromptRelease(
    string Name,
    IReadOnlyDictionary<string, string> Prompts,
    DateTimeOffset CreatedAt);

/// <summary>
/// Resolves prompts through a pinned release.
/// </summary>
/// <remarks>
/// There is no way to ask for the newest version. Section 8.1 forbids
/// <c>latest</c> in a production workflow, and an API that cannot express it
/// enforces that better than a naming convention: an investigation resolves
/// through the release recorded in its configuration snapshot, so a prompt
/// promoted mid-incident cannot change what is already running.
/// </remarks>
public interface IPromptRegistry
{
    /// <summary>Resolves a prompt through a named release.</summary>
    ValueTask<Result<PromptDefinition>> ResolveAsync(
        string releaseName,
        string promptName,
        CancellationToken cancellationToken);

    /// <summary>Loads a release manifest.</summary>
    ValueTask<Result<PromptRelease>> GetReleaseAsync(string releaseName, CancellationToken cancellationToken);

    /// <summary>Every loaded prompt, for startup validation.</summary>
    IReadOnlyCollection<PromptDefinition> All { get; }
}
