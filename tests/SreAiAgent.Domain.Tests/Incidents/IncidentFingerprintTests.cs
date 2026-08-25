using SreAiAgent.Domain.Incidents;

namespace SreAiAgent.Domain.Tests.Incidents;

/// <summary>
/// Covers the deduplication key of ARCHITECTURE.md 9.3. Every property here has
/// a direct operational consequence: a fingerprint that is unstable duplicates
/// incidents, and one that collides merges unrelated failures.
/// </summary>
public sealed class IncidentFingerprintTests
{
    [Fact]
    public void SameInputProducesSameFingerprint()
    {
        string first = IncidentFingerprint.Compute("alertmanager", "checkout", "production", "HighLatency");
        string second = IncidentFingerprint.Compute("alertmanager", "checkout", "production", "HighLatency");

        second.ShouldBe(first);
    }

    [Fact]
    public void FingerprintIsLowercaseHexSha256()
    {
        string fingerprint = IncidentFingerprint.Compute("alertmanager", "checkout", "production", "HighLatency");

        fingerprint.Length.ShouldBe(64);
        fingerprint.ShouldAllBe(static c => char.IsAsciiDigit(c) || (c >= 'a' && c <= 'f'));
    }

    [Fact]
    public void CasingAndSurroundingWhitespaceDoNotChangeIdentity()
    {
        string canonical = IncidentFingerprint.Compute("alertmanager", "checkout", "production", "HighLatency");
        string noisy = IncidentFingerprint.Compute("  AlertManager ", "CHECKOUT", " Production", "highlatency  ");

        noisy.ShouldBe(canonical);
    }

    [Fact]
    public void LabelOrderDoesNotChangeIdentity()
    {
        KeyValuePair<string, string>[] ascending =
        [
            new("cluster", "eu-1"),
            new("namespace", "shop"),
        ];
        KeyValuePair<string, string>[] descending =
        [
            new("namespace", "shop"),
            new("cluster", "eu-1"),
        ];

        IncidentFingerprint.Compute("alertmanager", "checkout", "production", "HighLatency", descending)
            .ShouldBe(IncidentFingerprint.Compute("alertmanager", "checkout", "production", "HighLatency", ascending));
    }

    [Fact]
    public void DifferentEnvironmentsAreDifferentIncidents()
    {
        string production = IncidentFingerprint.Compute("alertmanager", "checkout", "production", "HighLatency");
        string staging = IncidentFingerprint.Compute("alertmanager", "checkout", "staging", "HighLatency");

        staging.ShouldNotBe(production);
    }

    [Fact]
    public void AdjacentFieldsCannotBeConfusedForEachOther()
    {
        // Without a field separator, ("ab", "c") and ("a", "bc") would hash the
        // same bytes and merge two unrelated services into one incident.
        string first = IncidentFingerprint.Compute("alertmanager", "ab", "c", "HighLatency");
        string second = IncidentFingerprint.Compute("alertmanager", "a", "bc", "HighLatency");

        second.ShouldNotBe(first);
    }

    [Fact]
    public void AddingAnIdentityLabelChangesTheFingerprint()
    {
        string withoutLabels = IncidentFingerprint.Compute("alertmanager", "checkout", "production", "HighLatency");
        string withLabels = IncidentFingerprint.Compute(
            "alertmanager", "checkout", "production", "HighLatency", [new KeyValuePair<string, string>("cluster", "eu-1")]);

        withLabels.ShouldNotBe(withoutLabels);
    }

    [Fact]
    public void LongValuesBeyondTheStackBufferStillHashConsistently()
    {
        string longService = new('s', 4096);

        string first = IncidentFingerprint.Compute("alertmanager", longService, "production", "HighLatency");
        string second = IncidentFingerprint.Compute("alertmanager", longService, "production", "HighLatency");

        second.ShouldBe(first);
        second.ShouldNotBe(IncidentFingerprint.Compute("alertmanager", longService + "x", "production", "HighLatency"));
    }
}
