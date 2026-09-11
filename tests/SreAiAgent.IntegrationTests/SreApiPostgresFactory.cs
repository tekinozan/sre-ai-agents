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
using SreAiAgent.Infrastructure.Messaging;

namespace SreAiAgent.IntegrationTests;

/// <summary>
/// In-process HTTP test fixture for <c>SreAiAgent.Api</c> backed by the real Testcontainers PostgreSQL container.
/// </summary>
public sealed class SreApiPostgresFactory(PostgresFixture postgres) : WebApplicationFactory<Program>
{
    internal IMessageProducer MessageProducer { get; } = Substitute.For<IMessageProducer>();

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        builder.UseEnvironment("Testing");

        builder.UseSetting("Database:ConnectionString", postgres.ConnectionString);
        builder.UseSetting("Database:MigrateOnStartup", "true");
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

            services.RemoveAll<IMessageProducer>();
            services.AddSingleton(MessageProducer);
        });
    }
}
