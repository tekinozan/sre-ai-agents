using SreAiAgent.Domain.ModelProviders;
using SreAiAgent.Domain.Shared;

namespace SreAiAgent.Domain.Tests.ModelProviders;

/// <summary>
/// Covers the closed scope-kind set of D40. The type has no public setter and
/// no constructor other than the three factories and <see cref="TenantScope.Create"/>,
/// so these tests exercise the only ways a <see cref="TenantScope"/> can come
/// to exist.
/// </summary>
public sealed class TenantScopeTests
{
    [Fact]
    public void TenantWideHasNoProductOrEnvironment()
    {
        TenantScope scope = TenantScope.TenantWide("acme");

        scope.Tenant.ShouldBe("acme");
        scope.Product.ShouldBe(string.Empty);
        scope.Environment.ShouldBe(string.Empty);
        scope.Kind.ShouldBe(TenantScopeKind.TenantWide);
    }

    [Fact]
    public void ProductWideHasNoEnvironment()
    {
        TenantScope scope = TenantScope.ProductWide("acme", "checkout");

        scope.Tenant.ShouldBe("acme");
        scope.Product.ShouldBe("checkout");
        scope.Environment.ShouldBe(string.Empty);
        scope.Kind.ShouldBe(TenantScopeKind.ProductWide);
    }

    [Fact]
    public void FullCarriesProductAndEnvironment()
    {
        TenantScope scope = TenantScope.Full("acme", "checkout", "production");

        scope.Tenant.ShouldBe("acme");
        scope.Product.ShouldBe("checkout");
        scope.Environment.ShouldBe("production");
        scope.Kind.ShouldBe(TenantScopeKind.Full);
    }

    [Fact]
    public void EnvironmentWithoutProductIsRejectedAtConstruction()
    {
        // D40's closed set has no member for this combination. The rejection
        // has to happen here, in Create, and never as a database constraint
        // violation or a silently-dropped environment.
        DomainException exception = Should.Throw<DomainException>(
            () => TenantScope.Create("acme", product: null, environment: "production"));

        exception.Code.ShouldBe(DomainErrorCodes.TenantScopeInvalid);
    }

    [Fact]
    public void BlankProductCountsAsAbsentSoEnvironmentAloneIsStillRejected()
    {
        DomainException exception = Should.Throw<DomainException>(
            () => TenantScope.Create("acme", product: "   ", environment: "production"));

        exception.Code.ShouldBe(DomainErrorCodes.TenantScopeInvalid);
    }

    [Fact]
    public void MissingTenantIsRejected()
        => Should.Throw<DomainException>(() => TenantScope.TenantWide(" "));

    [Fact]
    public void CreateWithNeitherProductNorEnvironmentIsTenantWide()
    {
        TenantScope scope = TenantScope.Create("acme", product: null, environment: null);

        scope.ShouldBe(TenantScope.TenantWide("acme"));
    }

    [Fact]
    public void CreateWithBothIsFull()
    {
        TenantScope scope = TenantScope.Create("acme", "checkout", "production");

        scope.ShouldBe(TenantScope.Full("acme", "checkout", "production"));
    }

    [Fact]
    public void EqualityIsByValueAcrossEveryComponent()
    {
        // A composite key needs this: two scopes are the same key only when
        // tenant, product and environment all match, not merely the same kind.
        TenantScope.Full("acme", "checkout", "production")
            .ShouldBe(TenantScope.Full("acme", "checkout", "production"));

        TenantScope.Full("acme", "checkout", "production")
            .ShouldNotBe(TenantScope.Full("acme", "checkout", "staging"));

        TenantScope.Full("acme", "checkout", "production")
            .ShouldNotBe(TenantScope.Full("acme", "billing", "production"));

        TenantScope.Full("acme", "checkout", "production")
            .ShouldNotBe(TenantScope.Full("globex", "checkout", "production"));
    }

    [Fact]
    public void TenantWideAndProductWideAreNeverEqualEvenWithTheSameTenant()
        // Different kinds must never collide just because the trailing
        // columns both happen to render as empty strings for one of them.
        => TenantScope.TenantWide("acme").ShouldNotBe(TenantScope.ProductWide("acme", "checkout"));

    // No test attempts `new TenantScope()`, `new TenantScope { Tenant = "x" }`
    // or `scope with { Environment = "x" }` from outside TenantScope: all
    // three are verified compile errors (CS1729 / CS0200), not runtime
    // rejections. TenantScope declares an explicit private parameterless
    // constructor, which removes the public one the compiler would otherwise
    // synthesize, and every property setter is `private init`; together they
    // mean direct construction and property mutation are unreachable from any
    // other assembly, which is a stronger guarantee than a unit test could
    // add and one this suite would rather record here than re-encode as a
    // test that could only ever fail to compile.
}
