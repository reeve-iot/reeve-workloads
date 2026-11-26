namespace Reeve.Workloads.Providers.Kubernetes;

internal interface IInClusterEnvironment
{
    bool IsInCluster { get; }

    string? Namespace { get; }
}

internal sealed class InClusterEnvironment : IInClusterEnvironment
{
    public const string ServiceAccountTokenPath = "/var/run/secrets/kubernetes.io/serviceaccount/token";

    public const string NamespacePath = "/var/run/secrets/kubernetes.io/serviceaccount/namespace";

    private readonly bool isInCluster;
    private readonly string? podNamespace;

    public InClusterEnvironment()
        : this(path => File.Exists(path), ServiceAccountTokenPath, namespacePath: NamespacePath)
    {
    }

    internal InClusterEnvironment(Func<string, bool> fileExists, string tokenPath)
        : this(fileExists, tokenPath, () => null)
    {
    }

    internal InClusterEnvironment(Func<string, bool> fileExists, string tokenPath, string namespacePath)
        : this(fileExists, tokenPath, () => ReadNamespaceFile(namespacePath))
    {
    }

    internal InClusterEnvironment(Func<string, bool> fileExists, string tokenPath, Func<string?> readNamespaceFile)
    {
        ArgumentNullException.ThrowIfNull(fileExists);
        ArgumentNullException.ThrowIfNull(readNamespaceFile);
        isInCluster = fileExists(tokenPath);
        var read = readNamespaceFile()?.Trim();
        podNamespace = string.IsNullOrEmpty(read) ? null : read;
    }

    public bool IsInCluster => isInCluster;

    public string? Namespace => podNamespace;

    private static string? ReadNamespaceFile(string path)
    {
        try
        {
            return File.Exists(path) ? File.ReadAllText(path) : null;
        }
        catch (IOException)
        {
            return null;
        }
        catch (UnauthorizedAccessException)
        {
            return null;
        }
    }
}
