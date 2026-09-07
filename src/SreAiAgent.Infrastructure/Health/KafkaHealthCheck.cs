using Confluent.Kafka;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Options;
using SreAiAgent.Infrastructure.Configuration;
using SreAiAgent.Infrastructure.Messaging;

namespace SreAiAgent.Infrastructure.Health;

internal sealed class KafkaHealthCheck : IHealthCheck, IDisposable
{
    private static readonly TimeSpan MetadataTimeout = TimeSpan.FromSeconds(5);
    private readonly IAdminClient _admin;

    public KafkaHealthCheck(IOptions<KafkaOptions> options)
    {
        ArgumentNullException.ThrowIfNull(options);

        AdminClientConfig config = new() { BootstrapServers = options.Value.BootstrapServers };
        KafkaClientConfiguration.ApplySecurity(config, options.Value);
        _admin = new AdminClientBuilder(config).Build();
    }

    public Task<HealthCheckResult> CheckHealthAsync(
        HealthCheckContext context,
        CancellationToken cancellationToken = default)
    {
        try
        {
            Metadata metadata = _admin.GetMetadata(MetadataTimeout);

            return Task.FromResult(metadata.Brokers.Count > 0
                ? HealthCheckResult.Healthy()
                : HealthCheckResult.Unhealthy("The Kafka cluster reported no brokers."));
        }
        catch (KafkaException exception)
        {
            return Task.FromResult(HealthCheckResult.Unhealthy(
                "The Kafka cluster did not answer a metadata request.", exception));
        }
    }

    public void Dispose() => _admin.Dispose();
}
