using Microsoft.EntityFrameworkCore;
using Npgsql;
using SreAiAgent.Application.Abstractions.Notifications;
using SreAiAgent.Application.Errors;
using SreAiAgent.Domain.Notifications;
using SreAiAgent.Domain.Shared;
using SreAiAgent.Infrastructure.Llm;
using SreAiAgent.Infrastructure.Persistence;
using SreAiAgent.Infrastructure.Persistence.Entities;
using SreAiAgent.Infrastructure.Persistence.Repositories;

namespace SreAiAgent.Infrastructure.Notifications;

/// <summary>A tenant's notification channels, backed by PostgreSQL.</summary>
internal sealed class NotificationChannelService(
    SreDbContext context,
    ModelProviderRepository audit,
    NotificationSender sender,
    TimeProvider timeProvider) : INotificationChannelService
{
    public async Task<IReadOnlyList<NotificationChannel>> ListAsync(string tenant, CancellationToken cancellationToken)
        => (await context.NotificationChannels
                .AsNoTracking()
                .Where(channel => channel.Tenant == tenant)
                .OrderBy(channel => channel.DisplayName)
                .ToListAsync(cancellationToken)
                .ConfigureAwait(false))
            .Select(ToDomain)
            .ToArray();

    public async Task<Result<NotificationChannel>> CreateAsync(
        string tenant,
        NotificationChannelKind kind,
        string displayName,
        string webhookRef,
        NotificationEvents events,
        string actor,
        CancellationToken cancellationToken)
    {
        // A literal URL in place of a reference is a secret about to be stored
        // in plain text and echoed by the list endpoint.
        if (!ExternalReferenceCredentialResolver.IsWellFormedReference(webhookRef ?? string.Empty))
        {
            return Result<NotificationChannel>.Failure(Error.Permanent(
                DomainErrorCodes.NotificationChannelInvalid,
                "The webhook must be given as a credential reference (env:// or file://), never as a URL."));
        }

        NotificationChannel channel;
        try
        {
            channel = NotificationChannel.Create(tenant, kind, displayName, webhookRef!, events, timeProvider.GetUtcNow(), actor);
        }
        catch (DomainException exception)
        {
            return Result<NotificationChannel>.Failure(Error.Permanent(exception.Code, exception.Message));
        }

        context.NotificationChannels.Add(new NotificationChannelEntity
        {
            Id = channel.Id,
            Tenant = channel.Tenant,
            Kind = channel.Kind.ToString(),
            DisplayName = channel.DisplayName,
            WebhookRef = channel.WebhookRef,
            Events = (int)channel.Events,
            Enabled = channel.Enabled,
            CreatedAt = channel.CreatedAt,
            CreatedBy = channel.CreatedBy,
        });
        audit.AddAudit(tenant, actor, "create", "notification_channel", channel.Id, string.Empty, channel.CreatedAt);

        try
        {
            await context.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (DbUpdateException exception) when (exception.InnerException is PostgresException
        {
            SqlState: PostgresErrorCodes.UniqueViolation,
        })
        {
            return Result<NotificationChannel>.Failure(Error.Permanent(
                ApplicationErrorCodes.NotificationChannelConflict, "The tenant already has a channel with that name."));
        }

        return Result<NotificationChannel>.Success(channel);
    }

    public async Task<Result> DeleteAsync(string tenant, Guid channelId, string actor, CancellationToken cancellationToken)
    {
        int removed = await context.NotificationChannels
            .Where(channel => channel.Tenant == tenant && channel.Id == channelId)
            .ExecuteDeleteAsync(cancellationToken)
            .ConfigureAwait(false);
        if (removed == 0)
        {
            return NotFound();
        }

        audit.AddAudit(tenant, actor, "delete", "notification_channel", channelId, string.Empty, timeProvider.GetUtcNow());
        await context.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        return Result.Success();
    }

    public async Task<Result> SendTestAsync(string tenant, Guid channelId, CancellationToken cancellationToken)
    {
        NotificationChannelEntity? entity = await context.NotificationChannels
            .AsNoTracking()
            .SingleOrDefaultAsync(channel => channel.Tenant == tenant && channel.Id == channelId, cancellationToken)
            .ConfigureAwait(false);
        if (entity is null)
        {
            return NotFound();
        }

        NotificationChannel channel = ToDomain(entity);
        return await sender.SendAsync(
                channel,
                new NotificationMessage(
                    NotificationEvents.None,
                    $"test:{channel.Id}:{timeProvider.GetUtcNow():O}",
                    NotificationTone.Good,
                    "SRE AI Agent test notification",
                    [new("Channel", channel.DisplayName), new("Tenant", tenant)],
                    "If you can read this, investigation reports will arrive here."),
                cancellationToken)
            .ConfigureAwait(false);
    }

    internal static NotificationChannel ToDomain(NotificationChannelEntity entity)
        => NotificationChannel.Restore(
            entity.Id,
            entity.Tenant,
            Enum.Parse<NotificationChannelKind>(entity.Kind),
            entity.DisplayName,
            entity.WebhookRef,
            (NotificationEvents)entity.Events,
            entity.Enabled,
            entity.CreatedAt,
            entity.CreatedBy);

    private static Result NotFound()
        => Result.Failure(Error.Permanent(ApplicationErrorCodes.NotificationChannelNotFound, "No such notification channel."));
}
