using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Reeve.Workloads.Abstractions;
using Reeve.Workloads.Abstractions.Contracts;
using Reeve.Workloads.Abstractions.Contracts.Deployment;
using Reeve.Workloads.Abstractions.Domain;
using Reeve.Workloads.Abstractions.Domain.DesiredSpec;
using Reeve.Workloads.Abstractions.Domain.Entities;
using Reeve.Workloads.Abstractions.Providers;
using Reeve.Workloads.Abstractions.Update;
using Reeve.Workloads.Core.Bootstrap;
using Reeve.Workloads.Core.HealthGate;
using Reeve.Workloads.Domain;
using Reeve.Workloads.Domain.DesiredSpec;

namespace Reeve.Workloads.Core.Update;


/// <summary>Renders the candidate target specs exactly as the reconcile pass renders the current ones, so the
/// whole atomic update rests on the target being the spec the provider will actually apply.</summary>
/// <remarks>The lookups are one port read each and the bootstrap map is a call to the function
/// <see cref="Groups.GroupManifestReconcileInputsProvider"/> calls — which makes "the update renders what
/// the drift pass converges on" a property of the code rather than of two copies staying in step.</remarks>
public sealed class GroupManifestUpdatePlanner : IWorkloadUpdatePlanner
{
    private readonly IWorkloadGroupManifestSource manifests;
    private readonly IWorkloadGroupRepository groups;
    private readonly IWorkloadInstanceRepository instanceRepository;
    private readonly IWorkloadOrchestratorSettingsService settingsService;
    private readonly IWorkloadInstallationIdentity installationIdentity;
    private readonly IWorkloadProviderRegistry providerRegistry;
    private readonly IWorkloadResourceNamerRegistry namers;
    private readonly IWorkloadHostBootstrapSource hostBootstrap;
    private readonly ProductImageOptions productImages;
    private readonly ILogger<GroupManifestUpdatePlanner> logger;
    private readonly IRolloutCertificationPolicy certification;
    private readonly WorkloadParameterSecretCodec parameterSecrets;
    private readonly IWorkloadParameterReferenceResolver parameterReferenceResolver;

    /// <summary>Builds the planner over the registered certifiers, of which there is one at most: which modes
    /// require its evidence decides each item's certification.</summary>
    /// <exception cref="InvalidOperationException">More than one certifier is registered.</exception>
    public GroupManifestUpdatePlanner(
        IWorkloadGroupManifestSource manifests,
        IWorkloadGroupRepository groups,
        IWorkloadInstanceRepository instanceRepository,
        IWorkloadOrchestratorSettingsService settingsService,
        IWorkloadInstallationIdentity installationIdentity,
        IWorkloadProviderRegistry providerRegistry,
        IWorkloadResourceNamerRegistry namers,
        IWorkloadHostBootstrapSource hostBootstrap,
        IOptions<ProductImageOptions> productImages,
        ILogger<GroupManifestUpdatePlanner> logger,
        IEnumerable<IRolloutCertifier> certifiers,
        WorkloadParameterSecretCodec parameterSecrets,
        IWorkloadParameterReferenceResolver parameterReferenceResolver)
        : this(manifests, groups, instanceRepository, settingsService, installationIdentity, providerRegistry, namers,
            hostBootstrap, productImages, logger, new RolloutCertificationPolicy(certifiers), parameterSecrets, parameterReferenceResolver)
    {
    }

    internal GroupManifestUpdatePlanner(
        IWorkloadGroupManifestSource manifests,
        IWorkloadGroupRepository groups,
        IWorkloadInstanceRepository instanceRepository,
        IWorkloadOrchestratorSettingsService settingsService,
        IWorkloadInstallationIdentity installationIdentity,
        IWorkloadProviderRegistry providerRegistry,
        IWorkloadResourceNamerRegistry namers,
        IWorkloadHostBootstrapSource hostBootstrap,
        IOptions<ProductImageOptions> productImages,
        ILogger<GroupManifestUpdatePlanner> logger,
        IRolloutCertificationPolicy certification,
        WorkloadParameterSecretCodec parameterSecrets,
        IWorkloadParameterReferenceResolver parameterReferenceResolver)
    {
        this.manifests = manifests ?? throw new ArgumentNullException(nameof(manifests));
        this.groups = groups ?? throw new ArgumentNullException(nameof(groups));
        this.instanceRepository = instanceRepository ?? throw new ArgumentNullException(nameof(instanceRepository));
        this.settingsService = settingsService ?? throw new ArgumentNullException(nameof(settingsService));
        this.installationIdentity = installationIdentity ?? throw new ArgumentNullException(nameof(installationIdentity));
        this.providerRegistry = providerRegistry ?? throw new ArgumentNullException(nameof(providerRegistry));
        this.namers = namers ?? throw new ArgumentNullException(nameof(namers));
        this.hostBootstrap = hostBootstrap ?? throw new ArgumentNullException(nameof(hostBootstrap));
        ArgumentNullException.ThrowIfNull(productImages);
        this.productImages = productImages.Value;
        this.logger = logger ?? throw new ArgumentNullException(nameof(logger));
        this.certification = certification ?? throw new ArgumentNullException(nameof(certification));
        this.parameterSecrets = parameterSecrets ?? throw new ArgumentNullException(nameof(parameterSecrets));
        this.parameterReferenceResolver = parameterReferenceResolver ?? throw new ArgumentNullException(nameof(parameterReferenceResolver));
    }

    /// <summary>Builds the upgrade plan from the version the group's row declares now, for no intent: a candidate
    /// workload with no row gets a random id, so the result is a read, not a plan for a provider to act on. The
    /// submitted name→value map merges over each workload's stored parameters; an adopting id then drops its
    /// overrides on the axes whose declaration moved.</summary>
    /// <exception cref="WorkloadHostRefusedException">The host refused either side's bootstrap.</exception>
    /// <exception cref="InvalidOperationException">No plan can be built for another reason, such as no instance, no
    /// module row or no candidate manifest.</exception>
    public Task<WorkloadUpdatePlan> BuildPlanAsync(
        Guid groupInstanceId,
        string candidateVersion,
        IReadOnlyDictionary<string, string?>? operatorParameters = null,
        IReadOnlyCollection<string>? adoptDeclaredResourceWorkloadIds = null,
        CancellationToken cancellationToken = default)
        => BuildAsync(groupInstanceId, candidateVersion, intentId: null, operatorParameters,
            adoptDeclaredResourceWorkloadIds, origin: null, cancellationToken);

    Task<WorkloadUpdatePlan> IWorkloadUpdatePlanner.BuildPlanAsync(
        Guid groupInstanceId,
        string candidateVersion,
        Guid? intentId,
        IReadOnlyDictionary<string, string?>? operatorParameters,
        IReadOnlyCollection<string>? adoptDeclaredResourceWorkloadIds,
        WorkloadUpdatePlanOrigin? origin,
        CancellationToken cancellationToken)
        => BuildAsync(groupInstanceId, candidateVersion, intentId, operatorParameters, adoptDeclaredResourceWorkloadIds,
            origin, cancellationToken);

    private async Task<WorkloadUpdatePlan> BuildAsync(
        Guid groupInstanceId,
        string candidateVersion,
        Guid? intentId,
        IReadOnlyDictionary<string, string?>? operatorParameters,
        IReadOnlyCollection<string>? adoptDeclaredResourceWorkloadIds,
        WorkloadUpdatePlanOrigin? origin,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(candidateVersion);

        var submittedParameters = operatorParameters;

        var adoptingWorkloads = adoptDeclaredResourceWorkloadIds is { Count: > 0 }
            ? new HashSet<string>(adoptDeclaredResourceWorkloadIds, StringComparer.Ordinal)
            : null;

        var groupId = (await instanceRepository.ListByPluginInstanceAsync(groupInstanceId, cancellationToken)
                .ConfigureAwait(false))
            .Select(row => row.GroupId)
            .FirstOrDefault()
            ?? throw new InvalidOperationException($"No module instance {groupInstanceId} to plan an update for.");

        var module = await groups.GetAsync(groupId, cancellationToken).ConfigureAwait(false)
            ?? throw new InvalidOperationException(
                $"Module '{groupId}' has no module row to plan an update against; the host adopts it first.");

        var fromVersion = origin is { } started ? started.FromVersion : module.InstalledVersion;

        var candidateDeployment =
            (await manifests.GetAtVersionAsync(module.SourceKey, groupId, candidateVersion, cancellationToken)
                .ConfigureAwait(false)).Manifest
            ?? throw new InvalidOperationException(
                $"Candidate manifest for '{groupId}' version '{candidateVersion}' could not be resolved with a valid deployment section (release not materialized?).");

        var previousRead = fromVersion is null
            ? GroupManifestRead.Absent
            : await manifests.GetAtVersionAsync(module.SourceKey, groupId, fromVersion, cancellationToken)
                .ConfigureAwait(false);

        var previousDeployment = previousRead.Manifest;

        var settingsResponse = await settingsService.GetAsync(cancellationToken).ConfigureAwait(false);
        var settings = settingsResponse.Settings;
        var installationId = await installationIdentity.GetInstallationIdAsync(cancellationToken).ConfigureAwait(false);

        var instances = await instanceRepository
            .ListByPluginInstanceAsync(groupInstanceId, cancellationToken).ConfigureAwait(false);
        var instanceByWorkload = instances.ToDictionary(instance => instance.WorkloadId, instance => instance, StringComparer.Ordinal);
        var unboundWorkloadProvider = ProviderForUnboundWorkloads(instances, settingsResponse.EffectiveProvider);

        var previousByWorkload = previousDeployment?.Workloads
            .ToDictionary(workload => workload.Id, workload => workload, StringComparer.Ordinal)
            ?? new Dictionary<string, WorkloadManifest>(StringComparer.Ordinal);

        var items = new List<WorkloadUpdatePlanItem>();
        var targetSpecMap = new Dictionary<string, WorkloadDesiredSpec>(StringComparer.Ordinal);
        var previousSpecMap = new Dictionary<string, WorkloadDesiredSpec>(StringComparer.Ordinal);
        var previousResolvedParameters = new Dictionary<string, IReadOnlyDictionary<string, string?>>(StringComparer.Ordinal);
        var previousStoredParameters = new Dictionary<string, IReadOnlyDictionary<string, string?>>(StringComparer.Ordinal);
        var candidateResolvedParameters = new Dictionary<string, IReadOnlyDictionary<string, string?>>(StringComparer.Ordinal);
        var candidateStoredParameters = new Dictionary<string, IReadOnlyDictionary<string, string?>>(StringComparer.Ordinal);
        var candidateUnpinnedReferences = new Dictionary<string, IReadOnlyDictionary<string, string?>>(StringComparer.Ordinal);
        var previousUnpinnedReferences = new Dictionary<string, IReadOnlyDictionary<string, string?>>(StringComparer.Ordinal);
        var ambiguousExistingClaimKeys = WorkloadDeploymentParameterScope.AmbiguousExistingClaimKeys(candidateDeployment);
        var fromDigests = new Dictionary<string, string?>(StringComparer.Ordinal);
        var toDigests = new Dictionary<string, string?>(StringComparer.Ordinal);

        foreach (var candidateWorkload in candidateDeployment.Workloads)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var instance = instanceByWorkload.GetValueOrDefault(candidateWorkload.Id);
            if (instance is { State: WorkloadInstanceState.Terminating }
                && instances.Any(row => row.State != WorkloadInstanceState.Terminating))
            {
                throw new InvalidOperationException(
                    $"Workload '{candidateWorkload.Id}' of module instance {groupInstanceId} is still being torn down "
                    + "after an earlier version dropped it, and version "
                    + $"'{candidateVersion}' declares it again. The update can start once that teardown has finished "
                    + $"and the workload's row is gone{(instance.StateMessage is { Length: > 0 } why ? $" (teardown: {why})" : string.Empty)}.");
            }

            var providerId = instance?.Provider ?? unboundWorkloadProvider ?? throw new InvalidOperationException(
                $"Module instance {groupInstanceId} has workloads bound to different providers; an update cannot "
                + $"choose one for workload '{candidateWorkload.Id}', which is not bound yet.");
            var provider = providerRegistry.Describe(providerId);
            var namer = namers.For(providerId);
            var instanceId = instance?.Id
                ?? (intentId is { } intent ? AddedWorkloadInstanceId(intent, candidateWorkload.Id) : Guid.NewGuid());

            var isOwnershipReleased = instance?.IsOwnershipReleased(providerRegistry) == true;
            var storedParams = instance?.GetResolvedParameters()
                ?? (IReadOnlyDictionary<string, string?>)new Dictionary<string, string?>(StringComparer.Ordinal);
            var storedManifest = previousByWorkload.GetValueOrDefault(candidateWorkload.Id) ?? candidateWorkload;
            var builder = new DeployableParameterBuilder(parameterSecrets, parameterReferenceResolver);
            var previousBuild = await builder.BuildAsync(candidateWorkload, storedParams, provider,
                DeployableParameterApplyContext.UpdateCandidate,
                storedSchema: storedManifest, cancellationToken: cancellationToken).ConfigureAwait(false);
            var operatorParams = previousBuild.Values;
            LogIgnoredRestoreBindings(candidateWorkload, instanceId, storedParams, submittedParameters, ambiguousExistingClaimKeys);
            var submittedBuild = await builder.BuildAsync(candidateWorkload, storedParams, provider,
                DeployableParameterApplyContext.UpdateCandidate,
                submittedParameters, ambiguousExistingClaimKeys, storedManifest,
                cancellationToken: cancellationToken).ConfigureAwait(false);

            var candidateOperatorParams = adoptingWorkloads?.Contains(candidateWorkload.Id) == true
                ? DropAdoptedResourceAxes(
                    submittedBuild.Stored, previousByWorkload.GetValueOrDefault(candidateWorkload.Id), candidateWorkload)
                : submittedBuild.Stored;

            var previousResolution = previousBuild;
            var candidateResolution = ReferenceEquals(candidateOperatorParams, submittedBuild.Stored)
                ? submittedBuild
                : ResolveAdoptedCandidate(candidateWorkload, candidateOperatorParams, submittedBuild, provider);
            var previousResolved = previousResolution.Values;
            var candidateResolved = candidateResolution.Values;

            var targetBootstrap = await BootstrapAsync(
                groupId, candidateWorkload, instanceId, provider.Id, candidateVersion, namer, cancellationToken)
                .ConfigureAwait(false);

            var targetSpec = WorkloadDesiredSpecFactory.ComputeWithParameterReferences(
                candidateWorkload,
                settings,
                candidateResolved,
                instance is null
                    ? candidateResolution.ReferencesForHash
                    : UnpinnedReferenceRolloutGuard.ProjectReferencesForHash(instance, candidateResolution.ReferencesForHash),
                targetBootstrap,
                provider,
                namer,
                groupInstanceId,
                groupId,
                instanceId,
                installationId,
                productImages);

            WorkloadDesiredSpec? previousSpec = null;
            IReadOnlyDictionary<string, string?> previousUnpinned = previousResolution.UnpinnedReferences;
            if (previousByWorkload.TryGetValue(candidateWorkload.Id, out var previousWorkload))
            {
                var previousWorkloadResolution = await builder.BuildAsync(
                    previousWorkload, storedParams, provider, DeployableParameterApplyContext.RollbackTargetRender,
                    storedSchema: storedManifest,
                    cancellationToken: cancellationToken).ConfigureAwait(false);
                var previousWorkloadResolved = previousWorkloadResolution.Values;
                previousUnpinned = previousWorkloadResolution.UnpinnedReferences;
                var previousBootstrap = await BootstrapAsync(
                    groupId, previousWorkload, instanceId, provider.Id, fromVersion, namer, cancellationToken)
                    .ConfigureAwait(false);
                previousSpec = WorkloadDesiredSpecFactory.ComputeWithParameterReferences(
                    previousWorkload,
                    settings,
                    previousWorkloadResolved,
                    instance is null
                        ? previousWorkloadResolution.ReferencesForHash
                        : UnpinnedReferenceRolloutGuard.ProjectReferencesForHash(instance, previousWorkloadResolution.ReferencesForHash),
                    previousBootstrap,
                    provider,
                    namer,
                    groupInstanceId,
                    groupId,
                    instanceId,
                    installationId,
                    productImages);
                fromDigests[candidateWorkload.Id] = previousWorkload.Image.Digest;
            }

            var reference = instance?.Provider is not null
                ? WorkloadRef.FromInstance(instance)
                : WorkloadRef.FromDesiredSpec(targetSpec);

            if (isOwnershipReleased)
            {
                logger.LogInformation(
                    "Skipping ownership-FROZEN workload '{WorkloadId}' (instance {InstanceId}, state {State}) from the "
                    + "update plan for module instance {GroupInstanceId} ({From} → {To}) — the operator owns its runtime; "
                    + "the update never provider-acts on a frozen instance. Its non-frozen siblings still update.",
                    candidateWorkload.Id, instance!.Id, instance.State, groupInstanceId, fromVersion, candidateVersion);
            }
            else
            {
                var current = CurrentDeclaration(previousWorkload, candidateWorkload);
                items.Add(new WorkloadUpdatePlanItem
                {
                    WorkloadId = candidateWorkload.Id,
                    Reference = reference,
                    TargetSpec = targetSpec,
                    PreviousSpec = previousSpec,
                    CurrentExtensions = current.Extensions,
                    CandidateExtensions = candidateWorkload.Extensions,
                    Certification = certification.CertificationFor(candidateWorkload),
                    PreviousCertification = certification.CertificationFor(previousWorkload ?? candidateWorkload),
                });
            }

            targetSpecMap[candidateWorkload.Id] = targetSpec;
            if (previousSpec is not null)
            {
                previousSpecMap[candidateWorkload.Id] = previousSpec;
            }
            previousResolvedParameters[candidateWorkload.Id] = previousResolved;
            previousStoredParameters[candidateWorkload.Id] = storedParams;
            candidateResolvedParameters[candidateWorkload.Id] = candidateResolved;
            candidateStoredParameters[candidateWorkload.Id] = OperatorSuppliedValues(
                candidateOperatorParams, candidateResolved, storedManifest, candidateWorkload);
            candidateUnpinnedReferences[candidateWorkload.Id] = candidateResolution.UnpinnedReferences;
            previousUnpinnedReferences[candidateWorkload.Id] = previousUnpinned;
            toDigests[candidateWorkload.Id] = candidateWorkload.Image.Digest;
        }

        var candidateRequirements = WorkloadRequirementsFacts.FromSpecs(targetSpecMap);

        return new WorkloadUpdatePlan
        {
            GroupInstanceId = groupInstanceId,
            GroupId = groupId,
            SourceKey = module.SourceKey,
            FromVersion = fromVersion,
            ToVersion = candidateVersion,
            PreviousManifest = previousDeployment ?? FallbackPrevious(candidateDeployment, previousRead.Refused),
            PreviousUnreadable = previousDeployment is null && previousRead.Refused,
            CandidateManifest = candidateDeployment,
            PreviousRequirements = WorkloadRequirementsFacts.FromSpecs(previousSpecMap),
            CandidateRequirements = candidateRequirements,
            RequirementsFingerprint = WorkloadRequirementsFacts.Fingerprint(candidateRequirements),
            PreviousResolvedParameters = previousResolvedParameters,
            PreviousStoredParameters = previousStoredParameters,
            PreviousPasswordParameterNames = previousByWorkload.ToDictionary(
                pair => pair.Key,
                pair => (IReadOnlyList<string>)WorkloadPasswordNames.For(pair.Value).ToArray(),
                StringComparer.Ordinal),
            CandidateResolvedParameters = candidateResolvedParameters,
            CandidateStoredParameters = candidateStoredParameters,
            CandidateUnpinnedReferences = candidateUnpinnedReferences,
            PreviousUnpinnedReferences = previousUnpinnedReferences,
            Workloads = items,
            TargetSpecs = targetSpecMap,
            PreviousSpecs = previousSpecMap,
            FromDigests = fromDigests,
            ToDigests = toDigests,
        };
    }

    /// <summary>The values an update commits for a workload: the operator's own raw values (stored plus submitted;
    /// a blank stays blank, a <c>ref:</c> stays the reference), kept for the names the candidate resolves. Nothing is
    /// taken from the resolver, which fills in author defaults and swaps a blank for one: committed, either would read
    /// as the operator's value on the next update, and a later version binding a host port, a ConfigMap mount or a
    /// network attachment to that name would take an author value the parser refuses there. The one transformation
    /// is the storage encoding for the candidate's schema: a stored password the candidate no longer types as a
    /// password is decrypted here, and commit-params protects every value the candidate types as one.</summary>
    private IReadOnlyDictionary<string, string?> OperatorSuppliedValues(
        IReadOnlyDictionary<string, string?> operatorValues,
        IReadOnlyDictionary<string, string?> resolved,
        WorkloadManifest storedSchema,
        WorkloadManifest candidate)
    {
        var raw = operatorValues
            .Where(pair => resolved.ContainsKey(pair.Key))
            .ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.Ordinal);
        var noLongerPasswords = WorkloadPasswordNames.For(storedSchema)
            .Except(WorkloadPasswordNames.For(candidate), StringComparer.Ordinal)
            .ToList();
        return noLongerPasswords.Count == 0
            ? raw
            : parameterSecrets.UnprotectNamed(storedSchema, raw, noLongerPasswords);
    }

    private static WorkloadProviderId? ProviderForUnboundWorkloads(
        IReadOnlyList<WorkloadInstance> instances, WorkloadProviderId effectiveProvider)
    {
        var bound = instances
            .Select(instance => instance.Provider)
            .OfType<WorkloadProviderId>()
            .Distinct()
            .ToList();
        return bound.Count switch
        {
            0 => effectiveProvider,
            1 => bound[0],
            _ => null,
        };
    }

    private void LogIgnoredRestoreBindings(
        WorkloadManifest candidateWorkload,
        Guid instanceId,
        IReadOnlyDictionary<string, string?> storedParams,
        IReadOnlyDictionary<string, string?>? submittedParameters,
        IReadOnlySet<string> ambiguousExistingClaimKeys)
    {
        if (submittedParameters is null || submittedParameters.Count == 0)
        {
            return;
        }

        var ignored = WorkloadDeploymentParameterScope
            .ForWorkload(candidateWorkload, submittedParameters, ambiguousExistingClaimKeys)
            .Where(kv => kv.Key.StartsWith("existingClaim.", StringComparison.Ordinal)
                && !string.Equals(
                    BlankAsNull(kv.Value), BlankAsNull(storedParams.GetValueOrDefault(kv.Key)), StringComparison.Ordinal))
            .Select(kv => kv.Key)
            .OrderBy(key => key, StringComparer.Ordinal)
            .ToList();
        if (ignored.Count > 0)
        {
            logger.LogWarning(
                "Update of workload '{WorkloadId}' (instance {InstanceId}) ignores the submitted restore binding(s) {Keys}: a restore is install-time only, so the stored binding is kept.",
                candidateWorkload.Id, instanceId, string.Join(", ", ignored));
        }
    }

    private static string? BlankAsNull(string? value) => string.IsNullOrWhiteSpace(value) ? null : value;

    private static DeployableParameterBuilder.Result ResolveAdoptedCandidate(
        WorkloadManifest candidateWorkload,
        IReadOnlyDictionary<string, string?> candidateOperatorParams,
        DeployableParameterBuilder.Result submittedBuild,
        WorkloadProviderDescriptor provider)
    {
        var values = WorkloadParameterResolver.Resolve(candidateWorkload, candidateOperatorParams, provider)
            .ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.Ordinal);
        foreach (var name in WorkloadPasswordNames.For(candidateWorkload))
            if (submittedBuild.Values.TryGetValue(name, out var value)) values[name] = value;
        return submittedBuild with { Stored = candidateOperatorParams, Values = values };
    }

    internal static Guid AddedWorkloadInstanceId(Guid intentId, string workloadId)
    {
        var name = Encoding.UTF8.GetBytes(workloadId);
        var input = new byte[16 + name.Length];
        intentId.TryWriteBytes(input, bigEndian: true, out _);
        name.CopyTo(input, 16);

        var hash = SHA256.HashData(input);
        hash[6] = (byte)((hash[6] & 0x0F) | 0x80);
        hash[8] = (byte)((hash[8] & 0x3F) | 0x80);
        return new Guid(hash.AsSpan(0, 16), bigEndian: true);
    }

    private static IReadOnlyDictionary<string, string?> DropAdoptedResourceAxes(
        IReadOnlyDictionary<string, string?> operatorParams,
        WorkloadManifest? previousWorkload,
        WorkloadManifest candidateWorkload)
    {
        var moved = WorkloadDeploymentDiff.MovedResourceAxisParameters(previousWorkload, candidateWorkload);
        if (moved.Count == 0)
        {
            return operatorParams;
        }

        var trimmed = new Dictionary<string, string?>(operatorParams, StringComparer.Ordinal);
        foreach (var axis in moved)
        {
            trimmed.Remove(axis);
        }

        return trimmed;
    }

    private async Task<WorkloadBootstrapContext> BootstrapAsync(
        string groupId,
        WorkloadManifest workload,
        Guid instanceId,
        WorkloadProviderId provider,
        string? appVersion,
        IWorkloadResourceNamer namer,
        CancellationToken cancellationToken)
    {
        var request = WorkloadHostBootstrapStep.Request(
            instanceId, groupId, provider, namer, workload, WorkloadBootstrapAudience.Runtime);
        var composed = await WorkloadHostBootstrapStep.ComposeAsync(hostBootstrap, request, workload, cancellationToken)
            .ConfigureAwait(false);
        if (composed.Refusal is { } refusal)
        {
            throw new WorkloadHostRefusedException(refusal);
        }

        return WorkloadHostBootstrapStep.Context(request, composed) with { AppVersion = appVersion };
    }

    private static WorkloadManifest CurrentDeclaration(WorkloadManifest? previousWorkload, WorkloadManifest candidateWorkload)
    {
        return previousWorkload ?? candidateWorkload;
    }

    private static DeploymentManifest FallbackPrevious(DeploymentManifest candidate, bool refused)
        => refused
            ? candidate with
            {
                Workloads = candidate.Workloads
                    .Select(workload => workload with { Extensions = WorkloadExtensions.None })
                    .ToList(),
            }
            : candidate;
}
