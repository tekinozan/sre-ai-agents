using SreAiAgent.Domain.Shared;

namespace SreAiAgent.Application.Prompting;

/// <summary>
/// Renders a prompt artifact with a fixed set of variables
/// (ARCHITECTURE.md 8.1).
/// </summary>
/// <remarks>
/// A controlled renderer rather than a general template engine. A full engine
/// brings expressions, includes and, in several well-known cases, code execution;
/// none of that is needed to substitute six named values, and all of it is
/// reachable from a template someone edits under time pressure during an
/// incident. This renderer substitutes declared variables and does nothing else.
/// </remarks>
public interface IPromptRenderer
{
    /// <summary>
    /// Renders the definition. Fails on a missing required variable, on an
    /// undeclared variable, and when the result exceeds the artifact's size
    /// bound.
    /// </summary>
    Result<RenderedPrompt> Render(
        PromptDefinition definition,
        IReadOnlyDictionary<string, string> variables);
}
