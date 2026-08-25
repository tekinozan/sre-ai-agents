namespace SreAiAgent.Application.Prompting;

/// <summary>
/// A versioned prompt artifact (ARCHITECTURE.md 8.1).
/// </summary>
/// <remarks>
/// Prompts are not embedded in C#. They are versioned files, schema-validated,
/// pinned by a release manifest and hashed for audit. The practical consequence
/// is that changing a prompt is a reviewable change with a version bump rather
/// than an edit inside a string literal that nobody diffs.
/// </remarks>
/// <param name="Name">Artifact name, for example <c>database_expert.investigate</c>.</param>
/// <param name="Version">Artifact version, for example <c>v7</c>.</param>
/// <param name="SystemTemplate">System message template.</param>
/// <param name="UserTemplate">User message template.</param>
/// <param name="RequiredVariables">
/// Variables that must be supplied. A missing one fails fast rather than
/// rendering an empty hole into the prompt, which is how an agent ends up
/// reasoning about an incident with no incident in it.
/// </param>
/// <param name="OptionalVariables">Variables that may be omitted.</param>
/// <param name="MaxRenderedCharacters">
/// Upper bound on the rendered size. Retrieved evidence can be arbitrarily large,
/// and an unbounded prompt is both a cost incident and a context-window failure.
/// </param>
/// <param name="ContentHash">SHA-256 of the artifact as loaded, for audit.</param>
public sealed record PromptDefinition(
    string Name,
    string Version,
    string SystemTemplate,
    string UserTemplate,
    IReadOnlySet<string> RequiredVariables,
    IReadOnlySet<string> OptionalVariables,
    int MaxRenderedCharacters,
    string ContentHash)
{
    /// <summary>Stable reference in <c>name:version</c> form.</summary>
    public string Reference => $"{Name}:{Version}";
}

/// <summary>
/// A rendered prompt, ready to send.
/// </summary>
/// <remarks>
/// <paramref name="Hash"/> is what gets logged, stored on the agent run and
/// attached to spans. The text itself is never logged by default
/// (ARCHITECTURE.md 8.1), because a rendered prompt contains the retrieved
/// evidence and is therefore the single richest source of sensitive data in the
/// system.
/// </remarks>
/// <param name="System">Rendered system message.</param>
/// <param name="User">Rendered user message.</param>
/// <param name="Hash">SHA-256 of the rendered content.</param>
/// <param name="CharacterCount">Total rendered size, for budget accounting.</param>
public sealed record RenderedPrompt(string System, string User, string Hash, int CharacterCount);
