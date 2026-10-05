using System.Text.Json;
using System.Text.Json.Serialization.Metadata;
using Confluent.Kafka;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Npgsql;
using NSubstitute;
using SreAiAgent.Application.Abstractions.Messaging;
using SreAiAgent.Application.Abstractions.Persistence;
using SreAiAgent.Contracts.Alerts;
using SreAiAgent.Contracts.Messaging;
using SreAiAgent.Contracts.Serialization;
using SreAiAgent.Domain.Incidents;
using SreAiAgent.Infrastructure.Configuration;
using SreAiAgent.Infrastructure.Messaging;

namespace SreAiAgent.Infrastructure.Tests;

public sealed class KafkaConsumerServiceTests
{
    private sealed class TestKafkaConsumerService(
        IServiceScopeFactory scopeFactory,
        IOptions<KafkaOptions> options,
        IMessageProducer producer,
        TimeProvider timeProvider,
        Func<EventEnvelope<CanonicalAlert>, IServiceProvider, CancellationToken, Task>? onHandle = null)
        : KafkaConsumerService<CanonicalAlert>(scopeFactory, options, producer, timeProvider, NullLogger.Instance)
    {
        protected override string Topic => "sre.alert.received.v1";
        protected override string ConsumerName => "test-alert-worker";
        protected override JsonTypeInfo<EventEnvelope<CanonicalAlert>> EnvelopeTypeInfo
            => SreAiAgentJsonContext.Default.EventEnvelopeCanonicalAlert;

        protected override Task HandleAsync(
            EventEnvelope<CanonicalAlert> envelope,
            IServiceProvider services,
            CancellationToken cancellationToken)
            => onHandle is not null
                ? onHandle(envelope, services, cancellationToken)
                : Task.CompletedTask;
    }

    private static EventEnvelope<CanonicalAlert> CreateSampleEnvelope()
    {
        CanonicalAlert alert = new(
            Source: "alertmanager",
            ExternalId: "ext-123",
            Fingerprint: "fingerprint-123",
            Title: "HighLatency",
            Description: "High latency detected",
            Service: "checkout",
            Environment: "production",
            Severity: IncidentSeverity.Critical,
            StartedAt: DateTimeOffset.UtcNow,
            Labels: new Dictionary<string, string> { ["tenant"] = "local" },
            Annotations: new Dictionary<string, string>(),
            RawPayloadReference: null);

        return new EventEnvelope<CanonicalAlert>(
            EventId: Guid.CreateVersion7(),
            EventType: "sre.alert.received",
            EventVersion: 1,
            OccurredAt: DateTimeOffset.UtcNow,
            CorrelationId: "corr-123",
            CausationId: null,
            Payload: alert);
    }

    private static ConsumeResult<string, byte[]> CreateConsumeResult(
        EventEnvelope<CanonicalAlert> envelope,
        string topic = "sre.alert.received.v1",
        int partition = 2,
        long offset = 555)
    {
        byte[] bytes = JsonSerializer.SerializeToUtf8Bytes(
            envelope,
            SreAiAgentJsonContext.Default.EventEnvelopeCanonicalAlert);

        return new ConsumeResult<string, byte[]>
        {
            Topic = topic,
            Partition = new Partition(partition),
            Offset = new Offset(offset),
            Message = new Message<string, byte[]>
            {
                Key = "key-1",
                Value = bytes,
                Headers = new Headers(),
            },
        };
    }

    [Fact]
    public async Task ProcessAsync_WhenInboxReturnsDuplicate_SettlesPriorAttemptBeforeCommit()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        EventEnvelope<CanonicalAlert> envelope = CreateSampleEnvelope();
        ConsumeResult<string, byte[]> result = CreateConsumeResult(envelope, topic: "sre.alert.received.v1", partition: 2, offset: 555);

        IConsumer<string, byte[]> consumer = Substitute.For<IConsumer<string, byte[]>>();
        IInboxStore inbox = Substitute.For<IInboxStore>();
        IDeliveryAttemptStore attempts = Substitute.For<IDeliveryAttemptStore>();
        IUnitOfWork unitOfWork = Substitute.For<IUnitOfWork>();
        IMessageProducer producer = Substitute.For<IMessageProducer>();

        inbox.TryBeginAsync(envelope.EventId, envelope.EventType, "test-alert-worker", ct)
            .Returns(Task.FromResult(false));

        IServiceScopeFactory scopeFactory = Substitute.For<IServiceScopeFactory>();
        IServiceScope scope = Substitute.For<IServiceScope>();
        IServiceProvider services = Substitute.For<IServiceProvider>();

        scopeFactory.CreateScope().Returns(scope);
        scope.ServiceProvider.Returns(services);
        services.GetService(typeof(IInboxStore)).Returns(inbox);
        services.GetService(typeof(IDeliveryAttemptStore)).Returns(attempts);
        services.GetService(typeof(IUnitOfWork)).Returns(unitOfWork);

        IOptions<KafkaOptions> options = Options.Create(new KafkaOptions
        {
            BootstrapServers = "localhost:9092",
            ConsumerGroup = "test-group",
        });

        TestKafkaConsumerService sut = new(scopeFactory, options, producer, TimeProvider.System);

        await sut.ProcessAsync(consumer, result, ct);

        Received.InOrder(() =>
        {
            _ = attempts.SettleAsync(
                "sre.alert.received.v1",
                2,
                555,
                "test-alert-worker",
                SettlementKind.Succeeded,
                ct);

            consumer.Commit(result);
        });
    }

    [Fact]
    public async Task ProcessAsync_WhenDbUpdateExceptionIsDuplicateDelivery_SettlesPriorAttemptBeforeCommit()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        EventEnvelope<CanonicalAlert> envelope = CreateSampleEnvelope();
        ConsumeResult<string, byte[]> result = CreateConsumeResult(envelope, topic: "sre.alert.received.v1", partition: 1, offset: 777);

        IConsumer<string, byte[]> consumer = Substitute.For<IConsumer<string, byte[]>>();
        IInboxStore inbox = Substitute.For<IInboxStore>();
        IDeliveryAttemptStore attempts = Substitute.For<IDeliveryAttemptStore>();
        IUnitOfWork unitOfWork = Substitute.For<IUnitOfWork>();
        IMessageProducer producer = Substitute.For<IMessageProducer>();

        inbox.TryBeginAsync(envelope.EventId, envelope.EventType, "test-alert-worker", ct)
            .Returns(Task.FromResult(true));

        PostgresException postgresException = new(
            "duplicate key value violates unique constraint",
            "ERROR",
            "ERROR",
            PostgresErrorCodes.UniqueViolation,
            tableName: "inbox_messages");
        DbUpdateException dbUpdateException = new("Unique constraint violation", postgresException);

        unitOfWork.SaveChangesAsync(ct).Returns<Task<int>>(_ => throw dbUpdateException);

        IServiceScopeFactory scopeFactory = Substitute.For<IServiceScopeFactory>();
        IServiceScope mainScope = Substitute.For<IServiceScope>();
        IServiceProvider mainServices = Substitute.For<IServiceProvider>();
        IServiceScope settlementScope = Substitute.For<IServiceScope>();
        IServiceProvider settlementServices = Substitute.For<IServiceProvider>();

        scopeFactory.CreateScope().Returns(mainScope, settlementScope);

        mainScope.ServiceProvider.Returns(mainServices);
        mainServices.GetService(typeof(IInboxStore)).Returns(inbox);
        mainServices.GetService(typeof(IUnitOfWork)).Returns(unitOfWork);
        mainServices.GetService(typeof(IDeliveryAttemptStore)).Returns(attempts);

        settlementScope.ServiceProvider.Returns(settlementServices);
        settlementServices.GetService(typeof(IDeliveryAttemptStore)).Returns(attempts);

        IOptions<KafkaOptions> options = Options.Create(new KafkaOptions
        {
            BootstrapServers = "localhost:9092",
            ConsumerGroup = "test-group",
        });

        TestKafkaConsumerService sut = new(scopeFactory, options, producer, TimeProvider.System);

        await sut.ProcessAsync(consumer, result, ct);

        Received.InOrder(() =>
        {
            _ = attempts.SettleAsync(
                "sre.alert.received.v1",
                1,
                777,
                "test-alert-worker",
                SettlementKind.Succeeded,
                ct);

            consumer.Commit(result);
        });
    }

    [Theory]
    [InlineData("idempotency_keys")]
    [InlineData("incidents")]
    public async Task ProcessAsync_WhenAnotherMessageWinsTheAlertRace_RetriesInsteadOfAcknowledging(string table)
    {
        // BUG-001: a unique violation keyed on the alert rather than on the
        // message is a different message losing a race. Acknowledging it
        // discarded the alert; it has to be retried so it folds into the winner.
        CancellationToken ct = TestContext.Current.CancellationToken;
        EventEnvelope<CanonicalAlert> envelope = CreateSampleEnvelope();
        ConsumeResult<string, byte[]> result = CreateConsumeResult(envelope, partition: 3, offset: 901);

        IConsumer<string, byte[]> consumer = Substitute.For<IConsumer<string, byte[]>>();
        IDeliveryAttemptStore attempts = Substitute.For<IDeliveryAttemptStore>();
        IUnitOfWork unitOfWork = Substitute.For<IUnitOfWork>();
        IServiceScopeFactory scopeFactory = FailingScopes(envelope, attempts, unitOfWork, attemptsSoFar: 1, ct);

        PostgresException raceLost = new(
            "duplicate key value violates unique constraint",
            "ERROR",
            "ERROR",
            PostgresErrorCodes.UniqueViolation,
            tableName: table);
        unitOfWork.SaveChangesAsync(ct).Returns<Task<int>>(_ => throw new DbUpdateException("race", raceLost));

        TestKafkaConsumerService sut = new(scopeFactory, DefaultOptions(), Substitute.For<IMessageProducer>(), TimeProvider.System);

        await sut.ProcessAsync(consumer, result, ct);

        consumer.DidNotReceive().Commit(Arg.Any<ConsumeResult<string, byte[]>>());
        await attempts.Received(1).RecordFailureAsync(
            result.Topic, 3, 901, "test-alert-worker", envelope.EventId, envelope.EventType, Arg.Any<string>(), ct);
        consumer.Received(1).Seek(result.TopicPartitionOffset);
    }

    [Fact]
    public async Task ProcessAsync_WhenHandlerFailsBelowTheAttemptLimit_RewindsSoTheMessageReturns()
    {
        // Leaving the offset uncommitted does not redeliver on its own: the
        // fetch position has moved on, and a later success on the partition
        // commits past the failed message.
        CancellationToken ct = TestContext.Current.CancellationToken;
        EventEnvelope<CanonicalAlert> envelope = CreateSampleEnvelope();
        ConsumeResult<string, byte[]> result = CreateConsumeResult(envelope, partition: 0, offset: 42);

        IConsumer<string, byte[]> consumer = Substitute.For<IConsumer<string, byte[]>>();
        IDeliveryAttemptStore attempts = Substitute.For<IDeliveryAttemptStore>();
        IUnitOfWork unitOfWork = Substitute.For<IUnitOfWork>();
        IServiceScopeFactory scopeFactory = FailingScopes(envelope, attempts, unitOfWork, attemptsSoFar: 2, ct);

        TestKafkaConsumerService sut = new(
            scopeFactory,
            DefaultOptions(),
            Substitute.For<IMessageProducer>(),
            TimeProvider.System,
            onHandle: static (_, _, _) => throw new InvalidOperationException("database unavailable"));

        await sut.ProcessAsync(consumer, result, ct);

        consumer.DidNotReceive().Commit(Arg.Any<ConsumeResult<string, byte[]>>());
        consumer.Received(1).Seek(result.TopicPartitionOffset);
    }

    private static IOptions<KafkaOptions> DefaultOptions()
        => Options.Create(new KafkaOptions
        {
            BootstrapServers = "localhost:9092",
            ConsumerGroup = "test-group",
        });

    private static IServiceScopeFactory FailingScopes(
        EventEnvelope<CanonicalAlert> envelope,
        IDeliveryAttemptStore attempts,
        IUnitOfWork unitOfWork,
        int attemptsSoFar,
        CancellationToken ct)
    {
        IInboxStore inbox = Substitute.For<IInboxStore>();
        inbox.TryBeginAsync(envelope.EventId, envelope.EventType, "test-alert-worker", ct)
            .Returns(Task.FromResult(true));
        attempts.RecordFailureAsync(
                Arg.Any<string>(), Arg.Any<int>(), Arg.Any<long>(), Arg.Any<string>(),
                Arg.Any<Guid?>(), Arg.Any<string?>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(attemptsSoFar));

        IServiceScopeFactory scopeFactory = Substitute.For<IServiceScopeFactory>();
        IServiceScope mainScope = Substitute.For<IServiceScope>();
        IServiceProvider mainServices = Substitute.For<IServiceProvider>();
        IServiceScope failureScope = Substitute.For<IServiceScope>();
        IServiceProvider failureServices = Substitute.For<IServiceProvider>();

        scopeFactory.CreateScope().Returns(mainScope, failureScope);

        mainScope.ServiceProvider.Returns(mainServices);
        mainServices.GetService(typeof(IInboxStore)).Returns(inbox);
        mainServices.GetService(typeof(IUnitOfWork)).Returns(unitOfWork);

        failureScope.ServiceProvider.Returns(failureServices);
        failureServices.GetService(typeof(IDeliveryAttemptStore)).Returns(attempts);

        return scopeFactory;
    }
}
