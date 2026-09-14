using SreAiAgent.Domain.Shared;

namespace SreAiAgent.Domain.ModelProviders;

/// <summary>The closed set of scope kinds a <see cref="TenantScope"/> can represent (D40).</summary>
public enum TenantScopeKind
{
    /// <summary>Applies to the whole tenant; nothing narrower has been asked of it.</summary>
    TenantWide,

    /// <summary>Applies to every environment of one product.</summary>
    ProductWide,

    /// <summary>Applies to exactly one product in exactly one environment.</summary>
    Full,
}

/// <summary>
/// Extends the tenant boundary with product and environment (D40).
/// </summary>
/// <remarks>
/// Not a new entity: <see cref="Product"/> and <see cref="Environment"/> are
/// plain strings that widen the existing <c>(Tenant, ...)</c> composite keys in
/// <c>ModelProviderConfigurations.cs</c>, the same way <see cref="Tenant"/>
/// itself is a bare string with no dedicated table anywhere in this schema.
///
/// A closed set of exactly three kinds, not three free strings. Every
/// property here has a private setter, so the only way to build one is
/// through <see cref="Create"/> (or the three factories below, which call
/// it) — an environment without a product, the one combination D40 has no
/// member for, is rejected at that single point rather than validated later
/// by a repository or a migration.
///
/// An empty <see cref="Product"/> or <see cref="Environment"/> is the
/// sentinel for "not narrowed," and it means the same thing whether the row
/// predates this decision or an operator later left it tenant-wide on
/// purpose — the two are the same <see cref="TenantScopeKind.TenantWide"/>
/// kind by design (D40), not two meanings sharing one representation.
/// </remarks>
/// <remarks>
/// A <c>sealed record</c> (a reference type), not a <c>record struct</c>: a
/// struct always has a constructible <c>default</c> value no private
/// constructor can prevent, but a class does not — the only zero-argument
/// escape hatch on a class is the public parameterless constructor the
/// compiler would otherwise synthesize because no constructor is declared
/// here, and the explicit private one below removes exactly that.
/// </remarks>
public sealed record TenantScope
{
    // Declaring this removes the public parameterless constructor the
    // compiler would otherwise synthesize. With this here, `new TenantScope()`
    // is CS1729 (no accessible 0-arg constructor), `new TenantScope { Tenant
    // = "..." }` is the same CS1729 plus CS0200 (the init accessors are also
    // private), and `scope with { Environment = "..." }` is CS0200 too —
    // verified in TenantScopeTests' trailing comment, not tested at runtime
    // because a compile error cannot be caught by a test that has to compile
    // first. The three factories and Create are the only way in.
    private TenantScope()
    {
    }

    public string Tenant { get; private init; } = string.Empty;

    public string Product { get; private init; } = string.Empty;

    public string Environment { get; private init; } = string.Empty;

    public TenantScopeKind Kind { get; private init; }

    /// <summary>Applies to the whole tenant; nothing narrower has been asked of it.</summary>
    public static TenantScope TenantWide(string tenant) => Create(tenant, product: null, environment: null);

    /// <summary>Applies to every environment of one product.</summary>
    public static TenantScope ProductWide(string tenant, string product) => Create(tenant, product, environment: null);

    /// <summary>Applies to exactly one product in exactly one environment.</summary>
    public static TenantScope Full(string tenant, string product, string environment) => Create(tenant, product, environment);

    /// <summary>
    /// Derives a scope from optional, possibly-caller-supplied values — the
    /// shape a future claim reader (F1-3) will call with raw claim strings.
    /// </summary>
    /// <exception cref="DomainException">
    /// <paramref name="environment"/> is present without <paramref name="product"/>.
    /// D40 has no scope kind for that combination, so it is refused here,
    /// before it can reach a repository or a column.
    /// </exception>
    public static TenantScope Create(string tenant, string? product, string? environment)
    {
        Guard.AgainstNullOrWhiteSpace(tenant, DomainErrorCodes.TenantScopeInvalid);

        bool hasProduct = !string.IsNullOrWhiteSpace(product);
        bool hasEnvironment = !string.IsNullOrWhiteSpace(environment);

        if (hasEnvironment && !hasProduct)
        {
            throw new DomainException(
                DomainErrorCodes.TenantScopeInvalid,
                "An environment without a product is not a representable TenantScope (D40).");
        }

        return new TenantScope
        {
            Tenant = tenant,
            Product = hasProduct ? product! : string.Empty,
            Environment = hasEnvironment ? environment! : string.Empty,
            Kind = hasProduct
                ? (hasEnvironment ? TenantScopeKind.Full : TenantScopeKind.ProductWide)
                : TenantScopeKind.TenantWide,
        };
    }
}
