using Confluent.Kafka;
using SreAiAgent.Infrastructure.Configuration;

namespace SreAiAgent.Infrastructure.Messaging;

/// <summary>
/// Centralizes mapping of KafkaOptions onto Confluent ClientConfig instances.
/// </summary>
/// <remarks>
/// Ensures producer, consumer and admin clients cannot drift on wire protocol,
/// SASL mechanisms, credentials or TLS CA bundle paths.
/// </remarks>
internal static class KafkaClientConfiguration
{
    public static void ApplySecurity(ClientConfig config, KafkaOptions options)
    {
        ArgumentNullException.ThrowIfNull(config);
        ArgumentNullException.ThrowIfNull(options);

        config.SecurityProtocol = options.SecurityProtocol;
        config.SaslMechanism = options.SaslMechanism;
        config.SaslUsername = options.SaslUsername;
        config.SaslPassword = options.SaslPassword;
        config.SslCaLocation = options.SslCaLocation;
    }
}
