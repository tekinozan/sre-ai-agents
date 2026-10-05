using System.Text.Json;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using NSubstitute;
using SreAiAgent.Application.Abstractions.Llm;
using SreAiAgent.Contracts.Messaging;
using SreAiAgent.Domain.Budgets;
using SreAiAgent.Domain.Findings;
using SreAiAgent.Domain.Incidents;
using SreAiAgent.Domain.Notifications;
using SreAiAgent.Domain.Shared;
using SreAiAgent.Infrastructure.Configuration;
using SreAiAgent.Infrastructure.Notifications;

namespace SreAiAgent.Infrastructure.Tests;

/// <summary>Covers what is sent to Slack and Teams, and where (D48).</summary>
public sealed class NotificationTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 5, 9, 0, 0, TimeSpan.Zero);

    private static NotificationMessage Sample(string title = "Checkout latency", string? summary = "Pool exhausted.")
        => new(
            NotificationEvents.InvestigationCompleted,
            "incident:1",
            NotificationTone.Good,
            title,
            [new("Service", "checkout"), new("Outcome", "Complete")],
            summary);

    [Fact]
    public void SlackTextIsEscapedSoAnAlertCannotPageTheWholeChannel()
    {
        // An alert title or a model-written summary is not the platform's own
        // text. "<!channel>" in either would notify everyone in the channel.
        string json = NotificationFormats.Serialize(
            NotificationChannelKind.Slack, Sample("<!channel> db down", "see <http://evil|docs> & retry"));

        JsonElement body = JsonDocument.Parse(json).RootElement;
        string text = body.GetProperty("text").GetString()!;
        string summary = body.GetProperty("blocks")[2].GetProperty("text").GetProperty("text").GetString()!;

        text.ShouldNotContain("<!channel>");
        text.ShouldContain("&lt;!channel&gt; db down");
        summary.ShouldNotContain("<http://evil|docs>");
        summary.ShouldContain("&amp; retry");
        body.GetProperty("blocks")[1].GetProperty("fields")[0].GetProperty("text").GetString().ShouldBe("*Service*\ncheckout");
    }

    [Fact]
    public void TeamsReceivesAnAdaptiveCardInsideAMessage()
    {
        JsonElement body = JsonDocument.Parse(NotificationFormats.Serialize(NotificationChannelKind.Teams, Sample())).RootElement;

        body.GetProperty("type").GetString().ShouldBe("message");
        JsonElement attachment = body.GetProperty("attachments")[0];
        attachment.GetProperty("contentType").GetString().ShouldBe("application/vnd.microsoft.card.adaptive");
        JsonElement card = attachment.GetProperty("content");
        card.GetProperty("type").GetString().ShouldBe("AdaptiveCard");
        card.GetProperty("$schema").GetString().ShouldBe("http://adaptivecards.io/schemas/adaptive-card.json");
        card.GetProperty("body")[0].GetProperty("text").GetString().ShouldBe("Checkout latency");
        card.GetProperty("body")[1].GetProperty("facts")[1].GetProperty("value").GetString().ShouldBe("Complete");
        card.GetProperty("body")[2].GetProperty("text").GetString().ShouldBe("Pool exhausted.");
    }

    [Theory]
    [InlineData(NotificationChannelKind.Slack, "https://hooks.slack.com/services/T/B/x", true)]
    [InlineData(NotificationChannelKind.Teams, "https://acme.webhook.office.com/webhookb2/x", true)]
    [InlineData(NotificationChannelKind.Teams, "https://prod-12.westeurope.logic.azure.com:443/workflows/x", true)]
    [InlineData(NotificationChannelKind.Teams, "https://acme.environment.api.powerplatform.com/powerautomate/x", true)]
    [InlineData(NotificationChannelKind.Slack, "http://hooks.slack.com/services/x", false)]
    [InlineData(NotificationChannelKind.Slack, "https://hooks.slack.com.evil.example/x", false)]
    [InlineData(NotificationChannelKind.Teams, "https://hooks.slack.com/services/x", false)]
    [InlineData(NotificationChannelKind.Slack, "https://169.254.169.254/latest", false)]
    [InlineData(NotificationChannelKind.Slack, "file:///etc/passwd", false)]
    public void ProductionCallsOnlyTheChannelsOwnHostsOverHttps(NotificationChannelKind kind, string url, bool allowed)
        => Policy(production: true).Validate(kind, url).IsSuccess.ShouldBe(allowed);

    [Fact]
    public void AnExplicitlyAllowedHostIsAcceptedInProduction()
        => Policy(production: true, "chat.internal.example")
            .Validate(NotificationChannelKind.Slack, "https://chat.internal.example/hook").IsSuccess.ShouldBeTrue();

    [Fact]
    public void DevelopmentAllowsALocalReceiver()
        => Policy(production: false).Validate(NotificationChannelKind.Slack, "http://webhook-sink:8080/x").IsSuccess.ShouldBeTrue();

    [Fact]
    public void ACompletedInvestigationIsDescribedFromThePlatformsOwnFields()
    {
        Incident incident = NewIncident();
        Guid eventId = Guid.CreateVersion7();

        NotificationMessage message = IncidentNotificationConsumer.Describe(
            eventId,
            new IncidentCompletedPayload(incident.Id, IncidentStatus.Resolved, InvestigationOutcome.Partial, 1, null),
            incident,
            new string('x', 2_000));

        message.Event.ShouldBe(NotificationEvents.InvestigationCompleted);
        message.Tone.ShouldBe(NotificationTone.Warning, "A partial report is not a clean success.");
        message.DeliveryKey.ShouldBe($"incident:{eventId:N}");
        message.Title.ShouldBe("Investigation complete: Checkout latency above threshold");
        message.Facts.ShouldContain(new KeyValuePair<string, string>("Outcome", "Partial"));
        message.Summary!.Length.ShouldBe(600);
    }

    [Fact]
    public void AFailedInvestigationCarriesItsErrorCode()
    {
        Incident incident = NewIncident();

        NotificationMessage message = IncidentNotificationConsumer.Describe(
            Guid.CreateVersion7(),
            new IncidentCompletedPayload(incident.Id, IncidentStatus.Failed, InvestigationOutcome.BudgetExceeded, 0, new ErrorCode("BUDGET_EXCEEDED")),
            incident,
            "The tenant's monthly model budget is spent.");

        message.Event.ShouldBe(NotificationEvents.InvestigationFailed);
        message.Tone.ShouldBe(NotificationTone.Critical);
        message.Facts.ShouldContain(new KeyValuePair<string, string>("Error", "BUDGET_EXCEEDED"));
    }

    [Fact]
    public void ABudgetCrossingIsKeyedByTenantPeriodAndThreshold()
    {
        // The key is what stops every minute's check re-sending the same warning.
        TenantBudget budget = TenantBudget.Create("acme", 100m, "USD", BudgetPeriod.Monthly, Now, Now, "alice");
        TenantBudgetStatus status = new(budget, budget.PeriodStart(Now), 85m);

        NotificationMessage warning = BudgetThresholdMonitor.Describe(status, 0.8m);
        NotificationMessage spent = BudgetThresholdMonitor.Describe(status with { Spent = 100m }, 1.0m);

        warning.DeliveryKey.ShouldBe($"budget:acme:{budget.PeriodStart(Now).ToUnixTimeSeconds()}:80");
        warning.Tone.ShouldBe(NotificationTone.Warning);
        spent.DeliveryKey.ShouldEndWith(":100");
        spent.Tone.ShouldBe(NotificationTone.Critical);
        spent.Facts.ShouldContain(new KeyValuePair<string, string>("Spent", "100.00 USD"));
    }

    [Fact]
    public void AChannelHearsOnlyTheEventsItSubscribedTo()
    {
        NotificationChannel channel = NotificationChannel.Create(
            "acme", NotificationChannelKind.Teams, "oncall", "env://TEAMS_HOOK", NotificationEvents.InvestigationFailed, Now, "alice");

        channel.Wants(NotificationEvents.InvestigationFailed).ShouldBeTrue();
        channel.Wants(NotificationEvents.InvestigationCompleted).ShouldBeFalse();
        Should.Throw<DomainException>(() => NotificationChannel.Create(
            "acme", NotificationChannelKind.Slack, "x", "env://X", NotificationEvents.None, Now, "alice"));
    }

    private static WebhookEndpointPolicy Policy(bool production, params string[] hosts)
    {
        IHostEnvironment environment = Substitute.For<IHostEnvironment>();
        environment.EnvironmentName.Returns(production ? Environments.Production : Environments.Development);
        return new WebhookEndpointPolicy(environment, Options.Create(new NotificationOptions { AllowedWebhookHosts = hosts }));
    }

    private static Incident NewIncident()
    {
        Incident incident = new(
            IncidentId.New(),
            new NewIncident(
                "acme", "alertmanager", "alert-1", "fingerprint-1",
                "Checkout latency above threshold", null, "checkout", "production", IncidentSeverity.High, Now),
            Now);
        incident.DequeueEvents();
        return incident;
    }
}
