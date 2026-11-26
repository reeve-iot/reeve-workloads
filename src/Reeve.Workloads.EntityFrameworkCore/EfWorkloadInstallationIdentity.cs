using Microsoft.EntityFrameworkCore;
using Reeve.Workloads.Abstractions;
using Reeve.Workloads.Abstractions.Domain.Entities;
using Reeve.Workloads.EntityFrameworkCore.Persistence;

namespace Reeve.Workloads.EntityFrameworkCore;

/// <summary>The installation id as a row in the workloads context, minted on first read. A fresh database
/// mints a NEW id — which is exactly the FOREIGN id adoption re-homes — while a restart keeps it.</summary>
/// <remarks>A default rather than a port the host answers: every host wrote the same forty lines against its
/// own settings store, and the label the id ends up in is the library's. The one thing that is not trivial is
/// the race, and the singleton key is what settles it — two minters cannot both insert.</remarks>
public sealed class EfWorkloadInstallationIdentity : IWorkloadInstallationIdentity
{
    private readonly WorkloadsDbContext context;

    public EfWorkloadInstallationIdentity(WorkloadsDbContext context)
        => this.context = context ?? throw new ArgumentNullException(nameof(context));

    /// <inheritdoc />
    public async Task<string> GetInstallationIdAsync(CancellationToken cancellationToken = default)
    {
        var existing = await ReadAsync(cancellationToken).ConfigureAwait(false);
        if (existing is not null)
        {
            return existing;
        }

        var minted = Guid.NewGuid().ToString("D");
        context.WorkloadInstallation.Add(
            WorkloadInstallationRecord.Mint(minted, DateTimeOffset.UtcNow));

        try
        {
            await context.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
            return minted;
        }
        catch (DbUpdateException ex)
        {
            context.ChangeTracker.Clear();
            return await ReadAsync(cancellationToken).ConfigureAwait(false)
                ?? throw new InvalidOperationException(
                    "The installation row could not be written and could not be read back.", ex);
        }
    }

    private Task<string?> ReadAsync(CancellationToken cancellationToken)
        => context.WorkloadInstallation
            .AsNoTracking()
            .Where(row => row.Id == WorkloadInstallationRecord.SingletonId)
            .Select(row => (string?)row.InstallationId)
            .FirstOrDefaultAsync(cancellationToken);
}
