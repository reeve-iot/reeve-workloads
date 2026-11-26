namespace Reeve.Workloads.Abstractions.Update;

/// <summary>The ordered position of an update intent within its cohort.</summary>
public sealed record WorkloadUpdateCohortPlace(Guid CohortId, int Ordinal, int Size);
