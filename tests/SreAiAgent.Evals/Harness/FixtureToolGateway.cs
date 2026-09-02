using SreAiAgent.Application.Abstractions.Tools;
using SreAiAgent.Application.Errors;
using SreAiAgent.Domain.Remediation;
using SreAiAgent.Domain.Shared;

namespace SreAiAgent.Evals.Harness;

internal sealed class FixtureToolGateway : IToolGateway
{
    private readonly Dictionary<string, ToolFixture> _fixtures;
    private readonly IReadOnlySet<string> _allowlist;
    private readonly IReadOnlyToolRegistry _registry;
    private readonly List<ToolInvocation> _invocations = [];

    public FixtureToolGateway(IEnumerable<ToolFixture> fixtures, IReadOnlySet<string> allowlist, IReadOnlyToolRegistry registry)
    {
        _allowlist = allowlist;
        _registry = registry;
        _fixtures = fixtures.ToDictionary(Key, StringComparer.Ordinal);
    }

    public IReadOnlyList<ToolInvocation> Invocations => _invocations;

    public async Task<ToolExecutionResult> ExecuteAsync(
        ToolExecutionRequest request,
        ToolExecutionContext context,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        string canonical = CanonicalJson.Canonicalize(request.Arguments);
        bool allowed = _allowlist.Contains(request.ToolName) && context.RiskCeiling == RiskLevel.ReadOnly;

        // Ask the same registry the production gateway consults: read-only is
        // whatever the registered descriptor says, not a name heuristic. An
        // unregistered tool cannot be honoured and is treated as not read-only.
        ToolDescriptor? descriptor = await _registry.FindAsync(request.ToolName, request.ToolVersion, cancellationToken).ConfigureAwait(false);
        bool readOnly = descriptor is not null && descriptor.Risk == RiskLevel.ReadOnly;

        bool matched = _fixtures.TryGetValue(Key(request.ToolName, request.ToolVersion, canonical), out ToolFixture? fixture);
        Guid? returnedEvidenceId = matched && fixture is not null ? fixture.EvidenceId : null;
        _invocations.Add(new ToolInvocation(request.ToolName, request.ToolVersion, canonical, allowed, readOnly, matched, returnedEvidenceId));

        ToolCallId callId = DeterministicToolCallId(request.ToolName, canonical);
        if (!allowed || !readOnly)
        {
            return ToolExecutionResult.Failure(
                callId,
                Error.Permanent(ApplicationErrorCodes.ToolPolicyDenied, "Eval tool request was outside the pinned read-only allowlist."),
                TimeSpan.FromMilliseconds(5));
        }

        if (!matched || fixture is null)
        {
            return ToolExecutionResult.Failure(
                callId,
                Error.Permanent(ApplicationErrorCodes.ToolArgumentsInvalid, "No canonical eval fixture matched the requested tool and arguments."),
                TimeSpan.FromMilliseconds(5));
        }

        return ToolExecutionResult.Success(
            callId,
            new EvidenceId(fixture.EvidenceId),
            fixture.Summary,
            fixture.RedactedResult.GetRawText(),
            TimeSpan.FromMilliseconds(5),
            fixture.IsTruncated,
            fixture.RedactedValueCount);
    }

    internal static string Key(ToolFixture fixture)
        => Key(fixture.Tool, fixture.Version, CanonicalJson.Canonicalize(fixture.Arguments));

    internal static string Key(string tool, string version, string canonicalArguments)
        => string.Concat(tool, "|", version, "|", canonicalArguments);

    private static ToolCallId DeterministicToolCallId(string tool, string canonicalArguments)
    {
        byte[] hash = System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(tool + "|" + canonicalArguments));
        return new ToolCallId(new Guid(hash.AsSpan(0, 16)));
    }
}
