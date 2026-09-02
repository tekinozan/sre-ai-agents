using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using NSubstitute;
using SreAiAgent.Domain.ModelProviders;
using SreAiAgent.Infrastructure.Configuration;
using SreAiAgent.Infrastructure.Llm;

namespace SreAiAgent.SecurityTests;

public sealed class ProviderEndpointPolicyTests
{
    private static ProviderEndpointPolicy Create(bool production, params string[] allowedHosts)
    {
        var environment = Substitute.For<IHostEnvironment>();
        environment.EnvironmentName.Returns(production ? Environments.Production : Environments.Development);

        var options = Substitute.For<IOptions<LlmOptions>>();
        options.Value.Returns(new LlmOptions { AllowedCustomEndpointHosts = allowedHosts });

        return new ProviderEndpointPolicy(environment, options);
    }

    [Fact]
    public void ScriptedAdapterIsRefusedForEveryEndpointInProduction()
    {
        var policy = Create(production: true);

        Should.Throw<InvalidOperationException>(() => policy.Validate(ProviderAdapterKind.Scripted, "https://api.openai.com"))
            .Message.ShouldBe("The provider endpoint host is not allowed in production.");
    }

    [Fact]
    public void ScriptedAdapterIsAllowedInDevelopment()
    {
        var policy = Create(production: false);

        var uri = policy.Validate(ProviderAdapterKind.Scripted, "https://api.openai.com");
        uri.Host.ShouldBe("api.openai.com");
    }

    [Fact]
    public void IpLiteralHostIsRefusedInProduction()
    {
        var policy = Create(production: true, "192.168.1.100");

        Should.Throw<InvalidOperationException>(() => policy.Validate(ProviderAdapterKind.OpenAiCompatible, "https://192.168.1.100"))
            .Message.ShouldBe("The provider endpoint host is not allowed in production.");
    }

    [Fact]
    public void HostOutsideAllowedCustomHostsIsRefusedForOpenAiCompatible()
    {
        var policy = Create(production: true, "trusted.example.com");

        Should.Throw<InvalidOperationException>(() => policy.Validate(ProviderAdapterKind.OpenAiCompatible, "https://untrusted.example.com"))
            .Message.ShouldBe("The provider endpoint host is not allowed in production.");

        var uri = policy.Validate(ProviderAdapterKind.OpenAiCompatible, "https://trusted.example.com");
        uri.Host.ShouldBe("trusted.example.com");
    }

    [Fact]
    public void PlainHttpIsRefusedUnlessLoopbackInDevelopment()
    {
        var production = Create(production: true);
        var development = Create(production: false);

        Should.Throw<InvalidOperationException>(() => production.Validate(ProviderAdapterKind.OpenAi, "http://api.openai.com"))
            .Message.ShouldBe("Provider endpoints must use HTTPS; development HTTP is limited to loopback.");

        Should.Throw<InvalidOperationException>(() => development.Validate(ProviderAdapterKind.OpenAi, "http://api.openai.com"))
            .Message.ShouldBe("Provider endpoints must use HTTPS; development HTTP is limited to loopback.");

        var loopback = development.Validate(ProviderAdapterKind.OpenAi, "http://localhost:8080");
        loopback.Host.ShouldBe("localhost");
    }
}
