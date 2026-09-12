using System.Text.Json;
using SreAiAgent.Contracts.Alerts;
using SreAiAgent.Domain.Shared;

namespace SreAiAgent.Infrastructure.Alerts;

/// <summary>
/// Normalizes one provider's payload into a <see cref="CanonicalAlert"/>
/// (ARCHITECTURE.md 10.1).
/// </summary>
/// <remarks>
/// This is the only place in the platform that knows what an Alertmanager
/// payload looks like. Everything downstream, deduplication, routing, the
/// incident aggregate, sees the canonical shape, which is what allows a sixth
/// alert source to be added without touching any of it.
///
/// An adapter never throws on bad input. A provider sending something unexpected
/// is a routine event, and an exception here would either drop the alert or stall
/// the partition it arrived on; a Result lets the caller dead-letter it with the
/// reason attached.
/// </remarks>
internal interface IAlertSourceAdapter
{
    /// <summary>The source key, for example <c>alertmanager</c>.</summary>
    string Source { get; }

    /// <summary>
    /// Maps a provider payload onto the canonical shape.
    /// </summary>
    /// <remarks>
    /// A webhook delivery is a batch even when it carries one alert — Alertmanager
    /// always posts the group envelope, never a bare alert — so the port returns
    /// every alert the payload named rather than assuming there is exactly one. An
    /// empty list is never a valid success: a payload that named zero alerts is
    /// malformed, not an empty batch to shrug off.
    /// </remarks>
    Result<IReadOnlyList<CanonicalAlert>> Normalize(JsonElement payload);
}
