using System.Security.Cryptography;
using System.Text.Json;
using SreAiAgent.Application.Abstractions.Llm;
using SreAiAgent.Application.Errors;
using SreAiAgent.Domain.Shared;

namespace SreAiAgent.Infrastructure.Llm;

/// <summary>The verdict the control plane derived from an eval summary, and the bytes' hash.</summary>
internal sealed record VerifiedEvaluationSummary(bool Passed, string ArtifactHash);

/// <summary>
/// Checks that an eval harness summary is evidence for the release it is
/// offered for (incidents-feedback-evals.md E-1, D50).
/// </summary>
/// <remarks>
/// What this establishes is identity and consistency, not arithmetic: the
/// summary names this release, its one deployment and that deployment's
/// provider revision, by id and hash; it was run on the dataset and against
/// the baseline the requester asked for; and its gate result agrees with
/// itself. The pass/fail is derived here from the gate rather than accepted as
/// a flag. Whether the runner computed the gate honestly is what separation of
/// duties is for.
///
/// Parsed with <see cref="JsonDocument"/> rather than a contract type: the
/// summary is the harness's artifact, and the platform reads only the members
/// it checks, so a field the harness adds later does not break completion.
/// </remarks>
internal static class EvaluationSummaryVerifier
{
    public const string SchemaVersion = "sre-eval-summary/v2";

    /// <summary>The baseline name that asks for a first run against a synthesized baseline.</summary>
    public const string BootstrapBaseline = "bootstrap";

    /// <summary>Summaries are a few kilobytes; anything near this is not one.</summary>
    public const int MaximumBytes = 256 * 1024;

    public static Result<VerifiedEvaluationSummary> Verify(
        ReadOnlyMemory<byte> summary,
        string requestedDataset,
        string requestedBaseline,
        PinnedModelAssignment release)
    {
        ArgumentNullException.ThrowIfNull(release);

        if (summary.IsEmpty || summary.Length > MaximumBytes)
        {
            return Invalid("The body must be the harness's summary.json.");
        }

        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(summary);
        }
        catch (JsonException)
        {
            return Invalid("The summary is not JSON.");
        }

        using (document)
        {
            try
            {
                return Check(document.RootElement, requestedDataset, requestedBaseline, release, summary.Span);
            }
            catch (Exception exception) when (exception is KeyNotFoundException or InvalidOperationException or FormatException)
            {
                // A member missing or of the wrong JSON kind: not a v2 summary.
                return Invalid("The summary is missing a member or has one of the wrong type.");
            }
        }
    }

    private static Result<VerifiedEvaluationSummary> Check(
        JsonElement root,
        string requestedDataset,
        string requestedBaseline,
        PinnedModelAssignment release,
        ReadOnlySpan<byte> bytes)
    {
        if (!string.Equals(root.GetProperty("schema_version").GetString(), SchemaVersion, StringComparison.Ordinal))
        {
            return Invalid($"Only {SchemaVersion} summaries can complete an evaluation.");
        }

        JsonElement assignment = root.GetProperty("assignment_release");
        if (!Guid.TryParse(assignment.GetProperty("id").GetString(), out Guid releaseId)
            || releaseId != release.ReleaseId.Value
            || !string.Equals(assignment.GetProperty("hash").GetString(), release.ConfigurationHash, StringComparison.Ordinal))
        {
            return Invalid("The summary evaluated a different assignment release.");
        }

        // The harness runs one deployment for every slot. A release that spreads
        // its slots over several deployments has no single summary that covers
        // it, and accepting one would promote models nobody evaluated.
        PinnedModelCandidate[] slots = [release.Classifier, release.Correlator, .. release.Agents.Values];
        JsonElement deployment = root.GetProperty("model_deployment");
        JsonElement revision = root.GetProperty("provider_connection_revision");
        foreach (PinnedModelCandidate slot in slots)
        {
            if (!Matches(deployment, slot.DeploymentVersionId.Value, slot.DeploymentVersion, slot.DeploymentConfigurationHash))
            {
                return Invalid($"The summary's deployment is not the one the '{slot.Slot}' slot uses; "
                    + "a release whose slots use different deployments cannot be evaluated by one run.");
            }

            if (!Matches(revision, slot.ProviderConnectionRevisionId.Value, slot.ProviderConnectionRevision, slot.ProviderConfigurationHash))
            {
                return Invalid($"The summary's provider revision is not the one the '{slot.Slot}' slot uses.");
            }
        }

        JsonElement dataset = root.GetProperty("dataset");
        string datasetName = dataset.GetProperty("id").GetString() + "@" + dataset.GetProperty("version").GetString();
        if (!string.Equals(datasetName, requestedDataset, StringComparison.Ordinal))
        {
            return Invalid($"The evaluation was requested on '{requestedDataset}', the summary ran '{datasetName}'.");
        }

        JsonElement baseline = root.GetProperty("baseline");
        bool bootstrap = baseline.GetProperty("bootstrap").GetBoolean();
        bool bootstrapRequested = string.Equals(requestedBaseline, BootstrapBaseline, StringComparison.Ordinal);
        if (bootstrap != bootstrapRequested
            || (!bootstrap && !string.Equals(baseline.GetProperty("id").GetString(), requestedBaseline, StringComparison.Ordinal)))
        {
            return Invalid(bootstrap
                ? "The summary ran against a synthesized baseline; request the evaluation with baseline 'bootstrap' for that."
                : $"The evaluation was requested against baseline '{requestedBaseline}', the summary used another.");
        }

        JsonElement gate = root.GetProperty("gate");
        bool eligible = gate.GetProperty("promotion_eligible").GetBoolean();
        int failures = gate.GetProperty("failures").GetArrayLength();
        int hardFailures = root.GetProperty("aggregate").GetProperty("hard_failure_count").GetInt32();
        if (eligible && (failures > 0 || hardFailures > 0))
        {
            return Invalid("The summary claims promotion eligibility while listing gate or hard failures.");
        }

        string hash = Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
        return Result<VerifiedEvaluationSummary>.Success(new VerifiedEvaluationSummary(eligible, hash));
    }

    private static bool Matches(JsonElement identity, Guid id, int version, string hash)
        => Guid.TryParse(identity.GetProperty("id").GetString(), out Guid parsed)
            && parsed == id
            && string.Equals(identity.GetProperty("version").GetString(), version.ToString(CultureInfo.InvariantCulture), StringComparison.Ordinal)
            && string.Equals(identity.GetProperty("hash").GetString(), hash, StringComparison.Ordinal);

    private static Result<VerifiedEvaluationSummary> Invalid(string message)
        => Result<VerifiedEvaluationSummary>.Failure(Error.Permanent(ApplicationErrorCodes.ModelEvaluationSummaryInvalid, message));
}
