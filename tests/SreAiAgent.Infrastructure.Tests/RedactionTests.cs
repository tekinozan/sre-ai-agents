using SreAiAgent.Application.Abstractions.Security;
using SreAiAgent.Infrastructure.Security;

namespace SreAiAgent.Infrastructure.Tests;

/// <summary>
/// Covers the redaction pipeline of ARCHITECTURE.md 15.3.
/// </summary>
/// <remarks>
/// Every case here is content that a real tool could plausibly return. A log line
/// containing a bearer token, a connection string in an exception message and a
/// card number in a payload are not hypotheticals; they are the ordinary output
/// of the systems this platform reads.
/// </remarks>
public sealed class RedactionTests
{
    private readonly RegexRedactor _redactor = new();

    [Theory]
    [InlineData("Authorization: Bearer eyJhbGciOiJIUzI1NiJ9.eyJzdWIiOiIxIn0.abcdefghijklmnop")]
    [InlineData("token=eyJhbGciOiJIUzI1NiJ9.eyJzdWIiOiIxMjM0NSJ9.SflKxwRJSMeKKF2QT4fwpMeJf36")]
    public void JsonWebTokensAreRemoved(string content)
    {
        RedactionResult result = _redactor.Redact(content);

        result.Content.ShouldNotContain("eyJhbGciOiJIUzI1NiJ9");
        result.ReplacedValueCount.ShouldBeGreaterThan(0);
    }

    [Fact]
    public void ConnectionStringsAreRemovedWhole()
    {
        const string Content =
            "Npgsql.NpgsqlException: failed to connect using " +
            "Host=db-primary.internal;Database=checkout;Username=app;Password=hunter2SuperSecret;";

        RedactionResult result = _redactor.Redact(Content);

        // The password must go, and so must the rest of the string. Removing the
        // password alone would leave the host, database and username behind,
        // which is most of what an attacker reading an evidence row wants.
        result.Content.ShouldNotContain("hunter2SuperSecret");
        result.Content.ShouldNotContain("db-primary.internal");
        result.Categories.ShouldContain(RedactionCategories.ConnectionString);
    }

    [Theory]
    [InlineData("api_key=sk-live-0123456789abcdef", RedactionCategories.ApiKey)]
    [InlineData("client_secret: 9f8e7d6c5b4a39281706", RedactionCategories.ApiKey)]
    [InlineData("password=correct-horse-battery", RedactionCategories.Password)]
    [InlineData("Cookie: session=abcd1234efgh5678", RedactionCategories.Cookie)]
    [InlineData("user reported 4111 1111 1111 1111 declined", RedactionCategories.PaymentCard)]
    [InlineData("contact ops-oncall@example.com for details", RedactionCategories.EmailAddress)]
    public void EachRequiredCategoryIsDetected(string content, string expectedCategory)
    {
        RedactionResult result = _redactor.Redact(content);

        result.Categories.ShouldContain(expectedCategory);
        result.IsClean.ShouldBeFalse();
    }

    [Fact]
    public void PrivateKeyBlocksAreRemovedEntirely()
    {
        const string Content = """
            -----BEGIN RSA PRIVATE KEY-----
            MIIEowIBAAKCAQEAtHqrKlm3n4o5p6q7r8s9t0u1v2w3x4y5z6A7B8C9D0E1F2G3
            H4I5J6K7L8M9N0O1P2Q3R4S5T6U7V8W9X0Y1Z2a3b4c5d6e7f8g9h0i1j2k3l4
            -----END RSA PRIVATE KEY-----
            """;

        RedactionResult result = _redactor.Redact(Content);

        result.Content.ShouldNotContain("MIIEowIBAAKCAQEA");
        result.Categories.ShouldContain(RedactionCategories.PrivateKey);
    }

    [Fact]
    public void ReplacementsNameTheCategoryInsteadOfBlanking()
    {
        RedactionResult result = _redactor.Redact("Authorization: Bearer abcdefghijklmnopqrstuvwxyz");

        // An agent seeing [REDACTED:BEARER_TOKEN] in a logged URL can still
        // observe that a token was being logged, which is itself a finding. A
        // blanked value tells it nothing.
        result.Content.ShouldContain("[REDACTED:");
        result.Content.ShouldContain(RedactionCategories.BearerToken);
    }

    [Fact]
    public void OrdinaryOperationalTextIsLeftAlone()
    {
        const string Content =
            "2026-08-25T10:14:02Z WARN checkout p99 latency 1240ms exceeded SLO of 800ms over 5m window";

        RedactionResult result = _redactor.Redact(Content);

        result.Content.ShouldBe(Content);
        result.IsClean.ShouldBeTrue();
        result.Categories.ShouldBeEmpty();
    }

    [Fact]
    public void RedactionIsIdempotent()
    {
        RedactionResult first = _redactor.Redact("password=hunter2 and api_key=sk-live-0123456789abcdef");
        RedactionResult second = _redactor.Redact(first.Content);

        // A result can be redacted again on its way into a prompt. A second pass
        // must not mangle the placeholders the first pass produced.
        second.Content.ShouldBe(first.Content);
        second.ReplacedValueCount.ShouldBe(0);
    }

    [Fact]
    public void EveryRequiredCategoryFromTheArchitectureIsImplemented()
    {
        // Section 15.3 names the minimum set. A category declared but never
        // detected would be a documented control that does nothing.
        string[] detectable =
        [
            "Authorization: Bearer abcdefghijklmnopqrstuvwxyz",
            "api_key=sk-live-0123456789abcdef",
            "password=hunter2xyz",
            "Cookie: session=abcd1234efgh5678",
            "Host=db.internal;Database=x;Password=secretvalue;",
            "4111 1111 1111 1111",
            "tckn: 12345678901",
            "ops@example.com",
            "eyJhbGciOiJIUzI1NiJ9.eyJzdWIiOiIxIn0.abcdefghijklmnop",
            "-----BEGIN PRIVATE KEY-----\nAAAA\n-----END PRIVATE KEY-----",
        ];

        HashSet<string> found = [];
        foreach (string sample in detectable)
        {
            foreach (string category in _redactor.Redact(sample).Categories)
            {
                found.Add(category);
            }
        }

        RedactionCategories.Required.ShouldAllBe(category => found.Contains(category));
    }
}
