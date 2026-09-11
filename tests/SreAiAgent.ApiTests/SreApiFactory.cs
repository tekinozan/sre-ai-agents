using System.Security.Claims;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.IdentityModel.Protocols.OpenIdConnect;
using Microsoft.IdentityModel.Tokens;
using NSubstitute;
using SreAiAgent.Application.Abstractions.Llm;
using SreAiAgent.Application.Abstractions.Messaging;
using SreAiAgent.Application.Abstractions.Persistence;
using SreAiAgent.Infrastructure.Messaging;

namespace SreAiAgent.ApiTests;

/// <summary>
/// In-process HTTP test fixture for <c>SreAiAgent.Api</c>.
/// </summary>
/// <remarks>
/// Boots the full ASP.NET Core pipeline without running Docker containers. Replaces
/// external dependencies (database, Kafka producer, Temporal workflow engine, model
/// control plane) with deterministic in-memory substitutes.
/// </remarks>
public sealed class SreApiFactory : WebApplicationFactory<Program>
{
    internal RecordingEventPublisher Publisher { get; } = new();

    internal CountingUnitOfWork UnitOfWork { get; } = new();

    public IModelProviderControlPlane ControlPlane { get; } = Substitute.For<IModelProviderControlPlane>();

    internal IMessageProducer MessageProducer { get; } = Substitute.For<IMessageProducer>();

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        builder.UseEnvironment("Testing");

        builder.UseSetting("Database:ConnectionString", "Host=127.0.0.1;Port=5432;Database=sre_apitests;Username=apitests;Password=apitests");
        builder.UseSetting("Database:MigrateOnStartup", "false");
        builder.UseSetting("Registries:ConfigurationRoot", RepositoryPaths.Config);
        builder.UseSetting("Registries:PromptRoot", RepositoryPaths.Prompts);
        builder.UseSetting("Workflow:Engine", "inline");
        builder.UseSetting("Outbox:Enabled", "false");
        builder.UseSetting("Security:AllowDevelopmentAuthentication", "false");
        builder.UseSetting("Security:Audience", TestTokens.Audience);

        builder.ConfigureTestServices(services =>
        {
            services.PostConfigure<JwtBearerOptions>(JwtBearerDefaults.AuthenticationScheme, static options =>
            {
                options.Authority = null;
                options.MetadataAddress = null!;
                options.RequireHttpsMetadata = false;
                options.Configuration = new OpenIdConnectConfiguration();
                options.TokenValidationParameters = new TokenValidationParameters
                {
                    ValidateIssuer = true,
                    ValidIssuer = TestTokens.Issuer,
                    ValidateAudience = true,
                    ValidAudience = TestTokens.Audience,
                    ValidateLifetime = true,
                    ValidateIssuerSigningKey = true,
                    IssuerSigningKey = TestTokens.SigningKey,
                    RoleClaimType = ClaimTypes.Role,
                    NameClaimType = ClaimTypes.NameIdentifier,
                    ClockSkew = TimeSpan.FromSeconds(30),
                };
            });

            services.RemoveAll<IEventPublisher>();
            services.AddSingleton(Publisher);
            services.AddScoped<IEventPublisher>(static sp => sp.GetRequiredService<RecordingEventPublisher>());

            services.RemoveAll<IUnitOfWork>();
            services.AddSingleton(UnitOfWork);
            services.AddScoped<IUnitOfWork>(static sp => sp.GetRequiredService<CountingUnitOfWork>());

            services.RemoveAll<IModelProviderControlPlane>();
            services.AddSingleton(ControlPlane);

            services.RemoveAll<IMessageProducer>();
            services.AddSingleton(MessageProducer);
        });
    }

    public void Reset()
    {
        Publisher.Reset();
        UnitOfWork.Reset();
        ControlPlane.ClearReceivedCalls();
    }
}
