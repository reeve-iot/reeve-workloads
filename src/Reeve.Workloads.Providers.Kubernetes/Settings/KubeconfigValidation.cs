namespace Reeve.Workloads.Providers.Kubernetes;

internal enum KubeconfigValidationFailure
{
    None = 0,

    Empty,

    Unparseable,

    ContextNotFound,

    NotSelfContained,
}

internal readonly record struct KubeconfigValidationResult(bool IsValid, KubeconfigValidationFailure Failure, string? Message)
{
    public static KubeconfigValidationResult Valid { get; } = new(true, KubeconfigValidationFailure.None, null);

    public static KubeconfigValidationResult Invalid(KubeconfigValidationFailure failure, string message)
        => new(false, failure, message);
}
