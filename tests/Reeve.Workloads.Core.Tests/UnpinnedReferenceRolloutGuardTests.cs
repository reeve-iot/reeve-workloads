using FluentAssertions;
using Microsoft.AspNetCore.DataProtection;
using System.Text.Json;
using Reeve.Workloads.Abstractions.Domain.Entities;
using Reeve.Workloads.Core;

namespace Reeve.Workloads.Core.Tests;

public sealed class UnpinnedReferenceRolloutGuardTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 4, 0, 0, 0, TimeSpan.Zero);
    private static readonly string[] PasswordNames = ["networkKey"];
    private static readonly IReadOnlyDictionary<string, string?> Reference =
        new Dictionary<string, string?> { ["networkKey"] = "ref:vault:zwave#networkKey" };

    [Fact]
    public void Initial_successful_apply_stamps_hmac_and_same_value_is_allowed()
    {
        var guard = Guard();
        var instance = Instance();
        guard.RecordApplied(instance, Reference, Values("one"), PasswordNames, Now);

        guard.Check(instance, Reference, Values("one"), PasswordNames).ChangedParameter.Should().BeNull();
        instance.ReferenceRolloutState.Should().NotContain("one");
    }

    [Fact]
    public void Changed_unpinned_value_holds_until_exact_value_is_approved_and_rejects_a_later_change()
    {
        var guard = Guard();
        var instance = Instance();
        guard.RecordApplied(instance, Reference, Values("one"), PasswordNames, Now);

        guard.Check(instance, Reference, Values("two"), PasswordNames).ChangedParameter.Should().Be("networkKey");
        guard.ApproveValues(instance, Reference, Values("two"), PasswordNames, Now.AddMinutes(1));
        guard.Check(instance, Reference, Values("two"), PasswordNames).ChangedParameter.Should().BeNull();
        guard.Check(instance, Reference, Values("three"), PasswordNames).ChangedParameter.Should().Be("networkKey");
    }

    [Fact]
    public void Pending_read_lists_changed_reference_then_clears_after_approval_without_exposing_value_or_fingerprint()
    {
        var guard = Guard();
        var instance = Instance();
        guard.RecordApplied(instance, Reference, Values("one"), PasswordNames, Now);

        var pending = guard.GetPending(instance, Reference, Values("two"), PasswordNames);
        pending.Should().ContainSingle(item => item.ParameterName == "networkKey"
            && item.Reference == Reference["networkKey"]
            && item.State == Reeve.Workloads.Abstractions.WorkloadReferenceRolloutState.AwaitingApproval);
        pending.Select(item => item.Reference).Should().NotContain("two");
        pending.Select(item => item.Reference).Should().NotContain("one");

        guard.ApproveValues(instance, Reference, Values("two"), PasswordNames, Now.AddMinutes(1));
        guard.GetPending(instance, Reference, Values("two"), PasswordNames).Should().BeEmpty();
        guard.GetPending(instance, new Dictionary<string, string?>(), Values("two"), PasswordNames).Should().BeEmpty();
    }

    [Fact]
    public void Removing_one_of_two_references_still_holds_that_binding_and_read_agrees_with_check()
    {
        var guard = Guard();
        var instance = Instance();
        var both = new Dictionary<string, string?> { ["alpha"] = "ref:vault:a", ["beta"] = "ref:vault:b" };
        var values = new Dictionary<string, string?> { ["alpha"] = "one", ["beta"] = "two" };
        var names = new[] { "alpha", "beta" };
        guard.RecordApplied(instance, both, values, names, Now);
        var remaining = new Dictionary<string, string?> { ["alpha"] = "ref:vault:a" };
        var remainingValues = new Dictionary<string, string?> { ["alpha"] = "one" };

        var pending = guard.GetPending(instance, remaining, remainingValues, names);
        pending.Should().ContainSingle(item => item.ParameterName == "beta" && item.Reference == null
            && item.State == Reeve.Workloads.Abstractions.WorkloadReferenceRolloutState.AwaitingApproval);
        guard.Check(instance, remaining, remainingValues, names).ChangedParameter.Should().Be("beta");
    }

    [Fact]
    public void Legacy_applied_row_requires_approval_for_unpinned_reference()
    {
        var instance = Instance();
        instance.SetAppliedSpecHash("legacy-applied-hash", Now);
        var pending = Guard().GetPending(instance, Reference, Values("one"), PasswordNames);

        pending.Should().ContainSingle(item => item.ParameterName == "networkKey"
            && item.State == Reeve.Workloads.Abstractions.WorkloadReferenceRolloutState.AwaitingApproval);
    }

    [Fact]
    public void Check_keeps_main_iteration_order_when_multiple_parameters_are_pending()
    {
        var guard = Guard();
        var instance = Instance();
        var references = new Dictionary<string, string?> { ["zeta"] = "ref:vault:z", ["alpha"] = "ref:vault:a" };
        var names = new[] { "zeta", "alpha" };
        var originalValues = new Dictionary<string, string?> { ["zeta"] = "z-one", ["alpha"] = "a-one" };
        guard.RecordApplied(instance, references, originalValues, names, Now);
        var changedValues = new Dictionary<string, string?> { ["zeta"] = "z-two", ["alpha"] = "a-two" };

        guard.Check(instance, references, changedValues, names).ChangedParameter.Should().Be("zeta");
        guard.GetPending(instance, references, changedValues, names).First().ParameterName.Should().Be("zeta");
    }

    [Fact]
    public void Pinned_reference_is_not_fingerprinted_or_held()
    {
        var guard = Guard();
        var instance = Instance();
        guard.RecordApplied(instance, new Dictionary<string, string?>(), Values("one"), PasswordNames, Now);

        guard.Check(instance, new Dictionary<string, string?>(), Values("two"), PasswordNames).ChangedParameter.Should().BeNull();
    }

    [Fact]
    public void Replacing_an_unpinned_reference_requires_approval_even_when_the_resolved_value_is_same()
    {
        var guard = Guard();
        var instance = Instance();
        guard.RecordApplied(instance, Reference, Values("one"), PasswordNames, Now);
        var changedReference = new Dictionary<string, string?> { ["networkKey"] = "ref:vault:zwave#replacement" };

        guard.Check(instance, changedReference, Values("one"), PasswordNames).ChangedParameter.Should().Be("networkKey");
        guard.ApproveValues(instance, changedReference, Values("one"), PasswordNames, Now.AddMinutes(1));
        guard.Check(instance, changedReference, Values("one"), PasswordNames).ChangedParameter.Should().BeNull();
    }

    [Fact]
    public void Hash_pinned_binding_omits_reference_from_hash_until_approval_then_restores_it()
    {
        var guard = Guard();
        var instance = Instance();
        instance.SetResolvedParameters(Reference, Now);
        guard.PinHashToLiteral(instance, "networkKey", Reference["networkKey"]!, "same-literal", Now);

        guard.ReferencesForHash(instance, Reference).Should().BeEmpty();
        guard.GetPending(instance, Reference, Values("same-literal"), PasswordNames).Should().BeEmpty();

        guard.ApproveValues(instance, Reference, Values("rotated"), PasswordNames, Now.AddMinutes(1));
        guard.ReferencesForHash(instance, Reference).Should().BeEquivalentTo(Reference);
        guard.GetPending(instance, Reference, Values("rotated"), PasswordNames).Should().BeEmpty();
    }

    [Fact]
    public void Hash_pin_applies_only_to_the_exact_binding_and_editing_to_literal_clears_it()
    {
        var guard = Guard();
        var instance = Instance();
        var original = Reference["networkKey"]!;
        instance.SetResolvedParameters(Reference, Now);
        guard.PinHashToLiteral(instance, "networkKey", original, "same-literal", Now);

        var changed = new Dictionary<string, string?> { ["networkKey"] = "ref:vault:zwave#replacement" };
        guard.ReferencesForHash(instance, changed).Should().BeEquivalentTo(changed,
            "the marker stands for one exact binding, not every future value of this parameter name");

        instance.SetResolvedParameters(Values("same-literal"), Now.AddMinutes(1));
        guard.RecordApplied(instance, new Dictionary<string, string?>(), Values("same-literal"), PasswordNames, Now.AddMinutes(1));
        using (var document = JsonDocument.Parse(instance.ReferenceRolloutState!))
            document.RootElement.GetProperty("hashPinnedBindings").TryGetProperty("networkKey", out _).Should().BeFalse();
        guard.ReferencesForHash(instance, changed).Should().BeEquivalentTo(changed,
            "editing back to a literal ends the marker before a later reference is stored");
    }

    [Fact]
    public void Rebinding_after_literal_edit_uses_reference_hash_after_record_applied_removed_marker()
    {
        var guard = Guard();
        var instance = Instance();
        var binding = Reference["networkKey"]!;
        instance.SetResolvedParameters(Reference, Now);
        guard.PinHashToLiteral(instance, "networkKey", binding, "literal", Now);
        instance.SetResolvedParameters(Values("literal"), Now.AddSeconds(1));
        guard.RecordApplied(instance, new Dictionary<string, string?>(), Values("literal"), PasswordNames, Now.AddSeconds(1));
        instance.SetResolvedParameters(Reference, Now.AddSeconds(2));

        guard.ReferencesForHash(instance, Reference).Should().BeEquivalentTo(Reference);
    }

    [Fact]
    public void Record_applied_with_a_new_binding_clears_the_old_marker_before_checking_pending()
    {
        var guard = Guard();
        var instance = Instance();
        const string x = "ref:vault:x";
        const string y = "ref:vault:y";
        instance.SetResolvedParameters(new Dictionary<string, string?> { ["networkKey"] = x }, Now);
        guard.PinHashToLiteral(instance, "networkKey", x, "literal", Now);

        guard.RecordApplied(instance, new Dictionary<string, string?> { ["networkKey"] = y },
            new Dictionary<string, string?> { ["networkKey"] = "Y" }, PasswordNames, Now.AddSeconds(1));
        instance.SetResolvedParameters(new Dictionary<string, string?> { ["networkKey"] = y }, Now.AddSeconds(2));

        guard.Check(instance, new Dictionary<string, string?> { ["networkKey"] = y },
            new Dictionary<string, string?> { ["networkKey"] = "Y" }, PasswordNames).ChangedParameter.Should().BeNull();
    }

    [Fact]
    public void Approval_for_a_different_binding_keeps_the_pinned_hash_marker()
    {
        var guard = Guard();
        var instance = Instance();
        const string x = "ref:vault:x";
        instance.SetResolvedParameters(new Dictionary<string, string?> { ["networkKey"] = x }, Now);
        guard.PinHashToLiteral(instance, "networkKey", x, "literal", Now);

        guard.ApproveValues(instance, new Dictionary<string, string?> { ["networkKey"] = "ref:vault:y" },
            Values("Y"), PasswordNames, Now.AddSeconds(1));

        guard.ReferencesForHash(instance, new Dictionary<string, string?> { ["networkKey"] = x }).Should().BeEmpty();
    }

    [Fact]
    public void Literal_only_approval_after_key_loss_preserves_pinned_marker()
    {
        var original = Guard();
        var instance = Instance();
        const string pinned = "ref:vault:immutable#key@3";
        instance.SetResolvedParameters(new Dictionary<string, string?> { ["networkKey"] = pinned }, Now);
        original.RecordApplied(instance: instance,
            references: new Dictionary<string, string?> { ["otherPassword"] = "ref:vault:other" },
            values: new Dictionary<string, string?> { ["otherPassword"] = "other-value" },
            passwordNames: ["otherPassword"], nowUtc: Now);
        original.PinHashToLiteral(instance, "networkKey", pinned, "literal", Now, isVersionPinned: true);
        var anotherKeyRing = new UnpinnedReferenceRolloutGuard(new EphemeralDataProtectionProvider());

        anotherKeyRing.ApproveValues(instance, new Dictionary<string, string?> { ["networkKey"] = null },
            Values("literal"), ["networkKey", "otherPassword"], Now.AddSeconds(1));

        anotherKeyRing.ReferencesForHash(instance, new Dictionary<string, string?> { ["networkKey"] = pinned }).Should().BeEmpty();
    }

    [Fact]
    public void Key_free_apply_clears_lost_key_so_later_unpinned_rebind_can_start_a_new_rollout()
    {
        var instance = Instance();
        var original = Guard();
        original.RecordApplied(instance, Reference, Values("old"), PasswordNames, Now);
        instance.SetResolvedParameters(new Dictionary<string, string?> { ["networkKey"] = "literal" }, Now.AddSeconds(1));
        var recovered = Guard();
        recovered.RecordApplied(instance, new Dictionary<string, string?>(), Values("literal"), PasswordNames, Now.AddSeconds(2));
        instance.SetResolvedParameters(Reference, Now.AddSeconds(3));

        var act = () => recovered.PinHashToLiteral(instance, "networkKey", Reference["networkKey"]!, "literal", Now.AddSeconds(4));
        act.Should().NotThrow();
        recovered.Check(instance, Reference, Values("literal"), PasswordNames).ChangedParameter.Should().BeNull();
    }

    [Fact]
    public void Version_pinned_marker_does_not_hide_a_different_unpinned_candidate_from_pending()
    {
        var guard = Guard();
        var instance = Instance();
        const string pinned = "ref:vault:immutable#key@3";
        instance.SetResolvedParameters(new Dictionary<string, string?> { ["networkKey"] = pinned }, Now);
        guard.PinHashToLiteral(instance, "networkKey", pinned, "same-literal", Now, isVersionPinned: true);

        var replacement = new Dictionary<string, string?> { ["networkKey"] = "ref:vault:rotating#key" };
        guard.Check(instance, replacement, Values("new-value"), PasswordNames).ChangedParameter.Should().Be("networkKey");
        guard.GetPending(instance, replacement, Values("new-value"), PasswordNames)
            .Should().ContainSingle(item => item.ParameterName == "networkKey");
    }

    [Fact]
    public void Record_applied_with_no_unpinned_reference_survives_lost_key_ring()
    {
        var instance = Instance();
        var references = new Dictionary<string, string?> { ["networkKey"] = Reference["networkKey"] };
        Guard().RecordApplied(instance, references, Values("one"), PasswordNames, Now);
        instance.SetResolvedParameters(Values("literal"), Now.AddMinutes(1));
        var lostKeyRing = Guard();

        lostKeyRing.Check(instance, new Dictionary<string, string?>(), Values("literal"), PasswordNames)
            .ChangedParameter.Should().BeNull();
        var act = () => lostKeyRing.RecordApplied(instance, new Dictionary<string, string?>(), Values("literal"), PasswordNames, Now.AddMinutes(2));
        act.Should().NotThrow();
    }

    [Fact]
    public void Version_pinned_rebind_next_to_unpinned_password_does_not_create_pending_item()
    {
        var guard = Guard();
        var instance = Instance();
        const string pinned = "ref:vault:immutable#key@3";
        const string ordinary = "ref:vault:rotating#key";
        instance.SetResolvedParameters(new Dictionary<string, string?> { ["pinnedPassword"] = pinned }, Now);
        var baseline = new Dictionary<string, string?> { ["rotatingPassword"] = ordinary };
        var baselineValues = new Dictionary<string, string?> { ["rotatingPassword"] = "rotating-value" };
        guard.RecordApplied(instance, baseline, baselineValues, ["rotatingPassword"], Now);
        guard.PinHashToLiteral(instance, "pinnedPassword", pinned, "pinned-value", Now.AddSeconds(1), isVersionPinned: true);
        var references = new Dictionary<string, string?> { ["pinnedPassword"] = null, ["rotatingPassword"] = ordinary };
        var values = new Dictionary<string, string?> { ["pinnedPassword"] = "pinned-value", ["rotatingPassword"] = "rotating-value" };

        guard.GetPending(instance, references, values, ["rotatingPassword"]).Should().BeEmpty();
    }

    [Fact]
    public void Restart_preserves_the_exact_binding_marker_and_its_hash_projection()
    {
        var guard = Guard();
        var instance = Instance();
        instance.SetResolvedParameters(Reference, Now);
        guard.PinHashToLiteral(instance, "networkKey", Reference["networkKey"]!, "same-literal", Now);
        var restarted = WorkloadInstance.Create(instance.Id, instance.GroupInstanceId, instance.GroupId, instance.WorkloadId, Now);
        restarted.SetResolvedParameters(Reference, Now);
        restarted.SetReferenceRolloutState(instance.ReferenceRolloutState, Now);

        guard.ReferencesForHash(restarted, Reference).Should().BeEmpty();
        guard.GetPending(restarted, Reference, Values("same-literal"), PasswordNames).Should().BeEmpty();
    }

    [Fact]
    public void Editing_to_literal_then_rebinding_replaces_the_old_marker_without_storing_plaintext()
    {
        var guard = Guard();
        var instance = Instance();
        var first = Reference["networkKey"]!;
        const string second = "ref:vault:zwave#replacement";
        instance.SetResolvedParameters(Reference, Now);
        guard.PinHashToLiteral(instance, "networkKey", first, "first-secret", Now);

        instance.SetResolvedParameters(new Dictionary<string, string?> { ["networkKey"] = "second-secret" }, Now.AddSeconds(1));
        guard.ReferencesForHash(instance, Reference).Should().BeEquivalentTo(Reference);
        instance.SetResolvedParameters(new Dictionary<string, string?> { ["networkKey"] = second }, Now.AddSeconds(2));
        guard.PinHashToLiteral(instance, "networkKey", second, "second-secret", Now.AddSeconds(2));

        guard.ReferencesForHash(instance, new Dictionary<string, string?> { ["networkKey"] = second }).Should().BeEmpty();
        instance.ReferenceRolloutState.Should().NotContain("first-secret").And.NotContain("second-secret");
    }

    [Fact]
    public void Removing_the_last_unpinned_reference_does_not_require_approval()
    {
        var guard = Guard();
        var instance = Instance();
        guard.RecordApplied(instance, Reference, Values("one"), PasswordNames, Now);
        var empty = new Dictionary<string, string?>();

        guard.Check(instance, empty, empty, PasswordNames).ChangedParameter.Should().BeNull();
    }

    [Fact]
    public void Lost_key_ring_does_not_hold_literal_only_instances()
    {
        var instance = Instance();
        Guard().RecordApplied(instance, new Dictionary<string, string?>(), Values("literal"), PasswordNames, Now);

        var otherKeyRing = new UnpinnedReferenceRolloutGuard(new EphemeralDataProtectionProvider());
        otherKeyRing.Check(instance, new Dictionary<string, string?>(), Values("literal"), PasswordNames)
            .ChangedParameter.Should().BeNull();
        otherKeyRing.ApproveValues(instance, new Dictionary<string, string?>(), Values("literal"), PasswordNames, Now.AddMinutes(1));
    }

    [Fact]
    public void Lost_key_ring_holds_only_unpinned_refs_and_recovered_key_allows_approval()
    {
        var instance = Instance();
        var original = Guard();
        original.RecordApplied(instance, Reference, Values("one"), PasswordNames, Now);
        var otherKeyRing = new UnpinnedReferenceRolloutGuard(new EphemeralDataProtectionProvider());

        otherKeyRing.Check(instance, Reference, Values("two"), PasswordNames).ChangedParameter.Should().Be("networkKey");
        original.ApproveValues(instance, Reference, Values("two"), PasswordNames, Now.AddMinutes(1));
        original.Check(instance, Reference, Values("two"), PasswordNames).ChangedParameter.Should().BeNull();
    }

    private static UnpinnedReferenceRolloutGuard Guard() => new(new EphemeralDataProtectionProvider());
    private static WorkloadInstance Instance() => WorkloadInstance.Create(Guid.NewGuid(), Guid.NewGuid(), "test.plugin", "workload", Now);
    private static IReadOnlyDictionary<string, string?> Values(string value) => new Dictionary<string, string?> { ["networkKey"] = value };
}
