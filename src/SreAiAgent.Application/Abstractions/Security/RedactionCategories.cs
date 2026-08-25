namespace SreAiAgent.Application.Abstractions.Security;

/// <summary>
/// The minimum categories ARCHITECTURE.md 15.3 requires the redaction pipeline
/// to handle. Named constants rather than free strings so the security tests can
/// assert coverage of the full set rather than of whatever a detector happened to
/// be called.
/// </summary>
public static class RedactionCategories
{
    public const string Password = "PASSWORD";
    public const string ApiKey = "API_KEY";
    public const string JsonWebToken = "JWT";
    public const string BearerToken = "BEARER_TOKEN";
    public const string Cookie = "COOKIE";
    public const string ConnectionString = "CONNECTION_STRING";
    public const string PaymentCard = "PAYMENT_CARD";
    public const string PersonalIdentifier = "PERSONAL_IDENTIFIER";
    public const string EmailAddress = "EMAIL_ADDRESS";
    public const string PrivateKey = "PRIVATE_KEY";

    /// <summary>Every required category, for coverage assertions at startup and in tests.</summary>
    public static IReadOnlyList<string> Required { get; } =
    [
        Password,
        ApiKey,
        JsonWebToken,
        BearerToken,
        Cookie,
        ConnectionString,
        PaymentCard,
        PersonalIdentifier,
        EmailAddress,
        PrivateKey,
    ];
}
