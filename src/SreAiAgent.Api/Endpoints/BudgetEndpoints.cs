using System.Security.Claims;
using SreAiAgent.Api.Http;
using SreAiAgent.Api.Security;
using SreAiAgent.Application.Abstractions.Llm;
using SreAiAgent.Application.Errors;
using SreAiAgent.Contracts.Budgets;
using SreAiAgent.Domain.Budgets;
using SreAiAgent.Domain.Shared;

namespace SreAiAgent.Api.Endpoints;

/// <summary>
/// The tenant's model-cost budget (gemini-budgets-notifications.md B, D47).
/// </summary>
/// <remarks>
/// The tenant comes from the caller's validated claims, never from the route or
/// body, the same rule as every other tenant-owned resource. Reading is open to
/// anyone who can read incidents, because "why did the investigation stop" is
/// an incident question; changing it needs the model-provider role.
/// </remarks>
public static class BudgetEndpoints
{
    /// <summary>Maps the budget endpoints.</summary>
    public static IEndpointRouteBuilder MapBudgetEndpoints(this IEndpointRouteBuilder builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        RouteGroupBuilder budget = builder.MapGroup("/v1/budget").WithTags("Budget");

        budget.MapGet("/", GetAsync)
            .RequireAuthorization(SrePolicies.ReadIncidents)
            .WithSummary("Returns the tenant's model-cost budget and current spend.");

        budget.MapPut("/", SetAsync)
            .RequireAuthorization(SrePolicies.ManageModelProviders)
            .WithSummary("Creates or replaces the tenant's model-cost budget.");

        budget.MapDelete("/", RemoveAsync)
            .RequireAuthorization(SrePolicies.ManageModelProviders)
            .WithSummary("Removes the tenant's budget, lifting the ceiling.");

        return builder;
    }

    private static async Task<IResult> GetAsync(
        ClaimsPrincipal principal,
        ITenantBudgetService budgets,
        CancellationToken cancellationToken)
    {
        TenantBudgetStatus? status = await budgets.GetAsync(principal.Tenant(), cancellationToken).ConfigureAwait(false);
        return status is null
            ? ResultExtensions.Problem(Error.Permanent(ApplicationErrorCodes.BudgetNotFound, "The tenant has no budget."))
            : Results.Json(ToResponse(status));
    }

    private static async Task<IResult> SetAsync(
        SetBudgetRequest request,
        ClaimsPrincipal principal,
        ITenantBudgetService budgets,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (!TryParsePeriod(request.Period, out BudgetPeriod period))
        {
            return ResultExtensions.Problem(Error.Permanent(
                DomainErrorCodes.BudgetInvalid, "The budget period must be 'monthly' or 'fixed'."));
        }

        Result<TenantBudgetStatus> result = await budgets
            .SetAsync(
                principal.Tenant(),
                request.Limit,
                request.Currency,
                period,
                request.StartsAt,
                principal.Actor(),
                cancellationToken)
            .ConfigureAwait(false);
        return result.IsSuccess ? Results.Json(ToResponse(result.Value)) : ResultExtensions.Problem(result.Error);
    }

    private static async Task<IResult> RemoveAsync(
        ClaimsPrincipal principal,
        ITenantBudgetService budgets,
        CancellationToken cancellationToken)
        => (await budgets.RemoveAsync(principal.Tenant(), principal.Actor(), cancellationToken).ConfigureAwait(false))
            .ToHttpResult(StatusCodes.Status204NoContent);

    private static bool TryParsePeriod(string? value, out BudgetPeriod period)
    {
        period = default;
        return !string.IsNullOrWhiteSpace(value)
            && Enum.TryParse(value, ignoreCase: true, out period)
            && Enum.IsDefined(period);
    }

    private static BudgetResponse ToResponse(TenantBudgetStatus status) => new()
    {
        Limit = status.Budget.Limit,
        Currency = status.Budget.Currency,
        Period = status.Budget.Period == BudgetPeriod.Monthly ? "monthly" : "fixed",
        StartsAt = status.Budget.StartsAt,
        PeriodStartedAt = status.PeriodStartedAt,
        Spent = status.Spent,
        Remaining = status.Remaining,
        Exhausted = status.IsExhausted,
        UpdatedAt = status.Budget.UpdatedAt,
        UpdatedBy = status.Budget.UpdatedBy,
    };
}
