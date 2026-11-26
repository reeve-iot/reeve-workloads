using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Reeve.Pipeline;
using Reeve.Workloads.Abstractions;
using Reeve.Workloads.Abstractions.Update;
using Reeve.Workloads.Core;

namespace Reeve.Workloads.DependencyInjection;

/// <summary>The one way to start the engine.
///
/// It lives in <c>Reeve.Workloads.Core</c> rather than in the <c>Reeve.Workloads.DependencyInjection</c> project
/// whose namespace it shares, and that is deliberate: Core references THAT project, not the reverse, so an
/// extension living there could not name <see cref="WorkloadsEngineRunner"/> at all.
/// <c>ReeveWorkloadsCoreBuilderExtensions</c> is arranged the same way, for the same reason — a host sees one
/// namespace and never learns which assembly each half came from.</summary>
public static class ReeveWorkloadsRunExtensions
{
    /// <summary>Migrates the workloads database, then starts the engine: it contends for the single leadership and
    /// runs every engine unit for as long as it holds it.
    ///
    /// Returns when the engine is RUNNING, not when it has won. A replica that will never be leader must not block
    /// its own startup, so this returns as soon as the loop is contending.
    ///
    /// Calling it twice starts ONE engine and logs a warning. Two loops in one process would contend against each
    /// other and both reconcile the same fleet, which is the exact thing the leadership exists to prevent.</summary>
    /// <param name="services">The built provider. The root one: the engine's runner, units and start signal are all
    /// singletons, and a scope would resolve the same instances while adding a lifetime nobody needs.</param>
    /// <param name="runMigrations">Apply the library's migrations first. True by default, because the common host's
    /// whole deployment is "start the process". A host that migrates in a deployment step passes false. Several
    /// replicas calling this at once are serialised by EF Core's own migration lock, not by anything here — and
    /// only once the history table exists, because the lock is taken on that table. On an empty database, start
    /// one replica first or migrate in a step of your own.</param>
    /// <param name="cancellationToken">The engine's "run until this" token, not a token that merely bounds this
    /// call: cancelling it later shuts the engine down.</param>
    /// <exception cref="InvalidOperationException"><paramref name="runMigrations"/> is true and no
    /// <see cref="IWorkloadsSchemaMigrator"/> is registered; or the update pipeline cannot be resolved — a host
    /// stage belongs after <see cref="WorkloadUpdateStages.CommitVersion"/> and before
    /// <see cref="WorkloadUpdateStages.CommitParams"/>, and the message names which rule it broke; or two health
    /// contributors observe one health mode, or more than one rollout certifier is registered. Nothing has been
    /// migrated or started when any of these is thrown.</exception>
    public static async Task RunReeveWorkloadsAsync(
        this IServiceProvider services,
        bool runMigrations = true,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(services);

        var logger = services.GetService<ILoggerFactory>()?.CreateLogger(typeof(ReeveWorkloadsRunExtensions))
            ?? (ILogger)NullLogger.Instance;

        var signal = services.GetService<WorkloadsEngineStartSignal>()
            ?? throw new InvalidOperationException(
                "RunReeveWorkloadsAsync was called on a service provider that has no Reeve workloads engine in "
                + "it. Call AddReeveWorkloads(...).AddEngine() when composing the container, before building the "
                + "provider this was called on.");

        await RefuseAnUnwalkableUpdatePipelineAsync(services).ConfigureAwait(false);

        WorkloadSeamRegistrationCheck.Run(services.GetRequiredService<IServiceScopeFactory>());

        if (!signal.TryMarkStarted())
        {
            logger.LogWarning(
                "RunReeveWorkloadsAsync was called more than once. One engine is running; this call started "
                + "nothing. A host with two startup paths into the engine has a bug it cannot see from outside, "
                + "because the second path looks like it worked.");
            return;
        }

        try
        {
            if (runMigrations)
            {
                await MigrateAsync(services, cancellationToken).ConfigureAwait(false);
            }

            await services.GetRequiredService<WorkloadsEngineRunner>()
                .StartAsync(cancellationToken)
                .ConfigureAwait(false);
        }
        catch
        {
            signal.Unmark();
            throw;
        }
    }

    private static async Task RefuseAnUnwalkableUpdatePipelineAsync(IServiceProvider services)
    {
        var scope = services.CreateAsyncScope();
        await using var scopeLifetime = scope.ConfigureAwait(false);

        try
        {
            _ = scope.ServiceProvider.GetRequiredService<Pipeline<WorkloadUpdateStageContext>>();
        }
        catch (PipelineDefinitionException refused)
        {
            throw new InvalidOperationException(
                "RunReeveWorkloadsAsync refused to start: the update pipeline this composition registers cannot "
                + $"be walked. {refused.Message} Place a host stage after "
                + $"'{WorkloadUpdateStages.CommitVersion}' and before '{WorkloadUpdateStages.CommitParams}': "
                + $"declare StagePlacement.After({nameof(WorkloadUpdateStages)}."
                + $"{nameof(WorkloadUpdateStages.CommitVersion)}).",
                refused);
        }
    }

    private static async Task MigrateAsync(IServiceProvider services, CancellationToken cancellationToken)
    {
        var scope = services.CreateAsyncScope();
        await using var scopeLifetime = scope.ConfigureAwait(false);

        var migrator = scope.ServiceProvider.GetService<IWorkloadsSchemaMigrator>()
            ?? throw new InvalidOperationException(
                $"RunReeveWorkloadsAsync was asked to migrate, but no {nameof(IWorkloadsSchemaMigrator)} is "
                + "registered. Add a persistence package's stores — AddEntityFrameworkStores() is the one this "
                + "library ships — or pass runMigrations: false if this host applies the workloads schema "
                + "itself. Starting the engine against a schema nobody has vouched for is not an option this "
                + "library offers: the first symptom would be a SQL error from somewhere unrelated, long after "
                + "startup.");

        await migrator.MigrateAsync(cancellationToken).ConfigureAwait(false);
    }
}
