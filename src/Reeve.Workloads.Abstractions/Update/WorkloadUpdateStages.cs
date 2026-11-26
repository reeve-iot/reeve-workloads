using Reeve.Pipeline;

namespace Reeve.Workloads.Abstractions.Update;

/// <summary>The ids of the stages this library ships. A host names one of these to place its own stage — a
/// rolling plugin reload declares <c>After(WorkloadUpdateStages.CommitVersion)</c> —
/// so they are public surface and changing one is a breaking change like any other.</summary>
/// <remarks>How often a stage is called, which is what a host stage can build on: one call per stage per tick, and
/// a stage that answers <c>Done</c> hands to the next in the same tick. The walk goes over every open update in turn
/// and then waits the working-tick interval (<c>Workloads:UpdateWalk:WorkingTickSeconds</c>, five seconds unless the
/// host configures it) before the next pass, or less after <see cref="IWorkloadUpdateWalkTrigger.Wake"/>. So a stage
/// that answers <c>Working</c> is asked again after the rest of that pass and the interval — not a fixed time after it
/// answered. Calls come from the leader only, one at a time per group, and never while a drift reconcile of the group
/// runs: a pass waits for one to finish before walking that group.</remarks>
public static class WorkloadUpdateStages
{
    /// <summary>Compute the diff, verify parameters, persist the snapshots.</summary>
    public const string Params = "stage.params";

    /// <summary>Apply each workload's new spec in declaration order.</summary>
    public const string Sidecar = "stage.sidecar";

    /// <summary>Wait until the operator has redeployed to the target every sidecar of a provider that materialises
    /// nothing, whatever else the group holds; a workload the update adds is not waited on. Passes at once for a plan
    /// without one. It runs before the health gate, which can only certify the sidecar the operator deployed.</summary>
    public const string OperatorRedeploy = "stage.operator-redeploy";

    /// <summary>Tick the health gate per workload until every one passes.</summary>
    public const string HealthGate = "stage.health-gate";

    /// <summary>Move the group's declared version. Every host stage runs after this one —
    /// <see cref="WorkloadUpdatePipeline.Resolve"/> refuses one that does not — which is how the ordering enforces
    /// what <c>IWorkloadUpdateEffects</c> can otherwise only ask for in a comment. It also creates the rows of the
    /// workloads the update added before the version moves, and its compensation deletes them once it has moved the
    /// version back.</summary>
    public const string CommitVersion = "stage.commit-version";

    /// <summary>Commit the parameters as the new last-known-good. Always last.</summary>
    public const string CommitParams = "stage.commit-params";

    /// <summary>The ordinal <see cref="CommitParams"/> is placed with, and it is load-bearing.
    ///
    /// The engine breaks a tie between two stages anchored to the same one by (ordinal, id) — ordinally, by
    /// STRING. A host stage declaring <c>After(CommitVersion)</c>, which is the placement a host stage is expected to take, would otherwise be ordered against "stage.commit-params" alphabetically and lose: "c" sorts before
    /// "h", so "stage.host.plugin-roll" would run AFTER the parameters were committed. Nothing about that is
    /// intended, and nothing about it would be obvious — renaming a host stage would silently move it.
    ///
    /// A maximal ordinal says what is actually meant: whatever else is anchored at the version commit, the
    /// parameter commit is last.</summary>
    public const int CommitParamsOrdinal = int.MaxValue;

    /// <summary>The six, in the order they resolve to when no host stage is registered.</summary>
    public static IReadOnlyList<string> Core { get; } =
        [Params, Sidecar, OperatorRedeploy, HealthGate, CommitVersion, CommitParams];
}

/// <summary>Resolution of the update pipeline, which is <see cref="Pipeline{TContext}.Resolve"/> plus the rules
/// that are this library's own rather than the engine's.</summary>
/// <remarks>The engine refuses an empty set, a duplicate id, an unknown anchor, more or fewer than one head and
/// a cycle — all at resolve time, and all tested in <c>Reeve.Pipeline</c>. What it cannot know is that THIS
/// pipeline has a first and a last stage that mean something: the diff must be computed before anything is
/// applied, and the parameters must be committed after everything else has succeeded. A host stage outside
/// those bounds is a composition error, and it is refused the way this library already refuses an engine started
/// against a schema nobody has vouched for.
///
/// <para>A host stage runs after <see cref="WorkloadUpdateStages.CommitVersion"/>. The row carries a phase
/// projected from where the walk stands, and that projection has no place for a stage of the host's before the
/// version is committed: there is no phase such a stage could be in, or that would let it fail past a point of no
/// return. </para></remarks>
public static class WorkloadUpdatePipeline
{
    /// <summary>Resolves the walk, or throws <see cref="PipelineDefinitionException"/> naming what is wrong.</summary>
    public static Pipeline<WorkloadUpdateStageContext> Resolve(
        IEnumerable<IStage<WorkloadUpdateStageContext>> stages)
    {
        ArgumentNullException.ThrowIfNull(stages);

        var resolved = Pipeline<WorkloadUpdateStageContext>.Resolve(stages);

        var first = resolved.Order[0];
        if (!string.Equals(first, WorkloadUpdateStages.Params, StringComparison.Ordinal))
        {
            throw new PipelineDefinitionException(
                $"Stage '{first}' is placed before '{WorkloadUpdateStages.Params}'. Nothing may run before the "
                + "diff is computed and the parameter snapshots are taken, because a stage that fails before "
                + "them has nothing to be compensated back to.");
        }

        var commit = IndexOf(resolved.Order, WorkloadUpdateStages.CommitVersion);
        for (var i = 0; i < resolved.Order.Count; i++)
        {
            var id = resolved.Order[i];
            if (i < commit && !WorkloadUpdateStages.Core.Contains(id, StringComparer.Ordinal))
            {
                throw new PipelineDefinitionException(
                    $"Stage '{id}' runs before '{WorkloadUpdateStages.CommitVersion}'. A host stage runs "
                    + "after the version is committed: the row carries a projected phase, and no "
                    + "phase before the commit can hold a stage of the host's.");
            }
        }

        var last = resolved.Order[^1];
        if (!string.Equals(last, WorkloadUpdateStages.CommitParams, StringComparison.Ordinal))
        {
            throw new PipelineDefinitionException(
                $"Stage '{last}' is placed after '{WorkloadUpdateStages.CommitParams}'. Nothing may run after "
                + "the parameters are committed as the last-known-good, because by then the update is the "
                + "state a later failure would be compensated back TO.");
        }

        return resolved;
    }

    private static int IndexOf(IReadOnlyList<string> order, string id)
    {
        for (var i = 0; i < order.Count; i++)
        {
            if (string.Equals(order[i], id, StringComparison.Ordinal))
            {
                return i;
            }
        }

        return order.Count;
    }
}
