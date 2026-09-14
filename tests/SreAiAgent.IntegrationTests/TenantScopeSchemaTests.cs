using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql;
using SreAiAgent.Domain.ModelProviders;
using SreAiAgent.Infrastructure.Persistence;
using SreAiAgent.Infrastructure.Persistence.Entities;

namespace SreAiAgent.IntegrationTests;

/// <summary>
/// Covers F1-2 (D40) against a real database: the composite keys widened from
/// <c>(Tenant, ...)</c> to <c>(Tenant, Product, Environment, ...)</c> in
/// <c>ModelProviderConfigurations.cs</c>, the migration's promise about rows
/// that predate it, and <c>tenant_active_model_assignments</c>' primary key.
/// </summary>
[Collection(PostgresCollection.Name)]
[Trait("Category", "Integration")]
public sealed class TenantScopeSchemaTests(PostgresFixture postgres)
{
    /// <summary>The migration immediately before <c>TenantScopeColumns</c>.</summary>
    private const string PreMigrationTarget = "20260907085854_ConsumerDeliveryAttempts";

    /// <summary>
    /// Builds connection -&gt; revision -&gt; deployment -&gt; release under one
    /// scope — everything <c>tenant_active_model_assignments</c>' foreign key
    /// needs to exist before an activation row can reference it.
    /// </summary>
    private static async Task<ModelAssignmentReleaseId> CreateReleaseChainAsync(
        SreDbContext context,
        TenantScope scope,
        CancellationToken cancellationToken)
    {
        ProviderConnectionEntity connection = new()
        {
            Id = ProviderConnectionId.New(),
            Tenant = scope.Tenant,
            Product = scope.Product,
            Environment = scope.Environment,
            DisplayName = "chain-connection-" + Guid.NewGuid(),
            CreatedAt = DateTimeOffset.UtcNow,
        };
        context.ProviderConnections.Add(connection);
        await context.SaveChangesAsync(cancellationToken).ConfigureAwait(false);

        ProviderConnectionRevisionEntity revision = new()
        {
            Id = ProviderConnectionRevisionId.New(),
            ProviderConnectionId = connection.Id,
            Tenant = scope.Tenant,
            Product = scope.Product,
            Environment = scope.Environment,
            Revision = 1,
            AdapterKind = ProviderAdapterKind.Scripted,
            Endpoint = "https://test",
            CredentialRef = "env://TEST_KEY",
            ConfigurationHash = "revision-hash",
            CreatedAt = DateTimeOffset.UtcNow,
            CreatedBy = "test",
        };
        context.ProviderConnectionRevisions.Add(revision);
        connection.ActiveRevisionId = revision.Id;
        await context.SaveChangesAsync(cancellationToken).ConfigureAwait(false);

        ModelDeploymentVersionEntity deployment = new()
        {
            Id = ModelDeploymentVersionId.New(),
            Tenant = scope.Tenant,
            Product = scope.Product,
            Environment = scope.Environment,
            LogicalName = "chain-deployment-" + Guid.NewGuid(),
            Version = 1,
            ProviderConnectionRevisionId = revision.Id,
            ProviderModel = "scripted-model",
            ProviderParametersJson = "{}",
            Currency = "USD",
            ConfigurationHash = "deployment-hash",
            CreatedAt = DateTimeOffset.UtcNow,
            CreatedBy = "test",
        };
        context.ModelDeploymentVersions.Add(deployment);
        await context.SaveChangesAsync(cancellationToken).ConfigureAwait(false);

        ModelAssignmentReleaseEntity release = new()
        {
            Id = ModelAssignmentReleaseId.New(),
            Tenant = scope.Tenant,
            Product = scope.Product,
            Environment = scope.Environment,
            Name = "chain-release-" + Guid.NewGuid(),
            Version = 1,
            ClassifierDeploymentVersionId = deployment.Id,
            CorrelatorDeploymentVersionId = deployment.Id,
            ConfigurationHash = "release-hash",
            CreatedAt = DateTimeOffset.UtcNow,
            CreatedBy = "test",
        };
        context.ModelAssignmentReleases.Add(release);
        context.ModelAssignmentAgentSlots.Add(new ModelAssignmentAgentSlotEntity
        {
            AssignmentReleaseId = release.Id,
            Tenant = scope.Tenant,
            Product = scope.Product,
            Environment = scope.Environment,
            AgentName = "app-expert",
            DeploymentVersionId = deployment.Id,
        });
        await context.SaveChangesAsync(cancellationToken).ConfigureAwait(false);

        return release.Id;
    }

    [Fact]
    public async Task WidenedCompositeKeysRoundTripAcrossTheFullChain()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        TenantScope scope = TenantScope.Full("scope-chain-" + Guid.NewGuid(), "checkout", "production");

        await using SreDbContext writeContext = postgres.CreateContext();
        ModelAssignmentReleaseId releaseId = await CreateReleaseChainAsync(writeContext, scope, ct);

        writeContext.TenantActiveModelAssignments.Add(new TenantActiveModelAssignmentEntity
        {
            Tenant = scope.Tenant,
            Product = scope.Product,
            Environment = scope.Environment,
            AssignmentReleaseId = releaseId,
            ActivatedAt = DateTimeOffset.UtcNow,
            ActivatedBy = "test",
        });
        await writeContext.SaveChangesAsync(ct);

        // Read back through a fresh context, exercising every widened
        // alternate key and foreign key the chain above relies on: connection
        // -> revision -> deployment -> release -> agent slot -> activation.
        await using SreDbContext readContext = postgres.CreateContext();
        TenantActiveModelAssignmentEntity? active = await readContext.TenantActiveModelAssignments
            .SingleOrDefaultAsync(
                item => item.Tenant == scope.Tenant && item.Product == scope.Product && item.Environment == scope.Environment,
                ct);

        active.ShouldNotBeNull();
        active.AssignmentReleaseId.ShouldBe(releaseId);

        ModelAssignmentAgentSlotEntity? slot = await readContext.ModelAssignmentAgentSlots
            .SingleOrDefaultAsync(
                item => item.Tenant == scope.Tenant && item.Product == scope.Product
                    && item.Environment == scope.Environment && item.AssignmentReleaseId == releaseId,
                ct);
        slot.ShouldNotBeNull();
    }

    /// <summary>
    /// Proves the actual upgrade, not a column default. Migrates a fresh
    /// database to exactly the migration before <c>TenantScopeColumns</c>,
    /// seeds a fully connected graph through raw SQL against that old shape
    /// (the current <see cref="SreDbContext"/> model already has
    /// <c>Product</c>/<c>Environment</c> on every entity, so writing through
    /// it here would insert columns the old tables do not have yet), then
    /// runs the migration and re-reads everything through the *post*-upgrade
    /// model. This is the only test in the suite that exercises the
    /// migration's drop-and-recreate of primary keys, alternate keys and
    /// composite foreign keys while the tables already hold related rows —
    /// every other test here starts from an already-migrated, empty schema.
    /// </summary>
    [Fact]
    public async Task ARowWrittenBeforeTheMigrationSurvivesTheUpgradeWithItsRelationshipsIntact()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        string databaseName = "upgrade_" + Guid.NewGuid().ToString("N");
        string adminConnectionString = new NpgsqlConnectionStringBuilder(postgres.ConnectionString)
        {
            Database = "postgres",
        }.ConnectionString;
        string upgradeConnectionString = new NpgsqlConnectionStringBuilder(postgres.ConnectionString)
        {
            Database = databaseName,
        }.ConnectionString;

        try
        {
            await using (NpgsqlConnection admin = new(adminConnectionString))
            {
                await admin.OpenAsync(ct);
                await using NpgsqlCommand create = admin.CreateCommand();
                create.CommandText = $"CREATE DATABASE \"{databaseName}\"";
                await create.ExecuteNonQueryAsync(ct);
            }

            DbContextOptionsBuilder<SreDbContext> optionsBuilder = new();
            optionsBuilder.UseNpgsql(
                upgradeConnectionString, npgsql => npgsql.MigrationsHistoryTable("__migrations_history"));

            string tenant = "pre-migration-" + Guid.NewGuid();
            ProviderConnectionId connectionId = ProviderConnectionId.New();
            ProviderConnectionRevisionId revisionId = ProviderConnectionRevisionId.New();
            ModelDeploymentVersionId deploymentId = ModelDeploymentVersionId.New();
            ModelAssignmentReleaseId releaseId = ModelAssignmentReleaseId.New();
            ModelAssignmentEvaluationId evaluationId = ModelAssignmentEvaluationId.New();
            Guid verificationId = Guid.NewGuid();
            DateTimeOffset createdAt = DateTimeOffset.UtcNow;

            await using (SreDbContext oldSchema = new(optionsBuilder.Options))
            {
                IMigrator migrator = oldSchema.GetService<IMigrator>();
                await migrator.MigrateAsync(PreMigrationTarget, ct);

                await using NpgsqlConnection connection = new(upgradeConnectionString);
                await connection.OpenAsync(ct);

                // A connected graph, not orphans: connection -> revision ->
                // deployment -> release -> agent slot, plus a verification and
                // an evaluation hanging off the revision and release, plus the
                // tenant's active assignment. Every one of these relationships
                // is a composite foreign key the migration drops and rebuilds.
                await ExecuteAsync(
                    connection,
                    """
                    INSERT INTO provider_connections (id, tenant, display_name, created_at)
                    VALUES (@id, @tenant, @display_name, @created_at)
                    """,
                    [
                        ("id", connectionId.Value),
                        ("tenant", tenant),
                        ("display_name", "pre-migration-connection"),
                        ("created_at", createdAt),
                    ],
                    ct);

                await ExecuteAsync(
                    connection,
                    """
                    INSERT INTO provider_connection_revisions
                        (id, provider_connection_id, tenant, revision, adapter_kind, endpoint, credential_ref,
                         declared_capabilities, configuration_hash, created_at, created_by)
                    VALUES
                        (@id, @provider_connection_id, @tenant, @revision, @adapter_kind, @endpoint, @credential_ref,
                         @declared_capabilities, @configuration_hash, @created_at, @created_by)
                    """,
                    [
                        ("id", revisionId.Value),
                        ("provider_connection_id", connectionId.Value),
                        ("tenant", tenant),
                        ("revision", 1),
                        ("adapter_kind", "Scripted"),
                        ("endpoint", "https://pre-migration.test"),
                        ("credential_ref", "env://PRE_MIGRATION_KEY"),
                        ("declared_capabilities", 0),
                        ("configuration_hash", "pre-migration-revision-hash"),
                        ("created_at", createdAt),
                        ("created_by", "test"),
                    ],
                    ct);

                // provider_connections carries no immutability trigger (only
                // the revision, deployment, release and verification tables
                // do), so this mirrors what the application does today: point
                // the connection at its revision after the revision exists.
                await ExecuteAsync(
                    connection,
                    """
                    UPDATE provider_connections SET active_revision_id = @active_revision_id
                    WHERE id = @id AND tenant = @tenant
                    """,
                    [
                        ("active_revision_id", revisionId.Value),
                        ("id", connectionId.Value),
                        ("tenant", tenant),
                    ],
                    ct);

                await ExecuteAsync(
                    connection,
                    """
                    INSERT INTO model_deployment_versions
                        (id, tenant, logical_name, version, provider_connection_revision_id, provider_model,
                         required_capabilities, temperature, max_output_tokens, timeout_milliseconds,
                         input_cost_per_million_tokens, output_cost_per_million_tokens, currency,
                         provider_parameters_json, configuration_hash, created_at, created_by)
                    VALUES
                        (@id, @tenant, @logical_name, @version, @provider_connection_revision_id, @provider_model,
                         @required_capabilities, @temperature, @max_output_tokens, @timeout_milliseconds,
                         @input_cost_per_million_tokens, @output_cost_per_million_tokens, @currency,
                         @provider_parameters_json::jsonb, @configuration_hash, @created_at, @created_by)
                    """,
                    [
                        ("id", deploymentId.Value),
                        ("tenant", tenant),
                        ("logical_name", "pre-migration-deployment"),
                        ("version", 1),
                        ("provider_connection_revision_id", revisionId.Value),
                        ("provider_model", "scripted-model"),
                        ("required_capabilities", 0),
                        ("temperature", 0.2f),
                        ("max_output_tokens", 1024),
                        ("timeout_milliseconds", 30_000),
                        ("input_cost_per_million_tokens", 1.5m),
                        ("output_cost_per_million_tokens", 3.0m),
                        ("currency", "USD"),
                        ("provider_parameters_json", "{}"),
                        ("configuration_hash", "pre-migration-deployment-hash"),
                        ("created_at", createdAt),
                        ("created_by", "test"),
                    ],
                    ct);

                await ExecuteAsync(
                    connection,
                    """
                    INSERT INTO model_assignment_releases
                        (id, tenant, name, version, classifier_deployment_version_id, correlator_deployment_version_id,
                         configuration_hash, created_at, created_by)
                    VALUES
                        (@id, @tenant, @name, @version, @classifier_deployment_version_id,
                         @correlator_deployment_version_id, @configuration_hash, @created_at, @created_by)
                    """,
                    [
                        ("id", releaseId.Value),
                        ("tenant", tenant),
                        ("name", "pre-migration-release"),
                        ("version", 1),
                        ("classifier_deployment_version_id", deploymentId.Value),
                        ("correlator_deployment_version_id", deploymentId.Value),
                        ("configuration_hash", "pre-migration-release-hash"),
                        ("created_at", createdAt),
                        ("created_by", "test"),
                    ],
                    ct);

                await ExecuteAsync(
                    connection,
                    """
                    INSERT INTO model_assignment_agent_slots (assignment_release_id, tenant, agent_name, deployment_version_id)
                    VALUES (@assignment_release_id, @tenant, @agent_name, @deployment_version_id)
                    """,
                    [
                        ("assignment_release_id", releaseId.Value),
                        ("tenant", tenant),
                        ("agent_name", "app-expert"),
                        ("deployment_version_id", deploymentId.Value),
                    ],
                    ct);

                await ExecuteAsync(
                    connection,
                    """
                    INSERT INTO model_assignment_evaluations
                        (id, tenant, assignment_release_id, dataset, baseline, status, passed, artifact_hash,
                         requested_at, requested_by, completed_at)
                    VALUES
                        (@id, @tenant, @assignment_release_id, @dataset, @baseline, @status, @passed, @artifact_hash,
                         @requested_at, @requested_by, @completed_at)
                    """,
                    [
                        ("id", evaluationId.Value),
                        ("tenant", tenant),
                        ("assignment_release_id", releaseId.Value),
                        ("dataset", "pre-migration-dataset"),
                        ("baseline", "pre-migration-baseline"),
                        ("status", "completed"),
                        ("passed", true),
                        ("artifact_hash", "pre-migration-artifact-hash"),
                        ("requested_at", createdAt),
                        ("requested_by", "test"),
                        ("completed_at", createdAt),
                    ],
                    ct);

                await ExecuteAsync(
                    connection,
                    """
                    INSERT INTO provider_revision_verifications
                        (id, tenant, provider_connection_revision_id, verified_capabilities, request_id, verified_at, verified_by)
                    VALUES
                        (@id, @tenant, @provider_connection_revision_id, @verified_capabilities, @request_id, @verified_at, @verified_by)
                    """,
                    [
                        ("id", verificationId),
                        ("tenant", tenant),
                        ("provider_connection_revision_id", revisionId.Value),
                        ("verified_capabilities", 0),
                        ("request_id", "pre-migration-request"),
                        ("verified_at", createdAt),
                        ("verified_by", "test"),
                    ],
                    ct);

                await ExecuteAsync(
                    connection,
                    """
                    INSERT INTO tenant_active_model_assignments (tenant, assignment_release_id, activated_at, activated_by)
                    VALUES (@tenant, @assignment_release_id, @activated_at, @activated_by)
                    """,
                    [
                        ("tenant", tenant),
                        ("assignment_release_id", releaseId.Value),
                        ("activated_at", createdAt),
                        ("activated_by", "test"),
                    ],
                    ct);

                // The risky part: dropping and recreating primary keys,
                // alternate keys and composite foreign keys on tables that
                // already hold the eight related rows just seeded.
                await migrator.MigrateAsync(cancellationToken: ct);
            }

            await using SreDbContext upgraded = new(optionsBuilder.Options);

            ProviderConnectionEntity? connection2 = await upgraded.ProviderConnections
                .SingleOrDefaultAsync(item => item.Tenant == tenant, ct);
            connection2.ShouldNotBeNull();
            connection2.Id.ShouldBe(connectionId);
            connection2.Product.ShouldBe(string.Empty);
            connection2.Environment.ShouldBe(string.Empty);
            connection2.DisplayName.ShouldBe("pre-migration-connection");
            connection2.ActiveRevisionId.ShouldBe(revisionId);

            ProviderConnectionRevisionEntity? revision = await upgraded.ProviderConnectionRevisions
                .SingleOrDefaultAsync(item => item.Tenant == tenant, ct);
            revision.ShouldNotBeNull();
            revision.Id.ShouldBe(revisionId);
            revision.ProviderConnectionId.ShouldBe(connectionId);
            revision.Product.ShouldBe(string.Empty);
            revision.Environment.ShouldBe(string.Empty);
            revision.ConfigurationHash.ShouldBe("pre-migration-revision-hash");

            ModelDeploymentVersionEntity? deployment = await upgraded.ModelDeploymentVersions
                .SingleOrDefaultAsync(item => item.Tenant == tenant, ct);
            deployment.ShouldNotBeNull();
            deployment.Id.ShouldBe(deploymentId);
            deployment.ProviderConnectionRevisionId.ShouldBe(revisionId);
            deployment.Product.ShouldBe(string.Empty);
            deployment.Environment.ShouldBe(string.Empty);
            deployment.ConfigurationHash.ShouldBe("pre-migration-deployment-hash");

            ModelAssignmentReleaseEntity? release = await upgraded.ModelAssignmentReleases
                .SingleOrDefaultAsync(item => item.Tenant == tenant, ct);
            release.ShouldNotBeNull();
            release.Id.ShouldBe(releaseId);
            release.ClassifierDeploymentVersionId.ShouldBe(deploymentId);
            release.CorrelatorDeploymentVersionId.ShouldBe(deploymentId);
            release.Product.ShouldBe(string.Empty);
            release.Environment.ShouldBe(string.Empty);
            release.ConfigurationHash.ShouldBe("pre-migration-release-hash");

            ModelAssignmentAgentSlotEntity? slot = await upgraded.ModelAssignmentAgentSlots
                .SingleOrDefaultAsync(item => item.Tenant == tenant, ct);
            slot.ShouldNotBeNull();
            slot.AssignmentReleaseId.ShouldBe(releaseId);
            slot.DeploymentVersionId.ShouldBe(deploymentId);
            slot.AgentName.ShouldBe("app-expert");
            slot.Product.ShouldBe(string.Empty);
            slot.Environment.ShouldBe(string.Empty);

            ModelAssignmentEvaluationEntity? evaluation = await upgraded.ModelAssignmentEvaluations
                .SingleOrDefaultAsync(item => item.Tenant == tenant, ct);
            evaluation.ShouldNotBeNull();
            evaluation.Id.ShouldBe(evaluationId);
            evaluation.AssignmentReleaseId.ShouldBe(releaseId);
            evaluation.Product.ShouldBe(string.Empty);
            evaluation.Environment.ShouldBe(string.Empty);

            ProviderRevisionVerificationEntity? verification = await upgraded.ProviderRevisionVerifications
                .SingleOrDefaultAsync(item => item.Tenant == tenant, ct);
            verification.ShouldNotBeNull();
            verification.Id.ShouldBe(verificationId);
            verification.ProviderConnectionRevisionId.ShouldBe(revisionId);
            verification.Product.ShouldBe(string.Empty);
            verification.Environment.ShouldBe(string.Empty);

            TenantActiveModelAssignmentEntity? active = await upgraded.TenantActiveModelAssignments
                .SingleOrDefaultAsync(item => item.Tenant == tenant, ct);
            active.ShouldNotBeNull();
            active.AssignmentReleaseId.ShouldBe(releaseId);
            active.Product.ShouldBe(string.Empty);
            active.Environment.ShouldBe(string.Empty);

            // Every seeded row survived exactly once: the drop-and-recreate
            // sequence neither duplicated a row nor silently dropped one.
            (await upgraded.ProviderConnections.CountAsync(item => item.Tenant == tenant, ct)).ShouldBe(1);
            (await upgraded.ProviderConnectionRevisions.CountAsync(item => item.Tenant == tenant, ct)).ShouldBe(1);
            (await upgraded.ModelDeploymentVersions.CountAsync(item => item.Tenant == tenant, ct)).ShouldBe(1);
            (await upgraded.ModelAssignmentReleases.CountAsync(item => item.Tenant == tenant, ct)).ShouldBe(1);
            (await upgraded.ModelAssignmentAgentSlots.CountAsync(item => item.Tenant == tenant, ct)).ShouldBe(1);
            (await upgraded.ModelAssignmentEvaluations.CountAsync(item => item.Tenant == tenant, ct)).ShouldBe(1);
            (await upgraded.ProviderRevisionVerifications.CountAsync(item => item.Tenant == tenant, ct)).ShouldBe(1);
            (await upgraded.TenantActiveModelAssignments.CountAsync(item => item.Tenant == tenant, ct)).ShouldBe(1);

            // The active-assignment invariant still holds on the widened key
            // for a row that migrated forward, not merely for a row created
            // fresh under the new schema: a second activation for the same
            // (Tenant, Product, Environment) must still collide at the
            // database.
            DbUpdateException exception = await Should.ThrowAsync<DbUpdateException>(async () =>
            {
                await using SreDbContext duplicate = new(optionsBuilder.Options);
                duplicate.TenantActiveModelAssignments.Add(new TenantActiveModelAssignmentEntity
                {
                    Tenant = tenant,
                    Product = string.Empty,
                    Environment = string.Empty,
                    AssignmentReleaseId = releaseId,
                    ActivatedAt = createdAt,
                    ActivatedBy = "second",
                });
                await duplicate.SaveChangesAsync(ct);
            });
            PostgresException? postgresException = exception.InnerException as PostgresException;
            postgresException.ShouldNotBeNull();
            postgresException.SqlState.ShouldBe(PostgresErrorCodes.UniqueViolation);
        }
        finally
        {
            // Npgsql pools connections by connection string; WITH (FORCE)
            // terminates any that are still open rather than leaving a
            // dropped-but-not-really database behind for the next run.
            await using NpgsqlConnection admin = new(adminConnectionString);
            await admin.OpenAsync(CancellationToken.None);
            await using NpgsqlCommand drop = admin.CreateCommand();
            drop.CommandText = $"DROP DATABASE IF EXISTS \"{databaseName}\" WITH (FORCE)";
            await drop.ExecuteNonQueryAsync(CancellationToken.None);
        }
    }

    private static async Task ExecuteAsync(
        NpgsqlConnection connection,
        string sql,
        (string Name, object Value)[] parameters,
        CancellationToken cancellationToken)
    {
        await using NpgsqlCommand command = connection.CreateCommand();
        command.CommandText = sql;

        foreach ((string name, object value) in parameters)
        {
            command.Parameters.AddWithValue(name, value);
        }

        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    [Fact]
    public async Task TwoActiveAssignmentsForOneScopeAreImpossibleAtTheDatabaseLevel()
    {
        // tenant_active_model_assignments' primary key widened from (Tenant)
        // to (Tenant, Product, Environment) (D40): "one active assignment per
        // scope" has to be enforced by Postgres, not by application code two
        // racing activations could both pass.
        CancellationToken ct = TestContext.Current.CancellationToken;
        TenantScope scope = TenantScope.Full("dup-active-" + Guid.NewGuid(), "checkout", "production");

        await using SreDbContext setup = postgres.CreateContext();
        ModelAssignmentReleaseId releaseId = await CreateReleaseChainAsync(setup, scope, ct);

        await using SreDbContext first = postgres.CreateContext();
        await using SreDbContext second = postgres.CreateContext();

        first.TenantActiveModelAssignments.Add(new TenantActiveModelAssignmentEntity
        {
            Tenant = scope.Tenant,
            Product = scope.Product,
            Environment = scope.Environment,
            AssignmentReleaseId = releaseId,
            ActivatedAt = DateTimeOffset.UtcNow,
            ActivatedBy = "first",
        });
        second.TenantActiveModelAssignments.Add(new TenantActiveModelAssignmentEntity
        {
            Tenant = scope.Tenant,
            Product = scope.Product,
            Environment = scope.Environment,
            AssignmentReleaseId = releaseId,
            ActivatedAt = DateTimeOffset.UtcNow,
            ActivatedBy = "second",
        });

        await first.SaveChangesAsync(ct);

        // The loser fails at commit, on the primary key, not on an
        // application-level check either racer could pass first.
        DbUpdateException exception = await Should.ThrowAsync<DbUpdateException>(
            async () => await second.SaveChangesAsync(ct));
        PostgresException? postgresException = exception.InnerException as PostgresException;
        postgresException.ShouldNotBeNull();
        postgresException.SqlState.ShouldBe(PostgresErrorCodes.UniqueViolation);
    }
}
