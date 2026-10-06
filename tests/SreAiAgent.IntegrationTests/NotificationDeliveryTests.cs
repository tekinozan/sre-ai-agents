using System.Net;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NSubstitute;
using SreAiAgent.Application.Abstractions.Llm;
using SreAiAgent.Application.Abstractions.Notifications;
using SreAiAgent.Application.Errors;
using SreAiAgent.Domain.Notifications;
using SreAiAgent.Domain.Shared;
using SreAiAgent.Infrastructure;
using SreAiAgent.Infrastructure.Configuration;
using SreAiAgent.Infrastructure.Notifications;

namespace SreAiAgent.IntegrationTests;

/// <summary>
/// Proves that each channel hears each notification once, against a real
/// database (gemini-budgets-notifications.md N, D48).
/// </summary>
[Collection(PostgresCollection.Name)]
[Trait("Category", "Integration")]
public sealed class NotificationDeliveryTests(PostgresFixture postgres)
{
    [Fact]
    public async Task EachChannelThatWantsAnEventHearsItOnceAndAFailedSendIsRetried()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        string tenant = "notify-" + Guid.NewGuid();
        RecordingHandler webhooks = new();
        await using ServiceProvider provider = Build(webhooks);
        await using AsyncServiceScope scope = provider.CreateAsyncScope();
        INotificationChannelService channels = scope.ServiceProvider.GetRequiredService<INotificationChannelService>();

        (await channels.CreateAsync(tenant, NotificationChannelKind.Slack, "sre-alerts", "env://SLACK_HOOK", NotificationEvents.All, "alice", ct))
            .IsSuccess.ShouldBeTrue();
        (await channels.CreateAsync(tenant, NotificationChannelKind.Teams, "oncall", "env://TEAMS_HOOK", NotificationEvents.InvestigationFailed, "alice", ct))
            .IsSuccess.ShouldBeTrue();

        NotificationDispatcher dispatcher = scope.ServiceProvider.GetRequiredService<NotificationDispatcher>();
        NotificationMessage completed = new(
            NotificationEvents.InvestigationCompleted, $"incident:{Guid.NewGuid():N}", NotificationTone.Good,
            "Investigation complete: checkout", [new("Service", "checkout")], null);

        // Only the Slack channel subscribed to completions.
        (await dispatcher.DispatchAsync(tenant, completed, ct)).IsSuccess.ShouldBeTrue();
        webhooks.Hosts.ShouldBe(["slack.test"]);

        // A redelivery of the same event tells nobody again.
        (await dispatcher.DispatchAsync(tenant, completed, ct)).IsSuccess.ShouldBeTrue();
        webhooks.Hosts.Count.ShouldBe(1);

        // A failure releases the claim, fails the dispatch so the caller
        // retries, and the retry sends.
        NotificationMessage failed = completed with
        {
            Event = NotificationEvents.InvestigationFailed,
            DeliveryKey = $"incident:{Guid.NewGuid():N}",
        };
        webhooks.FailHost = "teams.test";
        Result first = await dispatcher.DispatchAsync(tenant, failed, ct);
        first.IsFailure.ShouldBeTrue();
        first.Error.Code.ShouldBe(ApplicationErrorCodes.NotificationDeliveryFailed);
        ErrorCatalog.Describe(first.Error.Code).IsRetryable.ShouldBeTrue();

        webhooks.FailHost = null;
        (await dispatcher.DispatchAsync(tenant, failed, ct)).IsSuccess.ShouldBeTrue();
        // Slack (subscribed to everything) heard the failure once, on the
        // first attempt; Teams was tried, failed, and was tried again. Channel
        // order within one dispatch is not part of the contract.
        webhooks.Hosts.Count(static host => host == "slack.test").ShouldBe(2);
        webhooks.Hosts.Count(static host => host == "teams.test").ShouldBe(2);
        webhooks.Hosts[^1].ShouldBe("teams.test");
    }

    [Fact]
    public async Task AWebhookUrlIsNeverStoredAndANameIsUniquePerTenant()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        string tenant = "notify-names-" + Guid.NewGuid();
        await using ServiceProvider provider = Build(new RecordingHandler());
        await using AsyncServiceScope scope = provider.CreateAsyncScope();
        INotificationChannelService channels = scope.ServiceProvider.GetRequiredService<INotificationChannelService>();

        Result<NotificationChannel> literal = await channels.CreateAsync(
            tenant, NotificationChannelKind.Slack, "raw", "https://hooks.slack.com/services/T/B/secret", NotificationEvents.All, "alice", ct);
        literal.IsFailure.ShouldBeTrue();
        literal.Error.Code.ShouldBe(DomainErrorCodes.NotificationChannelInvalid);

        (await channels.CreateAsync(tenant, NotificationChannelKind.Slack, "alerts", "env://SLACK_HOOK", NotificationEvents.All, "alice", ct))
            .IsSuccess.ShouldBeTrue();
        Result<NotificationChannel> duplicate = await channels.CreateAsync(
            tenant, NotificationChannelKind.Teams, "alerts", "env://TEAMS_HOOK", NotificationEvents.All, "alice", ct);
        duplicate.Error.Code.ShouldBe(ApplicationErrorCodes.NotificationChannelConflict);

        (await channels.ListAsync(tenant, ct)).Select(static channel => channel.WebhookRef).ShouldBe(["env://SLACK_HOOK"]);
        (await channels.ListAsync("someone-else", ct)).ShouldBeEmpty();
    }

    private ServiceProvider Build(RecordingHandler webhooks)
    {
        ServiceCollection services = new();
        services.AddSingleton(PostgresFixture.DatabaseOptions(postgres.ConnectionString));
        services.AddSrePersistence();
        services.AddSreNotifications(new ConfigurationBuilder().Build());
        services.AddHttpClient(NotificationSender.HttpClientName).ConfigurePrimaryHttpMessageHandler(() => webhooks);
        services.AddSingleton(TimeProvider.System);
        services.AddSingleton(Options.Create(new NotificationOptions()));
        services.AddSingleton(typeof(ILogger<>), typeof(NullLogger<>));

        IHostEnvironment environment = Substitute.For<IHostEnvironment>();
        environment.EnvironmentName.Returns(Environments.Development);
        services.AddSingleton(environment);

        ICredentialResolver credentials = Substitute.For<ICredentialResolver>();
        credentials.ResolveAsync("env://SLACK_HOOK", Arg.Any<CancellationToken>())
            .Returns(Result<CredentialValue>.Success(new CredentialValue("https://slack.test/hook")));
        credentials.ResolveAsync("env://TEAMS_HOOK", Arg.Any<CancellationToken>())
            .Returns(Result<CredentialValue>.Success(new CredentialValue("https://teams.test/hook")));
        services.AddSingleton(credentials);

        return services.BuildServiceProvider(validateScopes: true);
    }

    private sealed class RecordingHandler : HttpMessageHandler
    {
        public List<string> Hosts { get; } = [];

        public string? FailHost { get; set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            string host = request.RequestUri!.Host;
            Hosts.Add(host);
            return Task.FromResult(new HttpResponseMessage(
                string.Equals(host, FailHost, StringComparison.Ordinal) ? HttpStatusCode.ServiceUnavailable : HttpStatusCode.OK));
        }
    }
}
