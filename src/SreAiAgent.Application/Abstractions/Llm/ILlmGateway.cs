using SreAiAgent.Domain.Shared;

namespace SreAiAgent.Application.Abstractions.Llm;

/// <summary>
/// The platform's only route to a language model (ARCHITECTURE.md 6.1).
/// </summary>
/// <remarks>
/// Provider SDK types never reach the application layer. An adapter may use
/// Microsoft.Extensions.AI internally, but the port stays here so structured
/// output enforcement, budgets, audit and retry classification remain platform
/// policy rather than whatever a given SDK happens to offer this year.
/// </remarks>
public interface ILlmGateway
{
    /// <summary>
    /// Calls the model and returns a value that already satisfies the requested
    /// schema.
    /// </summary>
    /// <remarks>
    /// Returns a <see cref="Result{T}"/> rather than throwing. A rate limit, a
    /// timeout or output that fails schema validation are expected outcomes of
    /// calling a model, not exceptional ones, and the caller has to choose
    /// between retrying, degrading and reporting a partial result.
    /// </remarks>
    /// <typeparam name="T">The structured response type.</typeparam>
    /// <param name="request">Prompt, schema, profile and audit metadata.</param>
    /// <param name="cancellationToken">Cancels the call.</param>
    Task<Result<LlmResult<T>>> GenerateStructuredAsync<T>(
        LlmRequest<T> request,
        CancellationToken cancellationToken)
        where T : class;
}
