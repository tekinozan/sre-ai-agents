using System.Reflection;
using SreAiAgent.Application.Errors;
using SreAiAgent.Domain.Shared;

namespace SreAiAgent.Application.Tests.Errors;

/// <summary>
/// Covers the error taxonomy of ARCHITECTURE.md 17 and the retry classification
/// rules of section 14.
/// </summary>
public sealed class ErrorCatalogTests
{
    public static TheoryData<string> ArchitectureErrorCodes() =>
    [
        "CONFIG_INVALID",
        "PROMPT_NOT_FOUND",
        "PROMPT_RENDER_FAILED",
        "MODEL_PROFILE_NOT_FOUND",
        "MODEL_CAPABILITY_MISMATCH",
        "LLM_RATE_LIMITED",
        "LLM_TIMEOUT",
        "LLM_PROVIDER_ERROR",
        "LLM_STRUCTURED_OUTPUT_INVALID",
        "TOOL_NOT_FOUND",
        "TOOL_POLICY_DENIED",
        "TOOL_TIMEOUT",
        "TOOL_RATE_LIMITED",
        "TOOL_EXECUTION_FAILED",
        "INCIDENT_NOT_FOUND",
        "INCIDENT_INVALID_STATE",
        "WORKFLOW_DEADLINE_EXCEEDED",
        "WORKFLOW_CANCELLED",
        "APPROVAL_REJECTED",
        "REMEDIATION_FAILED",
    ];

    [Theory]
    [MemberData(nameof(ArchitectureErrorCodes))]
    public void EveryCodeInTheArchitectureIsCatalogued(string code)
        => ErrorCatalog.KnownCodes.ShouldContain(code);

    [Fact]
    public void EveryDeclaredCodeHasACatalogueEntry()
    {
        // A code with no entry silently falls back to a non-retryable 500, which
        // is safe but wrong: it would report a not-found as a server error.
        string[] declared =
        [
            .. ReadCodes(typeof(ApplicationErrorCodes)),
            .. ReadCodes(typeof(DomainErrorCodes)),
        ];

        string[] missing = [.. declared.Where(static code => !ErrorCatalog.KnownCodes.Contains(code))];

        missing.ShouldBeEmpty();
    }

    [Theory]
    [InlineData("TOOL_POLICY_DENIED")]
    [InlineData("FORBIDDEN")]
    [InlineData("TOOL_ARGUMENTS_INVALID")]
    [InlineData("VALIDATION_FAILED")]
    [InlineData("INCIDENT_INVALID_STATE")]
    [InlineData("CONFIG_INVALID")]
    public void DeterministicFailuresAreNeverRetried(string code)
    {
        // Section 14: policy denials, invalid credentials and deterministic
        // validation are not retryable. Retrying them burns budget, and when the
        // cause is quota exhaustion it makes the outage worse.
        ErrorCatalog.IsRetryable(new ErrorCode(code)).ShouldBeFalse();
    }

    [Theory]
    [InlineData("LLM_RATE_LIMITED")]
    [InlineData("LLM_TIMEOUT")]
    [InlineData("TOOL_TIMEOUT")]
    [InlineData("DEPENDENCY_UNAVAILABLE")]
    public void TransientFailuresAreRetryable(string code)
        => ErrorCatalog.IsRetryable(new ErrorCode(code)).ShouldBeTrue();

    [Theory]
    [InlineData("INCIDENT_NOT_FOUND", 404)]
    [InlineData("INCIDENT_INVALID_STATE", 409)]
    [InlineData("VALIDATION_FAILED", 400)]
    [InlineData("TOOL_POLICY_DENIED", 403)]
    [InlineData("DEPENDENCY_UNAVAILABLE", 503)]
    public void HttpMappingFollowsTheArchitecture(string code, int expectedStatus)
        => ErrorCatalog.ToHttpStatusCode(new ErrorCode(code)).ShouldBe(expectedStatus);

    [Fact]
    public void AnUnknownCodeFailsClosed()
    {
        ErrorDescriptor descriptor = ErrorCatalog.Describe(new ErrorCode("SOMETHING_NOBODY_DECLARED"));

        descriptor.HttpStatusCode.ShouldBe(500);
        descriptor.IsRetryable.ShouldBeFalse(
            "guessing that an unrecognised failure is retryable is how a broken dependency becomes a retry storm");
    }

    private static IEnumerable<string> ReadCodes(Type container)
        => container
            .GetFields(BindingFlags.Public | BindingFlags.Static)
            .Where(static field => field.FieldType == typeof(ErrorCode))
            .Select(field => ((ErrorCode)field.GetValue(null)!).Value);
}
