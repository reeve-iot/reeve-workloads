using System.Text;
using k8s;
using k8s.KubeConfigModels;

namespace Reeve.Workloads.Providers.Kubernetes;

internal static class KubeconfigValidator
{
    public static KubeconfigValidationResult Validate(string? kubeconfigYaml, string? contextName = null)
    {
        var resolved = Resolve(kubeconfigYaml, contextName);
        return resolved.Failure ?? ValidateSelfContained(resolved.Cluster!.ClusterEndpoint, resolved.User!.UserCredentials);
    }

    public static string? ResolveServer(string? kubeconfigYaml, string? contextName = null)
    {
        var resolved = Resolve(kubeconfigYaml, contextName);
        if (resolved.Failure is not null || string.IsNullOrWhiteSpace(resolved.Cluster!.ClusterEndpoint.Server))
        {
            return null;
        }

        var server = resolved.Cluster.ClusterEndpoint.Server.Trim();
        return Uri.TryCreate(server, UriKind.Absolute, out var uri)
            ? uri.GetComponents(UriComponents.SchemeAndServer | UriComponents.Path, UriFormat.UriEscaped).TrimEnd('/')
            : server.TrimEnd('/');
    }

    private sealed record Resolution(KubeconfigValidationResult? Failure, Cluster? Cluster, User? User);

    private static Resolution Resolve(string? kubeconfigYaml, string? contextName)
    {
        if (string.IsNullOrWhiteSpace(kubeconfigYaml))
        {
            return Fail(KubeconfigValidationFailure.Empty, "the kubeconfig is empty.");
        }

        K8SConfiguration config;
        try
        {
            using var stream = new MemoryStream(Encoding.UTF8.GetBytes(kubeconfigYaml));
            config = KubernetesClientConfiguration.LoadKubeConfig(stream);
        }
        catch (Exception)
        {
            return Fail(KubeconfigValidationFailure.Unparseable, "the kubeconfig could not be parsed as a kubeconfig document.");
        }

        if (config is null)
        {
            return Fail(KubeconfigValidationFailure.Unparseable, "the kubeconfig could not be parsed as a kubeconfig document.");
        }

        var targetContextName = string.IsNullOrWhiteSpace(contextName) ? config.CurrentContext : contextName;
        if (string.IsNullOrWhiteSpace(targetContextName))
        {
            return Fail(
                KubeconfigValidationFailure.ContextNotFound,
                "no context was specified and the kubeconfig has no current-context.");
        }

        var context = config.Contexts?.FirstOrDefault(
            c => string.Equals(c.Name, targetContextName, StringComparison.Ordinal));
        if (context?.ContextDetails is null)
        {
            return Fail(
                KubeconfigValidationFailure.ContextNotFound,
                $"the kubeconfig does not define a context named '{targetContextName}'.");
        }

        var cluster = config.Clusters?.FirstOrDefault(
            c => string.Equals(c.Name, context.ContextDetails.Cluster, StringComparison.Ordinal));
        if (cluster?.ClusterEndpoint is null)
        {
            return Fail(
                KubeconfigValidationFailure.ContextNotFound,
                $"the context '{targetContextName}' references a cluster the kubeconfig does not define.");
        }

        var user = config.Users?.FirstOrDefault(
            u => string.Equals(u.Name, context.ContextDetails.User, StringComparison.Ordinal));
        if (user?.UserCredentials is null)
        {
            return Fail(
                KubeconfigValidationFailure.ContextNotFound,
                $"the context '{targetContextName}' references a user the kubeconfig does not define.");
        }

        return new Resolution(null, cluster, user);
    }

    private static Resolution Fail(KubeconfigValidationFailure failure, string message)
        => new(KubeconfigValidationResult.Invalid(failure, message), null, null);

    private static KubeconfigValidationResult ValidateSelfContained(ClusterEndpoint clusterEndpoint, UserCredentials credentials)
    {
        if (clusterEndpoint.SkipTlsVerify)
        {
            return KubeconfigValidationResult.Invalid(
                KubeconfigValidationFailure.NotSelfContained,
                "the cluster sets insecure-skip-tls-verify, which disables TLS verification of the remote API server; "
                + "remove it and embed certificate-authority-data.");
        }

        var hasClusterCaFile = !string.IsNullOrWhiteSpace(clusterEndpoint.CertificateAuthority);
        var hasClusterCaData = !string.IsNullOrWhiteSpace(clusterEndpoint.CertificateAuthorityData);
        if (!hasClusterCaData)
        {
            var reason = hasClusterCaFile
                ? "the cluster references a certificate-authority file"
                : "the cluster has no embedded certificate-authority-data";
            return KubeconfigValidationResult.Invalid(
                KubeconfigValidationFailure.NotSelfContained,
                $"{reason}; embed the cluster certificate-authority-data.");
        }

        if (credentials.ExternalExecution is not null || credentials.AuthProvider is not null)
        {
            return KubeconfigValidationResult.Invalid(
                KubeconfigValidationFailure.NotSelfContained,
                "the user references an external exec/auth-provider credential; the kubeconfig must be self-contained — embed client-certificate-data + client-key-data, an inline token, or basic auth.");
        }

        var hasClientCertFile = !string.IsNullOrWhiteSpace(credentials.ClientCertificate);
        var hasClientCertData = !string.IsNullOrWhiteSpace(credentials.ClientCertificateData);
        var hasClientKeyFile = !string.IsNullOrWhiteSpace(credentials.ClientKey);
        var hasClientKeyData = !string.IsNullOrWhiteSpace(credentials.ClientKeyData);
        if ((hasClientCertFile && !hasClientCertData) || (hasClientKeyFile && !hasClientKeyData))
        {
            return KubeconfigValidationResult.Invalid(
                KubeconfigValidationFailure.NotSelfContained,
                "the user references a client-certificate/client-key file; embed client-certificate-data and client-key-data.");
        }

        var hasEmbeddedClientCert = hasClientCertData && hasClientKeyData;
        var hasToken = !string.IsNullOrWhiteSpace(credentials.Token);
        var hasBasicAuth = !string.IsNullOrWhiteSpace(credentials.UserName) && !string.IsNullOrWhiteSpace(credentials.Password);
        if (!hasEmbeddedClientCert && !hasToken && !hasBasicAuth)
        {
            return KubeconfigValidationResult.Invalid(
                KubeconfigValidationFailure.NotSelfContained,
                "the user carries no embedded credential (client-certificate-data + client-key-data, an inline token, or basic auth).");
        }

        return KubeconfigValidationResult.Valid;
    }
}
