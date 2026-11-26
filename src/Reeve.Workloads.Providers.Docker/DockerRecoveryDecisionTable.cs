using System;
using System.Collections.Generic;
using System.Linq;

namespace Reeve.Workloads.Providers.Docker;

internal enum DockerRecoveryAction
{
    CreateFresh = 0,

    RunFullSwap = 1,

    ResumeCreateNew = 2,

    ResumeStartNew = 3,

    FinishRemovePrevious = 4,

    Converged = 5,
}

internal sealed record DockerRecoveryDecision
{
    public required DockerRecoveryAction Action { get; init; }

    public DockerContainerRef? NewContainer { get; init; }

    public DockerContainerRef? PreviousContainer { get; init; }

    public IReadOnlyList<DockerContainerRef> ObstructingContainers { get; init; } = Array.Empty<DockerContainerRef>();

    public string? Rationale { get; init; }
}

internal static class DockerRecoveryDecisionTable
{
    public const string RenamedSuffixMarker = "-prev-";

    public static string BuildRenamedName(string canonicalName, string? oldSpecHash)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(canonicalName);
        var hash8 = string.IsNullOrWhiteSpace(oldSpecHash)
            ? "unknown"
            : (oldSpecHash!.Length <= 8 ? oldSpecHash : oldSpecHash[..8]);
        return $"{canonicalName}{RenamedSuffixMarker}{hash8}";
    }

    public static bool IsRenamed(DockerContainerRef container)
    {
        ArgumentNullException.ThrowIfNull(container);
        return container.Name is { Length: > 0 } name
            && name.Contains(RenamedSuffixMarker, StringComparison.Ordinal);
    }

    public const string DefaultRestartNonce = "0";

    public static string NormalizeRestartNonce(string? raw)
        => string.IsNullOrWhiteSpace(raw) ? DefaultRestartNonce : raw.Trim();

    public static DockerRecoveryDecision Decide(
        IReadOnlyList<DockerContainerRef> observed,
        string targetSpecHash)
        => Decide(observed, targetSpecHash, DefaultRestartNonce);

    public static string NormalizeUnhashedEnvDigest(string? raw)
        => string.IsNullOrWhiteSpace(raw) ? string.Empty : raw.Trim();

    public static DockerRecoveryDecision Decide(
        IReadOnlyList<DockerContainerRef> observed,
        string targetSpecHash,
        string targetRestartNonce,
        string? targetUnhashedEnvDigest = null,
        string? targetCredentialDigest = null)
    {
        ArgumentNullException.ThrowIfNull(observed);
        ArgumentException.ThrowIfNullOrWhiteSpace(targetSpecHash);

        var targetNonce = NormalizeRestartNonce(targetRestartNonce);
        var targetDigest = NormalizeUnhashedEnvDigest(targetUnhashedEnvDigest);
        var targetCredentials = NormalizeUnhashedEnvDigest(targetCredentialDigest);

        bool MatchesTarget(DockerContainerRef c)
            => string.Equals(c.SpecHash, targetSpecHash, StringComparison.Ordinal)
               && string.Equals(NormalizeRestartNonce(c.RestartNonce), targetNonce, StringComparison.Ordinal)
               && string.Equals(NormalizeUnhashedEnvDigest(c.UnhashedEnvDigest), targetDigest, StringComparison.Ordinal)
               && string.Equals(NormalizeUnhashedEnvDigest(c.CredentialDigest), targetCredentials, StringComparison.Ordinal);

        var news = observed.Where(MatchesTarget).ToList();
        var olds = observed.Where(c => !MatchesTarget(c)).ToList();

        var newContainer = news.FirstOrDefault(c => c.IsRunning) ?? news.FirstOrDefault();
        var oldRenamed = olds.FirstOrDefault(IsRenamed);
        var oldCanonical = olds.FirstOrDefault(c => !IsRenamed(c));
        var previous = oldRenamed ?? oldCanonical;

        var obstructing = olds.Where(c => !ReferenceEquals(c, previous)).ToList();

        if (newContainer is not null)
        {
            if (newContainer.IsRunning)
            {
                return previous is not null
                    ? Decision(DockerRecoveryAction.FinishRemovePrevious, newContainer, previous, obstructing,
                        "new container is running and a previous (renamed old) remains ⇒ remove the previous (RemovedPrev)")
                    : Decision(DockerRecoveryAction.Converged, newContainer, null, obstructing,
                        "new container is running and no previous remains ⇒ the swap already completed");
            }

            return Decision(DockerRecoveryAction.ResumeStartNew, newContainer, previous, obstructing,
                "new container exists but is not running ⇒ start + health-gate it (roll back to the previous on failure)");
        }

        if (oldRenamed is not null)
        {
            return Decision(DockerRecoveryAction.ResumeCreateNew, null, oldRenamed, obstructing,
                "old already renamed but no new container ⇒ create + start + gate the new (RollbackOld on failure)");
        }

        if (oldCanonical is not null)
        {
            return Decision(DockerRecoveryAction.RunFullSwap, null, oldCanonical, obstructing,
                "only the canonical old exists ⇒ stop → rename → create → start → gate → remove-prev");
        }

        return Decision(DockerRecoveryAction.CreateFresh, null, null, obstructing,
            "no containers carry the instance label ⇒ create + start the desired container fresh");
    }

    private static DockerRecoveryDecision Decision(
        DockerRecoveryAction action,
        DockerContainerRef? newContainer,
        DockerContainerRef? previous,
        IReadOnlyList<DockerContainerRef> obstructing,
        string rationale) => new()
        {
            Action = action,
            NewContainer = newContainer,
            PreviousContainer = previous,
            ObstructingContainers = obstructing,
            Rationale = rationale,
        };
}
