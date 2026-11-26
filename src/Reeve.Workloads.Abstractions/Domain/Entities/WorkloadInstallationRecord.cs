namespace Reeve.Workloads.Abstractions.Domain.Entities;

/// <summary>This installation's stable id, as a row. One row, ever: the key is a constant, so two replicas
/// racing to mint one collide on the primary key instead of both writing a value.</summary>
/// <remarks>Stamped into the <c>reeve-iot.dev/installation</c> label on every materialized workload object, and the
/// anchor the DR adoption path uses to tell this installation's retained resources from a foreign one's. It
/// lives in the workloads context rather than a host settings table because the label is the library's and an
/// id that moves with the host's settings store is an id that can be lost without the label noticing.</remarks>
public sealed class WorkloadInstallationRecord
{
    /// <summary>The only key this table ever holds. A constant, so the row is a singleton by construction and
    /// a concurrent second minter loses on insert rather than overwriting.</summary>
    public const string SingletonId = "installation";

    /// <summary>Max persisted length of <see cref="InstallationId"/> — a 36-character GUID, with slack.</summary>
    public const int MaxInstallationIdLength = 64;

    private WorkloadInstallationRecord()
    {
    }

    /// <summary>Mints the row. Only the identity service calls this, and only when a read found nothing.</summary>
    public static WorkloadInstallationRecord Mint(string installationId, DateTimeOffset mintedAtUtc)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(installationId);
        return new WorkloadInstallationRecord
        {
            Id = SingletonId,
            InstallationId = installationId,
            MintedAtUtc = mintedAtUtc,
        };
    }

    /// <summary>Always <see cref="SingletonId"/>.</summary>
    public string Id { get; private set; } = SingletonId;

    /// <summary>The stable id, lowercase GUID. Never rewritten once set.</summary>
    public string InstallationId { get; private set; } = null!;

    /// <summary>When this installation first minted an id. Not read by anything — kept because an operator
    /// asking "is this the same install?" after a restore has nothing else to look at.</summary>
    public DateTimeOffset MintedAtUtc { get; private set; }
}
