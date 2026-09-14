using System.Security.Claims;
using SreAiAgent.Api.Security;
using SreAiAgent.Domain.ModelProviders;
using SreAiAgent.Domain.Shared;

namespace SreAiAgent.SecurityTests;

/// <summary>
/// Proves D40's authorization matrix (F1-3): a caller's <see cref="TenantScope"/>
/// is derived only from validated claims, and matching a row is exact equality
/// of the whole scope — kind included, never a broader or narrower claim set
/// reaching a row of a different kind.
/// </summary>
/// <remarks>
/// This is a different question from the one
/// <see cref="ModelProviderAuthorizationTests"/> answers. That suite gates
/// which role may call the management surface at all; this is which row a
/// caller's scope may reach once inside it. F1-5 (ADP-103) is what wires
/// this into <c>ModelProviderRepository</c> via
/// <c>IModelProviderControlPlane</c>'s cutover to a real
/// <see cref="TenantScope"/> — proven end to end in
/// <c>ModelControlPlaneTests.AProductScopedCallerCannotCreateReadUpdateOrActivateAnotherProductsData</c>.
/// This suite stays focused on the claim-to-scope derivation and the
/// matrix's equality rule in isolation, against real
/// <see cref="ClaimsPrincipal"/> instances rather than hand-built
/// <see cref="TenantScope"/> values, because the claim names and their
/// absence handling are the part that is actually new here. The equality
/// rule itself — a record comparing every component including
/// <c>Kind</c> — is already covered generically by <c>TenantScopeTests</c>.
/// </remarks>
public sealed class TenantScopeAuthorizationTests
{
    private static ClaimsPrincipal Principal(string tenant, string? product = null, string? environment = null)
    {
        List<Claim> claims = [new(ClaimTypes.NameIdentifier, "test-user"), new(SreClaims.Tenant, tenant)];

        if (product is not null)
        {
            claims.Add(new Claim(SreClaims.Product, product));
        }

        if (environment is not null)
        {
            claims.Add(new Claim(SreClaims.Environment, environment));
        }

        return new ClaimsPrincipal(new ClaimsIdentity(claims, authenticationType: "Test"));
    }

    [Fact]
    public void CrossTenantIsRefusedAtAnyScopeKind()
    {
        // Tenant A's claims never derive tenant B's scope, whichever kind the
        // row being checked against is.
        TenantScope callerScope = Principal("tenant-a", "checkout", "production").TenantScope();

        callerScope.ShouldNotBe(TenantScope.TenantWide("tenant-b"));
        callerScope.ShouldNotBe(TenantScope.ProductWide("tenant-b", "checkout"));
        callerScope.ShouldNotBe(TenantScope.Full("tenant-b", "checkout", "production"));
    }

    [Fact]
    public void SameTenantDifferentProductIsRefused()
    {
        // A ProductWide or Full caller for product P never matches a row
        // scoped to product Q in the same tenant.
        TenantScope productWideCaller = Principal("acme", "checkout").TenantScope();
        TenantScope fullCaller = Principal("acme", "checkout", "production").TenantScope();

        productWideCaller.ShouldNotBe(TenantScope.ProductWide("acme", "payments"));
        productWideCaller.ShouldNotBe(TenantScope.Full("acme", "payments", "production"));
        fullCaller.ShouldNotBe(TenantScope.Full("acme", "payments", "production"));
    }

    [Fact]
    public void AClaimLessCallerDoesNotReachAnotherProductsRows()
    {
        // A caller holding only a tenant claim derives TenantWide, which is
        // refused on every ProductWide and Full row in that tenant — not
        // merely on other tenants' rows, and not because TenantWide is
        // somehow "broader" than the others: it is a different kind.
        TenantScope callerScope = Principal("acme").TenantScope();

        callerScope.ShouldBe(TenantScope.TenantWide("acme"));
        callerScope.ShouldNotBe(TenantScope.ProductWide("acme", "checkout"));
        callerScope.ShouldNotBe(TenantScope.Full("acme", "checkout", "production"));
    }

    [Fact]
    public void AnEnvironmentClaimWithoutAProductClaimIsRejectedAtConstruction()
    {
        // D40 has no scope kind for this combination. The rejection has to
        // happen the moment the principal's claims are read, never as a check
        // some later repository call would otherwise have had to perform.
        ClaimsPrincipal principal = Principal("acme", product: null, environment: "production");

        DomainException exception = Should.Throw<DomainException>(() => principal.TenantScope());
        exception.Code.ShouldBe(DomainErrorCodes.TenantScopeInvalid);
    }

    [Fact]
    public void ClaimsMatchingARowsScopeExactlyAreAdmitted()
    {
        // The positive case behind the three refusals above: claims equal to
        // a row's tenant/product/environment derive the identical scope, kind
        // included, so the row is reachable.
        Principal("acme").TenantScope().ShouldBe(TenantScope.TenantWide("acme"));
        Principal("acme", "checkout").TenantScope().ShouldBe(TenantScope.ProductWide("acme", "checkout"));
        Principal("acme", "checkout", "production").TenantScope()
            .ShouldBe(TenantScope.Full("acme", "checkout", "production"));
    }
}
