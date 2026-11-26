using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.DataProtection;
using Reeve.Workloads.Abstractions;
using Reeve.Workloads.Abstractions.Domain.Entities;

namespace Reeve.Workloads.Core;

internal sealed class UnpinnedReferenceRolloutGuard(IDataProtectionProvider protectionProvider)
{
    private readonly IDataProtectionProvider _protectionProvider = protectionProvider;
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    internal sealed record CheckResult(string? ChangedParameter, string? PendingState);
    internal sealed record PendingReference(string ParameterName, string? Reference, WorkloadReferenceRolloutState State);

    public CheckResult Check(WorkloadInstance instance, IReadOnlyDictionary<string, string?> references,
        IReadOnlyDictionary<string, string?> values, IEnumerable<string> passwordNames)
    {
        var pending = GetPending(instance, references, values, passwordNames);
        return pending.Count == 0
            ? new(null, null)
            : new(pending[0].ParameterName,
                pending[0].State == WorkloadReferenceRolloutState.ApprovalStateUnavailable ? "unreadable-key" : null);
    }

    public IReadOnlyList<PendingReference> GetPending(WorkloadInstance instance,
        IReadOnlyDictionary<string, string?> references, IReadOnlyDictionary<string, string?> values,
        IEnumerable<string> passwordNames)
    {
        var passwordSet = passwordNames.ToHashSet(StringComparer.Ordinal);
        var eligibleNames = references.Where(pair => pair.Value is not null && passwordSet.Contains(pair.Key))
            .Select(pair => pair.Key).ToHashSet(StringComparer.Ordinal);
        if (!ReferenceHashPinRules.RolloutKeyRequired(references, passwordSet)) return [];

        var state = Read(instance.ReferenceRolloutState);
        if (instance.ReferenceRolloutState is not null && state is null)
            return Pending(eligibleNames, WorkloadReferenceRolloutState.ApprovalStateUnavailable);

        Dictionary<string, string> current;
        try { current = Fingerprints(instance.Id, references, values, passwordSet, state); }
        catch (CryptographicException)
        {
            return Pending(eligibleNames, WorkloadReferenceRolloutState.ApprovalStateUnavailable);
        }
        if (state is null)
            return instance.AppliedSpecHash is not null && current.Count > 0
                ? Pending(current.Keys.ToHashSet(StringComparer.Ordinal), WorkloadReferenceRolloutState.AwaitingApproval)
                : [];

        var currentBindings = Bindings(instance.Id, references, passwordSet, state.AppliedBindings.Keys, state);
        var pending = new List<string>();
        var seenPending = new HashSet<string>(StringComparer.Ordinal);
        foreach (var name in currentBindings.Keys.Union(state.AppliedBindings.Keys, StringComparer.Ordinal))
        {
            var currentBinding = currentBindings.GetValueOrDefault(name);
            var appliedBinding = state.AppliedBindings.GetValueOrDefault(name);
            var changedBinding = !string.Equals(currentBinding, appliedBinding, StringComparison.Ordinal);
            var hasBindingApproval = state.ApprovedBindings.TryGetValue(name, out var approvedBinding)
                && string.Equals(approvedBinding, currentBinding, StringComparison.Ordinal);
            if (changedBinding && !hasBindingApproval) AddPending(name);

            if (current.TryGetValue(name, out var currentFingerprint)
                && state.Applied.TryGetValue(name, out var appliedFingerprint)
                && !FixedEquals(appliedFingerprint, currentFingerprint)
                && (!state.Approved.TryGetValue(name, out var approvedFingerprint)
                    || !FixedEquals(approvedFingerprint, currentFingerprint)))
                AddPending(name);
        }
        return pending.Select(name => new PendingReference(name, references.GetValueOrDefault(name),
            WorkloadReferenceRolloutState.AwaitingApproval)).ToArray();

        IReadOnlyList<PendingReference> Pending(IEnumerable<string> names, WorkloadReferenceRolloutState status)
            => names.OrderBy(name => name, StringComparer.Ordinal)
                .Select(name => new PendingReference(name, references.GetValueOrDefault(name), status)).ToArray();
        void AddPending(string name) { if (seenPending.Add(name)) pending.Add(name); }
    }

    public void RecordApplied(WorkloadInstance instance, IReadOnlyDictionary<string, string?> references,
        IReadOnlyDictionary<string, string?> values, IEnumerable<string> passwordNames, DateTimeOffset nowUtc)
    {
        var names = passwordNames.ToHashSet(StringComparer.Ordinal);
        var unpinned = references.Where(pair => pair.Value is not null && names.Contains(pair.Key))
            .ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.Ordinal);
        var hasUnpinned = ReferenceHashPinRules.RolloutKeyRequired(references, names);
        if (!hasUnpinned)
        {
            if (instance.ReferenceRolloutState is null) return;
            var keyFreeState = Read(instance.ReferenceRolloutState) ?? NewState(instance.Id, createKey: false);
            keyFreeState.ProtectedKey = null;
            var storedWithoutReferences = instance.GetResolvedParameters();
            foreach (var (name, marker) in keyFreeState.HashPinnedBindings.ToArray())
            {
                storedWithoutReferences.TryGetValue(name, out var storedBinding);
                if (ReferenceHashPinRules.Evaluate(marker, storedBinding, storedBinding)
                    == ReferenceHashPinRules.Applicability.Invalid)
                    keyFreeState.HashPinnedBindings.Remove(name);
            }
            keyFreeState.Applied.Clear();
            keyFreeState.AppliedBindings.Clear();
            keyFreeState.Approved.Clear();
            keyFreeState.ApprovedBindings.Clear();
            instance.SetReferenceRolloutState(JsonSerializer.Serialize(keyFreeState, JsonOptions), nowUtc);
            return;
        }
        var state = Read(instance.ReferenceRolloutState) ?? NewState(instance.Id);
        var stored = instance.GetResolvedParameters();
        foreach (var (name, marker) in state.HashPinnedBindings.ToArray())
        {
            stored.TryGetValue(name, out var storedBinding);
            var remainsCurrent = ReferenceHashPinRules.Evaluate(marker, storedBinding, storedBinding)
                != ReferenceHashPinRules.Applicability.Invalid;
            var appliedDifferentBinding = unpinned.TryGetValue(name, out var appliedBinding)
                && ReferenceHashPinRules.Evaluate(marker, storedBinding, appliedBinding)
                    == ReferenceHashPinRules.Applicability.Invalid;
            if (!remainsCurrent || appliedDifferentBinding) state.HashPinnedBindings.Remove(name);
        }
        var previousApplied = state.Applied;
        var previousBindings = state.AppliedBindings;
        state.Applied = Fingerprints(instance.Id, unpinned, values, names, state);
        state.AppliedBindings = Bindings(instance.Id, unpinned, names, [], state);
        foreach (var (name, marker) in state.HashPinnedBindings)
        {
            if (ReferenceHashPinRules.Evaluate(marker, stored.GetValueOrDefault(name), stored.GetValueOrDefault(name))
                != ReferenceHashPinRules.Applicability.Unpinned) continue;
            if (previousApplied.TryGetValue(name, out var digest)) state.Applied[name] = digest;
            if (previousBindings.TryGetValue(name, out var binding)) state.AppliedBindings[name] = binding;
        }
        state.Approved.Clear();
        state.ApprovedBindings.Clear();
        instance.SetReferenceRolloutState(JsonSerializer.Serialize(state, JsonOptions), nowUtc);
    }

    public IReadOnlyDictionary<string, string?> ReferencesForHash(WorkloadInstance instance,
        IReadOnlyDictionary<string, string?> references)
        => ProjectReferencesForHash(instance, references);

    internal static IReadOnlyDictionary<string, string?> ProjectReferencesForHash(WorkloadInstance instance,
        IReadOnlyDictionary<string, string?> references)
        => instance.ParameterReferencesForSpecHash(references);

    public void PinHashToLiteral(WorkloadInstance instance, string parameterName, string reference,
        string? resolvedValue, DateTimeOffset nowUtc, bool isVersionPinned = false)
    {
        var state = Read(instance.ReferenceRolloutState);
        if (instance.ReferenceRolloutState is not null && state is null)
            throw new CryptographicException("The stored reference rollout state is unreadable.");
        state ??= isVersionPinned ? NewState(instance.Id, createKey: false) : NewState(instance.Id);
        var refs = new Dictionary<string, string?> { [parameterName] = reference };
        var values = new Dictionary<string, string?> { [parameterName] = resolvedValue };
        var needsKey = ReferenceHashPinRules.RolloutKeyRequired(refs, isVersionPinned ? [] : [parameterName]);
        var digest = needsKey ? Fingerprints(instance.Id, refs, values, [parameterName], state).GetValueOrDefault(parameterName) : null;
        if (needsKey && digest is null) throw new CryptographicException("The password binding could not be fingerprinted.");
        var bindingDigest = needsKey ? Bindings(instance.Id, refs, [parameterName], [], state)[parameterName] : null;
        state.HashPinnedBindings[parameterName] = new ReferenceHashPin(reference, isVersionPinned);
        if (!isVersionPinned)
        {
            state.AppliedBindings[parameterName] = bindingDigest!;
            state.Applied[parameterName] = digest!;
        }
        else
        {
            state.AppliedBindings.Remove(parameterName);
            state.Applied.Remove(parameterName);
            state.ApprovedBindings.Remove(parameterName);
            state.Approved.Remove(parameterName);
        }
        var json = JsonSerializer.Serialize(state, JsonOptions);
        instance.SetReferenceRolloutState(json, nowUtc);
    }

    public async Task ApproveAsync(WorkloadInstance instance, IReadOnlyDictionary<string, string?> references,
        IReadOnlyDictionary<string, string?> values, IEnumerable<string> passwordNames,
        IWorkloadInstanceRepository repository, IWorkloadClock clock, CancellationToken cancellationToken)
    {
        ApproveValues(instance, references, values, passwordNames, clock.UtcNow);
        instance.SetAppliedSpecHash(null, clock.UtcNow);
        await repository.UpdateAsync(instance, cancellationToken).ConfigureAwait(false);
    }

    public void ApproveValues(WorkloadInstance instance, IReadOnlyDictionary<string, string?> references,
        IReadOnlyDictionary<string, string?> values, IEnumerable<string> passwordNames, DateTimeOffset nowUtc)
    {
        var names = passwordNames.ToHashSet(StringComparer.Ordinal);
        if (!ReferenceHashPinRules.RolloutKeyRequired(references, names)) return;
        var state = Read(instance.ReferenceRolloutState) ?? NewState(instance.Id);
        try
        {
            _ = GetOrCreateKey(instance.Id, state);
        }
        catch (CryptographicException)
        {
            state = NewState(instance.Id);
        }
        state.Approved = Fingerprints(instance.Id, references, values, names, state);
        state.ApprovedBindings = Bindings(instance.Id, references, names, state.AppliedBindings.Keys, state);
        foreach (var name in references.Where(pair => pair.Value is not null && names.Contains(pair.Key)).Select(pair => pair.Key))
        {
            state.HashPinnedBindings.TryGetValue(name, out var marker);
            var storedBinding = instance.GetResolvedParameters().GetValueOrDefault(name);
            if (ReferenceHashPinRules.Evaluate(marker, storedBinding, references.GetValueOrDefault(name))
                != ReferenceHashPinRules.Applicability.Invalid)
                state.HashPinnedBindings.Remove(name);
        }
        instance.SetReferenceRolloutState(JsonSerializer.Serialize(state, JsonOptions), nowUtc);
    }

    private Dictionary<string, string> Fingerprints(Guid id, IReadOnlyDictionary<string, string?> references,
        IReadOnlyDictionary<string, string?> values, IEnumerable<string> passwordNames, State? state)
    {
        var passwordSet = passwordNames as ISet<string> ?? passwordNames.ToHashSet(StringComparer.Ordinal);
        if (!ReferenceHashPinRules.RolloutKeyRequired(references, passwordSet)) return new(StringComparer.Ordinal);
        var key = state is null ? null : UnprotectKey(id, state.ProtectedKey);
        if (key is null)
        {
            key = RandomNumberGenerator.GetBytes(32);
            state ??= NewState(id);
            state.ProtectedKey = _protectionProvider.CreateProtector("Reeve.Workloads.ReferenceRolloutKey", id.ToString("N"))
                .Protect(Convert.ToBase64String(key));
        }
        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var (name, reference) in references)
        {
            if (reference is null || !passwordSet.Contains(name)
                || !values.TryGetValue(name, out var value) || value is null) continue;
            result[name] = Convert.ToBase64String(HMACSHA256.HashData(key, Encoding.UTF8.GetBytes(value)));
        }
        return result;
    }

    private static State? Read(string? json)
    {
        if (string.IsNullOrEmpty(json)) return null;
        try { return JsonSerializer.Deserialize<State>(json, JsonOptions); }
        catch (JsonException) { return null; }
    }

    private byte[]? UnprotectKey(Guid id, string? protectedKey)
    {
        if (protectedKey is null) return null;
        var text = _protectionProvider.CreateProtector("Reeve.Workloads.ReferenceRolloutKey", id.ToString("N")).Unprotect(protectedKey);
        return Convert.FromBase64String(text);
    }

    private State NewState(Guid id, bool createKey = true)
    {
        var state = new State
        {
            Applied = new(StringComparer.Ordinal), Approved = new(StringComparer.Ordinal),
        };
        if (createKey)
        {
            var key = RandomNumberGenerator.GetBytes(32);
            state.ProtectedKey = _protectionProvider.CreateProtector("Reeve.Workloads.ReferenceRolloutKey", id.ToString("N"))
                .Protect(Convert.ToBase64String(key));
        }
        return state;
    }

    private static bool FixedEquals(string left, string right)
    {
        try { return CryptographicOperations.FixedTimeEquals(Convert.FromBase64String(left), Convert.FromBase64String(right)); }
        catch (FormatException) { return false; }
    }

    private Dictionary<string, string?> Bindings(Guid id, IReadOnlyDictionary<string, string?> references,
        IEnumerable<string> currentNames, IEnumerable<string> previousNames, State state)
    {
        var key = GetOrCreateKey(id, state);
        var result = currentNames.Union(previousNames, StringComparer.Ordinal)
            .Distinct(StringComparer.Ordinal)
            .ToDictionary(name => name, _ => (string?)null, StringComparer.Ordinal);
        foreach (var (name, reference) in references)
            result[name] = reference is null
                ? null
                : Convert.ToBase64String(HMACSHA256.HashData(key, Encoding.UTF8.GetBytes("binding\0" + reference)));
        return result;
    }

    private byte[] GetOrCreateKey(Guid id, State state)
    {
        var key = UnprotectKey(id, state.ProtectedKey);
        if (key is not null) return key;
        key = RandomNumberGenerator.GetBytes(32);
        state.ProtectedKey = _protectionProvider.CreateProtector("Reeve.Workloads.ReferenceRolloutKey", id.ToString("N"))
            .Protect(Convert.ToBase64String(key));
        return key;
    }

    private sealed class State
    {
        public State() { }
        public string? ProtectedKey { get; set; }
        public Dictionary<string, string> Applied { get; set; } = new(StringComparer.Ordinal);
        public Dictionary<string, string> Approved { get; set; } = new(StringComparer.Ordinal);
        public Dictionary<string, string?> AppliedBindings { get; set; } = new(StringComparer.Ordinal);
        public Dictionary<string, string?> ApprovedBindings { get; set; } = new(StringComparer.Ordinal);
        [System.Text.Json.Serialization.JsonPropertyName(ReferenceHashPinRules.HashPinnedBindingsJsonName)]
        public Dictionary<string, ReferenceHashPin> HashPinnedBindings { get; set; } = new(StringComparer.Ordinal);
    }
}
