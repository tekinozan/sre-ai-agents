namespace SreAiAgent.Domain.Findings;

/// <summary>
/// The exact release coordinates behind one agent execution. Recorded on every
/// run so a quality or cost regression can be attributed to a specific prompt,
/// model profile or agent version instead of investigated by bisecting deploys.
/// </summary>
/// <param name="AgentName">Registry name, for example <c>database-expert</c>.</param>
/// <param name="AgentVersion">Pinned agent definition version.</param>
/// <param name="PromptName">Prompt artifact name.</param>
/// <param name="PromptVersion">Pinned prompt version; never <c>latest</c> in production.</param>
/// <param name="PromptHash">SHA-256 of the rendered prompt. The text is not stored.</param>
/// <param name="ModelProvider">Provider key, for example <c>openai</c>.</param>
/// <param name="ModelName">Concrete model resolved for this environment.</param>
/// <param name="ModelDeploymentVersion">Pinned model profile version.</param>
public sealed record AgentExecutionIdentity(
    string AgentName,
    string AgentVersion,
    string PromptName,
    string PromptVersion,
    string PromptHash,
    string ModelProvider,
    string ModelName,
    string ModelDeploymentVersion);
