namespace SreAiAgent.Application.Abstractions.Llm;

/// <summary>
/// A successful model call: the validated value plus everything needed to bill,
/// audit and reproduce it.
/// </summary>
/// <typeparam name="T">The structured response type.</typeparam>
/// <param name="Value">The schema-valid response.</param>
/// <param name="Provider">Provider that served the call.</param>
/// <param name="Model">Concrete model that served the call.</param>
/// <param name="InputTokens">Prompt tokens consumed.</param>
/// <param name="OutputTokens">Completion tokens produced.</param>
/// <param name="Latency">Wall-clock duration.</param>
/// <param name="RequestId">Provider request id, for support escalation.</param>
/// <param name="FinishReason">
/// Why generation stopped. A value of <c>length</c> means the response was
/// truncated, which the gateway treats as a failure rather than parsing whatever
/// arrived.
/// </param>
/// <param name="EstimatedCost">Derived from the profile's token pricing.</param>
public sealed record LlmResult<T>(
    T Value,
    string Provider,
    string Model,
    int InputTokens,
    int OutputTokens,
    TimeSpan Latency,
    string RequestId,
    string FinishReason,
    decimal EstimatedCost)
    where T : class;
