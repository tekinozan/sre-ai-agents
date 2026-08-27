namespace SreAiAgent.Application.Abstractions.Persistence;

/// <summary>
/// Captures the release set in force right now (ARCHITECTURE.md 8.2 and 24.5).
/// </summary>
/// <remarks>
/// Pinning happens once, when an incident is opened, and everything the
/// investigation does afterwards resolves through that pin. This is what makes a
/// report reproducible and a regression attributable: a release promoted while an
/// investigation is running cannot change the prompts or model profiles it is
/// already using, so replaying it a week later produces the same work rather than
/// today's configuration applied to last week's incident.
///
/// A port rather than a direct read of configuration, because the layer that
/// knows which releases are current is the one that loaded them from disk, and
/// the application layer must not learn where they came from.
/// </remarks>
public interface IConfigurationPinner
{
    /// <summary>Builds an unsaved snapshot of the current releases and flag states.</summary>
    /// <param name="now">Pin time, from the injected clock.</param>
    ConfigurationSnapshot PinCurrent(DateTimeOffset now);
}
