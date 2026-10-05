using System.Text.Json;
using System.Text.Json.Serialization;
using SreAiAgent.Domain.Notifications;

namespace SreAiAgent.Infrastructure.Notifications;

/// <summary>How urgent a notification reads.</summary>
internal enum NotificationTone
{
    Good,
    Warning,
    Critical,
}

/// <summary>
/// What is said, before it is shaped for Slack or Teams.
/// </summary>
/// <param name="Event">Which subscription it belongs to.</param>
/// <param name="DeliveryKey">Identity of this notification, so a channel hears it once.</param>
/// <param name="Tone">How urgent it reads.</param>
/// <param name="Title">One line.</param>
/// <param name="Facts">Label and value pairs, in display order.</param>
/// <param name="Summary">Optional prose, already truncated.</param>
internal sealed record NotificationMessage(
    NotificationEvents Event,
    string DeliveryKey,
    NotificationTone Tone,
    string Title,
    IReadOnlyList<KeyValuePair<string, string>> Facts,
    string? Summary);

// Slack incoming webhook: https://api.slack.com/messaging/webhooks
internal sealed record SlackMessage(
    [property: JsonPropertyName("text")] string Text,
    [property: JsonPropertyName("blocks")] IReadOnlyList<SlackBlock> Blocks);

internal sealed record SlackBlock(
    [property: JsonPropertyName("type")] string Type,
    [property: JsonPropertyName("text")] SlackText? Text = null,
    [property: JsonPropertyName("fields")] IReadOnlyList<SlackText>? Fields = null);

internal sealed record SlackText(
    [property: JsonPropertyName("type")] string Type,
    [property: JsonPropertyName("text")] string Text);

// Teams Workflows webhook carrying an Adaptive Card. The Office 365 connector
// format it replaces was retired by Microsoft, so only this shape is sent.
internal sealed record TeamsMessage(
    [property: JsonPropertyName("type")] string Type,
    [property: JsonPropertyName("attachments")] IReadOnlyList<TeamsAttachment> Attachments);

internal sealed record TeamsAttachment(
    [property: JsonPropertyName("contentType")] string ContentType,
    [property: JsonPropertyName("content")] AdaptiveCard Content);

internal sealed record AdaptiveCard(
    [property: JsonPropertyName("$schema")] string Schema,
    [property: JsonPropertyName("type")] string Type,
    [property: JsonPropertyName("version")] string Version,
    [property: JsonPropertyName("body")] IReadOnlyList<AdaptiveElement> Body);

internal sealed record AdaptiveElement(
    [property: JsonPropertyName("type")] string Type,
    [property: JsonPropertyName("text")] string? Text = null,
    [property: JsonPropertyName("weight")] string? Weight = null,
    [property: JsonPropertyName("size")] string? Size = null,
    [property: JsonPropertyName("color")] string? Color = null,
    [property: JsonPropertyName("wrap")] bool? Wrap = null,
    [property: JsonPropertyName("facts")] IReadOnlyList<AdaptiveFact>? Facts = null);

internal sealed record AdaptiveFact(
    [property: JsonPropertyName("title")] string Title,
    [property: JsonPropertyName("value")] string Value);

[JsonSourceGenerationOptions(DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
[JsonSerializable(typeof(SlackMessage))]
[JsonSerializable(typeof(TeamsMessage))]
internal sealed partial class NotificationJsonContext : JsonSerializerContext;

/// <summary>Shapes a message for each destination.</summary>
internal static class NotificationFormats
{
    /// <summary>The Slack payload.</summary>
    /// <remarks>
    /// Escaped as Slack asks: an alert title or a model-written summary that
    /// contained <c>&lt;!channel&gt;</c> would otherwise page everyone in the
    /// channel, and a link in <c>&lt;url|text&gt;</c> form could disguise its target.
    /// </remarks>
    public static SlackMessage Slack(NotificationMessage message)
    {
        string title = $"{Icon(message.Tone)} {Escape(message.Title)}";
        List<SlackBlock> blocks =
        [
            new("section", new SlackText("mrkdwn", $"*{title}*")),
            new("section", Fields: message.Facts
                .Select(static fact => new SlackText("mrkdwn", $"*{Escape(fact.Key)}*\n{Escape(fact.Value)}"))
                .ToArray()),
        ];
        if (!string.IsNullOrWhiteSpace(message.Summary))
        {
            blocks.Add(new SlackBlock("section", new SlackText("mrkdwn", Escape(message.Summary))));
        }

        return new SlackMessage(title, blocks);
    }

    /// <summary>The Teams payload.</summary>
    public static TeamsMessage Teams(NotificationMessage message)
    {
        List<AdaptiveElement> body =
        [
            new("TextBlock", message.Title, Weight: "Bolder", Size: "Medium", Color: Color(message.Tone), Wrap: true),
            new("FactSet", Facts: message.Facts.Select(static fact => new AdaptiveFact(fact.Key, fact.Value)).ToArray()),
        ];
        if (!string.IsNullOrWhiteSpace(message.Summary))
        {
            body.Add(new AdaptiveElement("TextBlock", message.Summary, Wrap: true));
        }

        return new TeamsMessage(
            "message",
            [
                new TeamsAttachment(
                    "application/vnd.microsoft.card.adaptive",
                    new AdaptiveCard("http://adaptivecards.io/schemas/adaptive-card.json", "AdaptiveCard", "1.4", body)),
            ]);
    }

    /// <summary>Serialises the payload for <paramref name="kind"/>.</summary>
    public static string Serialize(NotificationChannelKind kind, NotificationMessage message)
        => kind switch
        {
            NotificationChannelKind.Slack => JsonSerializer.Serialize(Slack(message), NotificationJsonContext.Default.SlackMessage),
            NotificationChannelKind.Teams => JsonSerializer.Serialize(Teams(message), NotificationJsonContext.Default.TeamsMessage),
            _ => throw new InvalidOperationException("The notification channel kind is not registered."),
        };

    internal static string Escape(string value)
        => value.Replace("&", "&amp;", StringComparison.Ordinal)
            .Replace("<", "&lt;", StringComparison.Ordinal)
            .Replace(">", "&gt;", StringComparison.Ordinal);

    private static string Icon(NotificationTone tone) => tone switch
    {
        NotificationTone.Good => ":white_check_mark:",
        NotificationTone.Warning => ":warning:",
        _ => ":rotating_light:",
    };

    private static string Color(NotificationTone tone) => tone switch
    {
        NotificationTone.Good => "Good",
        NotificationTone.Warning => "Warning",
        _ => "Attention",
    };
}
