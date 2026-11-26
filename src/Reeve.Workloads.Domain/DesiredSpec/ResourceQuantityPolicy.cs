using System;
using System.Globalization;
using System.Text.RegularExpressions;
using Reeve.Workloads.Abstractions;
using Reeve.Workloads.Abstractions.Domain;
using Reeve.Workloads.Abstractions.Domain.DesiredSpec;

namespace Reeve.Workloads.Domain.DesiredSpec;

/// <summary>Which axis a quantity belongs to — CPU and memory accept different grammars.</summary>
public enum ResourceQuantityKind
{
    Cpu = 0,
    Memory = 1,
}

/// <summary>Resource-quantity gates at two strictness levels: strict for operator input (a subset of the
/// Kubernetes grammar), lenient for the render path (everything the manifest parser already accepts).</summary>
public static class ResourceQuantityPolicy
{
    public const long MaxCpuMillicores = 64_000;
    public const long MinMemoryBytes = 6L * 1024 * 1024;
    public const long MaxMemoryBytes = 1024L * 1024 * 1024 * 1024;

    private static readonly Regex StrictPattern = new(
        @"^\+?(?<num>[0-9]+(?:\.[0-9]+)?)(?<suffix>m|k|M|G|T|P|E|Ki|Mi|Gi|Ti|Pi|Ei)?\z",
        RegexOptions.CultureInvariant | RegexOptions.Compiled);

    private static readonly Regex LenientPattern = new(
        @"^\+?(?<num>[0-9]+(?:\.[0-9]+)?|\.[0-9]+)(?<exp>[eE][+-]?[0-9]+)?(?<suffix>m|k|M|G|T|P|E|Ki|Mi|Gi|Ti|Pi|Ei)?\z",
        RegexOptions.CultureInvariant | RegexOptions.Compiled);

    /// <summary>Null when the operator value is acceptable (a blank value is the ABSENCE of an override, not an
    /// error); otherwise the operator-facing reason.</summary>
    public static string? Validate(ResourceQuantityKind kind, string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        if (kind == ResourceQuantityKind.Cpu)
        {
            return TryParseCpuMillicores(value, out _)
                ? null
                : $"'{value}' is not a valid CPU quantity. Use whole millicores such as '250m', or a core count "
                  + $"such as '2' (at most {MaxCpuMillicores / 1000} cores).";
        }

        return TryParseMemoryBytes(value, out _)
            ? null
            : $"'{value}' is not a valid memory quantity. Use a value such as '512Mi' or '2Gi' "
              + $"(at least {MinMemoryBytes / (1024 * 1024)}Mi, at most 1Ti).";
    }

    /// <summary>The cross-field gate on EFFECTIVE values, parsed LENIENTLY because the other side of a pair is
    /// often the author's declaration, which the strict operator gate may legally refuse.</summary>
    public static string? ValidatePair(ResolvedResources? effective)
    {
        if (effective is null)
        {
            return null;
        }

        if (TryParseForRender(ResourceQuantityKind.Cpu, effective.RequestsCpu, out var reqCpu)
            && TryParseForRender(ResourceQuantityKind.Cpu, effective.LimitsCpu, out var limCpu)
            && limCpu < reqCpu)
        {
            return $"The CPU limit '{effective.LimitsCpu}' is below the CPU request '{effective.RequestsCpu}'.";
        }

        if (TryParseForRender(ResourceQuantityKind.Memory, effective.RequestsMemory, out var reqMem)
            && TryParseForRender(ResourceQuantityKind.Memory, effective.LimitsMemory, out var limMem)
            && limMem < reqMem)
        {
            return $"The memory limit '{effective.LimitsMemory}' is below the memory request "
                + $"'{effective.RequestsMemory}'.";
        }

        return null;
    }

    /// <summary>Strict CPU parse to whole millicores. Rejects sub-millicore precision, which the Kubernetes API
    /// server refuses, and memory units, which are not CPU units.</summary>
    public static bool TryParseCpuMillicores(string? value, out long millicores)
    {
        millicores = 0;
        if (!TryMatch(StrictPattern, value, out var mantissa, out var exponent, out var suffix) || exponent != 0)
        {
            return false;
        }

        decimal scaled;
        if (suffix.Length == 0)
        {
            if (!TryMultiply(mantissa, 1000m, out scaled))
            {
                return false;
            }
        }
        else if (suffix == "m")
        {
            scaled = mantissa;
        }
        else
        {
            return false;
        }

        if (scaled != decimal.Truncate(scaled) || scaled <= 0m || scaled > MaxCpuMillicores)
        {
            return false;
        }

        millicores = (long)scaled;
        return true;
    }

    /// <summary>Strict memory parse to whole bytes. The `m` suffix is a fraction of a byte and is refused; the
    /// bounds are the Docker container floor and a 1Ti sanity cap.</summary>
    public static bool TryParseMemoryBytes(string? value, out long bytes)
    {
        bytes = 0;
        if (!TryMatch(StrictPattern, value, out var mantissa, out var exponent, out var suffix) || exponent != 0)
        {
            return false;
        }

        if (suffix == "m" || !TryUnitFactor(suffix, out var factor))
        {
            return false;
        }

        if (!TryMultiply(mantissa, factor, out var scaled))
        {
            return false;
        }

        if (scaled != decimal.Truncate(scaled) || scaled < MinMemoryBytes || scaled > MaxMemoryBytes)
        {
            return false;
        }

        bytes = (long)scaled;
        return true;
    }

    /// <summary>The RENDER gate: every value the manifest parser accepts EXCEPT those that would render as an
    /// unlimited field, yielding millicores or bytes. False means the renderer must refuse rather than emit.</summary>
    public static bool TryParseForRender(ResourceQuantityKind kind, string? value, out long units)
    {
        units = 0;
        if (!TryMatch(LenientPattern, value, out var mantissa, out var exponent, out var suffix))
        {
            return false;
        }

        decimal factor;
        if (exponent != 0)
        {
            if (suffix.Length > 0 || !TryPow10(exponent, out factor))
            {
                return false;
            }
        }
        else if (!TryUnitFactor(suffix, out factor))
        {
            return false;
        }

        if (!TryMultiply(mantissa, factor, out var scaled))
        {
            return false;
        }

        if (kind == ResourceQuantityKind.Cpu && !TryMultiply(scaled, 1000m, out scaled))
        {
            return false;
        }

        if (scaled <= 0m || scaled > long.MaxValue)
        {
            return false;
        }

        units = (long)decimal.Ceiling(scaled);
        return units > 0;
    }

    private static bool TryMatch(Regex pattern, string? value, out decimal mantissa, out int exponent, out string suffix)
    {
        mantissa = 0m;
        exponent = 0;
        suffix = string.Empty;
        if (string.IsNullOrWhiteSpace(value))
        {
            return false;
        }

        var match = pattern.Match(value.Trim());
        if (!match.Success)
        {
            return false;
        }

        if (!decimal.TryParse(
                match.Groups["num"].Value,
                NumberStyles.AllowDecimalPoint,
                CultureInfo.InvariantCulture,
                out mantissa))
        {
            return false;
        }

        var exp = match.Groups["exp"];
        if (exp.Success && !int.TryParse(
                exp.Value.AsSpan(1), NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out exponent))
        {
            return false;
        }

        suffix = match.Groups["suffix"].Value;
        return true;
    }

    private static bool TryUnitFactor(string suffix, out decimal factor)
    {
        factor = suffix switch
        {
            "" => 1m,
            "m" => 0.001m,
            "Ki" => 1024m,
            "Mi" => 1024m * 1024,
            "Gi" => 1024m * 1024 * 1024,
            "Ti" => 1024m * 1024 * 1024 * 1024,
            "Pi" => 1024m * 1024 * 1024 * 1024 * 1024,
            "Ei" => 1024m * 1024 * 1024 * 1024 * 1024 * 1024,
            "k" => 1_000m,
            "M" => 1_000_000m,
            "G" => 1_000_000_000m,
            "T" => 1_000_000_000_000m,
            "P" => 1_000_000_000_000_000m,
            "E" => 1_000_000_000_000_000_000m,
            _ => 0m,
        };
        return factor != 0m;
    }

    private static bool TryPow10(int power, out decimal factor)
    {
        factor = 1m;
        var steps = power == int.MinValue ? long.MaxValue : Math.Abs((long)power);
        for (long i = 0; i < steps; i++)
        {
            if (!TryMultiply(factor, 10m, out factor))
            {
                return false;
            }
        }

        if (power >= 0)
        {
            return true;
        }

        factor = 1m / factor;
        return true;
    }

    private static bool TryMultiply(decimal left, decimal right, out decimal product)
    {
        try
        {
            product = left * right;
            return true;
        }
        catch (OverflowException)
        {
            product = 0m;
            return false;
        }
    }
}

/// <summary>A resolved resource quantity could not be parsed. Thrown instead of rendering an absent field,
/// which every provider reads as "unlimited".</summary>
public sealed class WorkloadResourceRenderException : Exception
{
    public WorkloadResourceRenderException(string message) : base(message) { }
}

/// <summary>A SecretRef env named no material the provider could resolve. Its own type because this is OUR
/// refusal, not an unreachable runtime: substituting any other value would hand the workload a wrong secret.</summary>
public sealed class WorkloadSecretMaterialMissingException : Exception
{
    public WorkloadSecretMaterialMissingException(string message) : base(message) { }
}
