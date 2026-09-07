using System.Collections.Frozen;
using System.Text.Json;
using SreAiAgent.Application.Errors;
using SreAiAgent.Contracts.Alerts;
using SreAiAgent.Contracts.Incidents;
using SreAiAgent.Domain.Shared;

namespace SreAiAgent.Infrastructure.Alerts;

/// <summary>
/// Turns a provider payload into the request the incident use case takes.
/// </summary>
/// <remarks>
/// Both ingestion paths converge here and then on the same handler: an alert
/// arriving over HTTP and one arriving on a topic produce the same incident,
/// deduplicate against each other and share one set of rules. Two code paths
/// that both create incidents would eventually disagree about what counts as a
/// duplicate.
/// </remarks>
public sealed class AlertNormalizer
{
    private readonly FrozenDictionary<string, IAlertSourceAdapter> _adapters;

    internal AlertNormalizer(IEnumerable<IAlertSourceAdapter> adapters)
    {
        ArgumentNullException.ThrowIfNull(adapters);

        _adapters = adapters.ToFrozenDictionary(
            static adapter => adapter.Source, StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>The sources this deployment can accept.</summary>
    public IReadOnlyCollection<string> SupportedSources => _adapters.Keys;

    /// <summary>Normalizes a payload from a named source.</summary>
    public Result<CreateIncidentRequest> Normalize(string source, JsonElement payload)
    {
        if (!_adapters.TryGetValue(source, out IAlertSourceAdapter? adapter))
        {
            // An allowlist. Falling back to the generic webhook adapter for an
            // unknown source would silently accept anything and attribute it to a
            // provider nobody configured.
            return Result<CreateIncidentRequest>.Failure(Error.Permanent(
                ApplicationErrorCodes.ValidationFailed,
                $"No adapter is configured for alert source '{source}'."));
        }

        Result<CanonicalAlert> alert = adapter.Normalize(payload);

        return alert.IsFailure
            ? Result<CreateIncidentRequest>.Failure(alert.Error)
            : Result<CreateIncidentRequest>.Success(ToRequest(alert.Value));
    }

    /// <summary>Maps a canonical alert onto the incident request.</summary>
    /// <remarks>
    /// A resolving alert opens the incident without starting an investigation.
    /// The condition has already cleared, so spending a model budget on it would
    /// produce a report about something that is no longer happening; the incident
    /// still exists because the fact that it happened is worth recording.
    /// </remarks>
    public static CreateIncidentRequest ToRequest(CanonicalAlert alert)
    {
        ArgumentNullException.ThrowIfNull(alert);

        return new CreateIncidentRequest
        {
            Source = alert.Source,
            ExternalId = alert.ExternalId,
            Fingerprint = alert.Fingerprint,
            Title = alert.Title,
            Description = alert.Description,
            Service = alert.Service,
            Environment = alert.Environment,
            Severity = alert.Severity,
            DetectedAt = alert.StartedAt,
            Labels = alert.Labels,
            Annotations = alert.Annotations,
            IdentityLabelKeys = alert.IdentityLabelKeys,
            StartInvestigation = !alert.IsResolved,
        };
    }

    /// <summary>
    /// Forces the alert onto the caller's tenant.
    /// </summary>
    /// <remarks>
    /// An alert arrives from a monitoring system, but it arrives through an
    /// authenticated caller, and that token is the only trustworthy statement of
    /// which tenant it belongs to. A payload naming a different tenant is refused
    /// rather than corrected silently: writing an incident into someone else's
    /// tenant is exactly what this check exists to stop.
    /// </remarks>
    public static Result<CreateIncidentRequest> BindTenant(CreateIncidentRequest request, string tenant)
    {
        ArgumentNullException.ThrowIfNull(request);

        if (string.IsNullOrWhiteSpace(tenant))
        {
            return Result<CreateIncidentRequest>.Failure(Error.Permanent(
                ApplicationErrorCodes.Forbidden, "The caller has no tenant claim."));
        }

        if (request.Labels is not null
            && request.Labels.TryGetValue("tenant", out string? claimed)
            && !string.IsNullOrWhiteSpace(claimed)
            && !string.Equals(claimed, tenant, StringComparison.Ordinal))
        {
            return Result<CreateIncidentRequest>.Failure(Error.Permanent(
                ApplicationErrorCodes.Forbidden,
                "The alert payload names a different tenant than the caller."));
        }

        Dictionary<string, string> labels = request.Labels is null
            ? new Dictionary<string, string>(StringComparer.Ordinal)
            : new Dictionary<string, string>(request.Labels, StringComparer.Ordinal);

        labels["tenant"] = tenant;

        return Result<CreateIncidentRequest>.Success(request with { Labels = labels });
    }
}
