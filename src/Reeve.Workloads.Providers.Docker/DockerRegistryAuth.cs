using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Text.RegularExpressions;
using Docker.DotNet.Models;
using Reeve.Workloads.Abstractions.Contracts;

namespace Reeve.Workloads.Providers.Docker;

internal static class DockerRegistryAuth
{
    public const string ImplicitDockerHubRegistry = "docker.io";

    public static DockerImageReference ParseReference(string imageReference)
    {
        if (string.IsNullOrWhiteSpace(imageReference))
        {
            throw new ArgumentException("Image reference must not be null or blank.", nameof(imageReference));
        }

        var raw = imageReference.Trim();

        string name;
        string reference;
        bool isDigest;

        var at = raw.IndexOf('@');
        if (at >= 0)
        {
            name = raw[..at];
            reference = raw[(at + 1)..];
            isDigest = true;
        }
        else
        {
            var lastSlash = raw.LastIndexOf('/');
            var colon = raw.LastIndexOf(':');
            if (colon > lastSlash)
            {
                name = raw[..colon];
                reference = raw[(colon + 1)..];
            }
            else
            {
                name = raw;
                reference = "latest";
            }

            isDigest = false;
        }

        if (string.IsNullOrWhiteSpace(name) || string.IsNullOrWhiteSpace(reference))
        {
            throw new ArgumentException($"Image reference '{imageReference}' is malformed.", nameof(imageReference));
        }

        var registryHost = ResolveRegistryHostFromName(name);

        var (fromImage, tag) = isDigest
            ? ($"{name}@{reference}", (string?)null)
            : (name, reference);

        return new DockerImageReference(imageReference, registryHost, name, reference, isDigest, fromImage, tag);
    }

    public static string ResolveRegistryHost(string imageReference) => ParseReference(imageReference).RegistryHost;

    private static string ResolveRegistryHostFromName(string name)
    {
        var firstSlash = name.IndexOf('/');
        if (firstSlash <= 0)
        {
            return ImplicitDockerHubRegistry;
        }

        var firstComponent = name[..firstSlash];
        var looksLikeHost = firstComponent.Contains('.', StringComparison.Ordinal)
            || firstComponent.Contains(':', StringComparison.Ordinal)
            || firstComponent.Equals("localhost", StringComparison.OrdinalIgnoreCase);

        return looksLikeHost ? firstComponent : ImplicitDockerHubRegistry;
    }

    public static bool IsSecureRegistry(string registryHost)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(registryHost);

        var host = StripPort(registryHost);

        if (host.EndsWith('.'))
        {
            host = host[..^1];
        }

        if (host.Equals("localhost", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        if (!IPAddress.TryParse(host, out var ip))
        {
            return true;
        }

        if (ip.IsIPv4MappedToIPv6)
        {
            ip = ip.MapToIPv4();
        }

        return !IPAddress.IsLoopback(ip);
    }

    private static string StripPort(string registryHost)
    {
        if (registryHost.StartsWith('['))
        {
            var close = registryHost.IndexOf(']');
            return close > 1 ? registryHost[1..close] : registryHost;
        }

        var firstColon = registryHost.IndexOf(':');
        if (firstColon < 0)
        {
            return registryHost;
        }

        return firstColon == registryHost.LastIndexOf(':') ? registryHost[..firstColon] : registryHost;
    }

    public static bool HostMatches(string hostPattern, string registryHost)
    {
        if (string.IsNullOrWhiteSpace(hostPattern) || string.IsNullOrWhiteSpace(registryHost))
        {
            return false;
        }

        var regex = "^" + Regex.Escape(hostPattern.Trim())
            .Replace("\\*", ".*", StringComparison.Ordinal)
            .Replace("\\?", ".", StringComparison.Ordinal) + @"\z";

        return Regex.IsMatch(registryHost, regex, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    }

    public static RegistryCredential? MatchCredential(
        IReadOnlyList<RegistryCredential>? credentials,
        string registryHost)
    {
        if (credentials is null || credentials.Count == 0 || string.IsNullOrWhiteSpace(registryHost))
        {
            return null;
        }

        return credentials.FirstOrDefault(c => c is not null && HostMatches(c.HostPattern, registryHost));
    }

    public static AuthConfig BuildAuthConfig(RegistryCredential credential, string registryHost)
    {
        ArgumentNullException.ThrowIfNull(credential);

        return credential.AuthType switch
        {
            RegistryAuthType.Bearer => new AuthConfig { RegistryToken = credential.Token },
            _ => new AuthConfig
            {
                Username = credential.Username,
                Password = credential.Token,
                ServerAddress = registryHost,
            },
        };
    }

    public static DockerAuthResolution Resolve(string imageReference, DockerProviderSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);

        var registryHost = ResolveRegistryHost(imageReference);
        var credential = MatchCredential(settings.RegistryCredentials, registryHost);

        if (credential is null)
        {
            return DockerAuthResolution.Anonymous(registryHost);
        }

        if (!IsSecureRegistry(registryHost))
        {
            return DockerAuthResolution.Reject(
                registryHost,
                $"Refusing to send registry credentials for '{registryHost}' over an insecure (plain-HTTP) "
                    + "connection. Only TLS registries may be used with credentials — remove the credential for this "
                    + "host or serve the registry over HTTPS.");
        }

        return DockerAuthResolution.WithAuth(registryHost, BuildAuthConfig(credential, registryHost), credential.AuthType);
    }
}

internal readonly record struct DockerImageReference(
    string Raw,
    string RegistryHost,
    string Name,
    string Reference,
    bool IsDigest,
    string FromImage,
    string? Tag);

internal sealed record DockerAuthResolution
{
    public required string RegistryHost { get; init; }

    public AuthConfig? Auth { get; init; }

    public RegistryAuthType? AuthType { get; init; }

    public bool Rejected { get; init; }

    public string? RejectionMessage { get; init; }

    public bool IsAnonymous => Auth is null && !Rejected;

    public static DockerAuthResolution Anonymous(string registryHost) =>
        new() { RegistryHost = registryHost };

    public static DockerAuthResolution WithAuth(string registryHost, AuthConfig auth, RegistryAuthType authType) =>
        new() { RegistryHost = registryHost, Auth = auth, AuthType = authType };

    public static DockerAuthResolution Reject(string registryHost, string message) =>
        new() { RegistryHost = registryHost, Rejected = true, RejectionMessage = message };
}
