using System.ComponentModel.DataAnnotations;
using Confluent.Kafka;
using SreAiAgent.Infrastructure.Configuration;
using SreAiAgent.Infrastructure.Messaging;

namespace SreAiAgent.Infrastructure.Tests;

/// <summary>
/// Verifies Kafka security options validation and client configuration mapping.
/// </summary>
public sealed class KafkaSecurityConfigurationTests
{
    [Fact]
    public void ApplySecurityCopiesAllFiveSettingsToProducerAndConsumerConfig()
    {
        KafkaOptions options = new()
        {
            BootstrapServers = "broker:9092",
            SecurityProtocol = SecurityProtocol.SaslSsl,
            SaslMechanism = SaslMechanism.ScramSha512,
            SaslUsername = "kafka-user",
            SaslPassword = "kafka-password",
            SslCaLocation = "/etc/ssl/certs/ca.pem",
        };

        ProducerConfig producerConfig = new();
        KafkaClientConfiguration.ApplySecurity(producerConfig, options);

        producerConfig.SecurityProtocol.ShouldBe(SecurityProtocol.SaslSsl);
        producerConfig.SaslMechanism.ShouldBe(SaslMechanism.ScramSha512);
        producerConfig.SaslUsername.ShouldBe("kafka-user");
        producerConfig.SaslPassword.ShouldBe("kafka-password");
        producerConfig.SslCaLocation.ShouldBe("/etc/ssl/certs/ca.pem");

        ConsumerConfig consumerConfig = new();
        KafkaClientConfiguration.ApplySecurity(consumerConfig, options);

        consumerConfig.SecurityProtocol.ShouldBe(SecurityProtocol.SaslSsl);
        consumerConfig.SaslMechanism.ShouldBe(SaslMechanism.ScramSha512);
        consumerConfig.SaslUsername.ShouldBe("kafka-user");
        consumerConfig.SaslPassword.ShouldBe("kafka-password");
        consumerConfig.SslCaLocation.ShouldBe("/etc/ssl/certs/ca.pem");
    }

    [Fact]
    public void SaslSslWithoutCredentialsFailsValidationWithThreeErrors()
    {
        KafkaOptions options = new()
        {
            BootstrapServers = "broker:9092",
            SecurityProtocol = SecurityProtocol.SaslSsl,
        };

        ValidationContext context = new(options);
        List<ValidationResult> results = options.Validate(context).ToList();

        results.Count.ShouldBe(3);
        results.ShouldContain(static r => r.MemberNames.Contains(nameof(KafkaOptions.SaslMechanism)));
        results.ShouldContain(static r => r.MemberNames.Contains(nameof(KafkaOptions.SaslUsername)));
        results.ShouldContain(static r => r.MemberNames.Contains(nameof(KafkaOptions.SaslPassword)));
    }

    [Fact]
    public void PlaintextProtocolRequiresNoSaslCredentials()
    {
        KafkaOptions options = new()
        {
            BootstrapServers = "broker:9092",
            SecurityProtocol = SecurityProtocol.Plaintext,
        };

        ValidationContext context = new(options);
        List<ValidationResult> results = options.Validate(context).ToList();

        results.ShouldBeEmpty();
    }
}
