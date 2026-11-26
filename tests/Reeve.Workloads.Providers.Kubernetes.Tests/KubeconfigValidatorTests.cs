using FluentAssertions;
using Reeve.Workloads.Providers.Kubernetes;
using Xunit;

namespace Reeve.Workloads.Providers.Kubernetes.Tests;

public sealed class KubeconfigValidatorTests
{
    private const string SelfContained = """
        apiVersion: v1
        kind: Config
        current-context: remote
        clusters:
        - name: remote-cluster
          cluster:
            server: https://10.0.0.1:6443
            certificate-authority-data: ZmFrZS1jYS1kYXRh
        contexts:
        - name: remote
          context:
            cluster: remote-cluster
            user: remote-user
        users:
        - name: remote-user
          user:
            client-certificate-data: ZmFrZS1jZXJ0LWRhdGE=
            client-key-data: ZmFrZS1rZXktZGF0YQ==
        """;

    private const string FileReferenceUser = """
        apiVersion: v1
        kind: Config
        current-context: remote
        clusters:
        - name: remote-cluster
          cluster:
            server: https://10.0.0.1:6443
            certificate-authority-data: ZmFrZS1jYS1kYXRh
        contexts:
        - name: remote
          context:
            cluster: remote-cluster
            user: remote-user
        users:
        - name: remote-user
          user:
            client-certificate: /home/op/.kube/client.crt
            client-key: /home/op/.kube/client.key
        """;

    private const string FileReferenceCluster = """
        apiVersion: v1
        kind: Config
        current-context: remote
        clusters:
        - name: remote-cluster
          cluster:
            server: https://10.0.0.1:6443
            certificate-authority: /home/op/.kube/ca.crt
        contexts:
        - name: remote
          context:
            cluster: remote-cluster
            user: remote-user
        users:
        - name: remote-user
          user:
            token: c2VjcmV0LXRva2Vu
        """;

    private const string SelfContainedToken = """
        apiVersion: v1
        kind: Config
        current-context: remote
        clusters:
        - name: remote-cluster
          cluster:
            server: https://10.0.0.1:6443
            certificate-authority-data: ZmFrZS1jYS1kYXRh
        contexts:
        - name: remote
          context:
            cluster: remote-cluster
            user: remote-user
        users:
        - name: remote-user
          user:
            token: c2VjcmV0LXRva2Vu
        """;

    private const string SelfContainedBasicAuth = """
        apiVersion: v1
        kind: Config
        current-context: remote
        clusters:
        - name: remote-cluster
          cluster:
            server: https://10.0.0.1:6443
            certificate-authority-data: ZmFrZS1jYS1kYXRh
        contexts:
        - name: remote
          context:
            cluster: remote-cluster
            user: remote-user
        users:
        - name: remote-user
          user:
            username: admin
            password: c2VjcmV0
        """;

    private const string SkipTlsNoCaData = """
        apiVersion: v1
        kind: Config
        current-context: remote
        clusters:
        - name: remote-cluster
          cluster:
            server: https://10.0.0.1:6443
            insecure-skip-tls-verify: true
        contexts:
        - name: remote
          context:
            cluster: remote-cluster
            user: remote-user
        users:
        - name: remote-user
          user:
            token: c2VjcmV0LXRva2Vu
        """;

    private const string SkipTlsWithCaData = """
        apiVersion: v1
        kind: Config
        current-context: remote
        clusters:
        - name: remote-cluster
          cluster:
            server: https://10.0.0.1:6443
            certificate-authority-data: ZmFrZS1jYS1kYXRh
            insecure-skip-tls-verify: true
        contexts:
        - name: remote
          context:
            cluster: remote-cluster
            user: remote-user
        users:
        - name: remote-user
          user:
            token: c2VjcmV0LXRva2Vu
        """;

    private const string ExecCredential = """
        apiVersion: v1
        kind: Config
        current-context: remote
        clusters:
        - name: remote-cluster
          cluster:
            server: https://10.0.0.1:6443
            certificate-authority-data: ZmFrZS1jYS1kYXRh
        contexts:
        - name: remote
          context:
            cluster: remote-cluster
            user: remote-user
        users:
        - name: remote-user
          user:
            exec:
              apiVersion: client.authentication.k8s.io/v1beta1
              command: /bin/some-cred-helper
              args:
              - get-token
        """;

    private const string AuthProviderCredential = """
        apiVersion: v1
        kind: Config
        current-context: remote
        clusters:
        - name: remote-cluster
          cluster:
            server: https://10.0.0.1:6443
            certificate-authority-data: ZmFrZS1jYS1kYXRh
        contexts:
        - name: remote
          context:
            cluster: remote-cluster
            user: remote-user
        users:
        - name: remote-user
          user:
            auth-provider:
              name: gcp
              config:
                cmd-path: /usr/bin/gcloud
        """;

    private const string TwoContexts = """
        apiVersion: v1
        kind: Config
        current-context: good
        clusters:
        - name: remote-cluster
          cluster:
            server: https://10.0.0.1:6443
            certificate-authority-data: ZmFrZS1jYS1kYXRh
        contexts:
        - name: good
          context:
            cluster: remote-cluster
            user: good-user
        - name: bad
          context:
            cluster: remote-cluster
            user: bad-user
        users:
        - name: good-user
          user:
            client-certificate-data: ZmFrZS1jZXJ0
            client-key-data: ZmFrZS1rZXk=
        - name: bad-user
          user:
            client-certificate: /path/client.crt
            client-key: /path/client.key
        """;

    private const string NoCurrentContext = """
        apiVersion: v1
        kind: Config
        clusters:
        - name: remote-cluster
          cluster:
            server: https://10.0.0.1:6443
            certificate-authority-data: ZmFrZS1jYS1kYXRh
        contexts:
        - name: remote
          context:
            cluster: remote-cluster
            user: remote-user
        users:
        - name: remote-user
          user:
            token: c2VjcmV0LXRva2Vu
        """;

    [Fact]
    public void Self_contained_kubeconfig_is_valid()
    {
        var result = KubeconfigValidator.Validate(SelfContained);

        result.IsValid.Should().BeTrue();
        result.Failure.Should().Be(KubeconfigValidationFailure.None);
    }

    [Fact]
    public void Inline_token_with_embedded_ca_is_valid()
    {
        var result = KubeconfigValidator.Validate(SelfContainedToken);

        result.IsValid.Should().BeTrue();
        result.Failure.Should().Be(KubeconfigValidationFailure.None);
    }

    [Fact]
    public void Basic_auth_with_embedded_ca_is_valid()
    {
        var result = KubeconfigValidator.Validate(SelfContainedBasicAuth);

        result.IsValid.Should().BeTrue();
        result.Failure.Should().Be(KubeconfigValidationFailure.None);
    }

    [Fact]
    public void Skip_tls_verify_without_ca_data_is_not_self_contained()
    {
        var result = KubeconfigValidator.Validate(SkipTlsNoCaData);

        result.IsValid.Should().BeFalse();
        result.Failure.Should().Be(KubeconfigValidationFailure.NotSelfContained);
    }

    [Fact]
    public void Skip_tls_verify_WITH_ca_data_is_still_not_self_contained()
    {
        var result = KubeconfigValidator.Validate(SkipTlsWithCaData);

        result.IsValid.Should().BeFalse();
        result.Failure.Should().Be(KubeconfigValidationFailure.NotSelfContained);
        result.Message.Should().Contain("insecure-skip-tls-verify");
    }

    [Fact]
    public void Exec_credential_is_not_self_contained()
    {
        var result = KubeconfigValidator.Validate(ExecCredential);

        result.IsValid.Should().BeFalse();
        result.Failure.Should().Be(KubeconfigValidationFailure.NotSelfContained);
    }

    [Fact]
    public void Auth_provider_credential_is_not_self_contained()
    {
        var result = KubeconfigValidator.Validate(AuthProviderCredential);

        result.IsValid.Should().BeFalse();
        result.Failure.Should().Be(KubeconfigValidationFailure.NotSelfContained);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(null)]
    public void Empty_kubeconfig_is_invalid(string? yaml)
    {
        var result = KubeconfigValidator.Validate(yaml);

        result.IsValid.Should().BeFalse();
        result.Failure.Should().Be(KubeconfigValidationFailure.Empty);
    }

    [Fact]
    public void Unparseable_kubeconfig_is_invalid()
    {
        var result = KubeconfigValidator.Validate("- just\n- a\n- list");

        result.IsValid.Should().BeFalse();
        result.Failure.Should().Be(KubeconfigValidationFailure.Unparseable);
    }

    [Fact]
    public void Garbage_text_is_unparseable()
    {
        var result = KubeconfigValidator.Validate("foo:\n\t- tab-indented is a yaml error");

        result.IsValid.Should().BeFalse();
        result.Failure.Should().Be(KubeconfigValidationFailure.Unparseable);
    }

    [Fact]
    public void File_reference_user_credentials_are_not_self_contained()
    {
        var result = KubeconfigValidator.Validate(FileReferenceUser);

        result.IsValid.Should().BeFalse();
        result.Failure.Should().Be(KubeconfigValidationFailure.NotSelfContained);
    }

    [Fact]
    public void File_reference_cluster_ca_is_not_self_contained()
    {
        var result = KubeconfigValidator.Validate(FileReferenceCluster);

        result.IsValid.Should().BeFalse();
        result.Failure.Should().Be(KubeconfigValidationFailure.NotSelfContained);
    }

    [Fact]
    public void Unknown_named_context_is_invalid()
    {
        var result = KubeconfigValidator.Validate(SelfContained, contextName: "does-not-exist");

        result.IsValid.Should().BeFalse();
        result.Failure.Should().Be(KubeconfigValidationFailure.ContextNotFound);
    }

    [Fact]
    public void Missing_current_context_and_no_override_is_invalid()
    {
        var result = KubeconfigValidator.Validate(NoCurrentContext);

        result.IsValid.Should().BeFalse();
        result.Failure.Should().Be(KubeconfigValidationFailure.ContextNotFound);
    }

    [Fact]
    public void Named_context_override_resolves_the_specified_context()
    {
        var byOverride = KubeconfigValidator.Validate(TwoContexts, contextName: "bad");
        byOverride.IsValid.Should().BeFalse();
        byOverride.Failure.Should().Be(KubeconfigValidationFailure.NotSelfContained);

        KubeconfigValidator.Validate(TwoContexts).IsValid.Should().BeTrue();

        KubeconfigValidator.Validate(TwoContexts, contextName: "good").IsValid.Should().BeTrue();
    }

    [Fact]
    public void Context_referencing_an_undefined_user_is_context_not_found()
    {
        const string danglingUser = """
            apiVersion: v1
            kind: Config
            current-context: remote
            clusters:
            - name: remote-cluster
              cluster:
                server: https://10.0.0.1:6443
                certificate-authority-data: ZmFrZS1jYQ==
            contexts:
            - name: remote
              context:
                cluster: remote-cluster
                user: missing-user
            users: []
            """;

        var result = KubeconfigValidator.Validate(danglingUser);

        result.IsValid.Should().BeFalse();
        result.Failure.Should().Be(KubeconfigValidationFailure.ContextNotFound);
    }
}
