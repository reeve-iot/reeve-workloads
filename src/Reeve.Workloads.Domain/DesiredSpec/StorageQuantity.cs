using System.Globalization;
using System.Text.RegularExpressions;
using Reeve.Workloads.Abstractions;
using Reeve.Workloads.Abstractions.Domain;
using Reeve.Workloads.Abstractions.Domain.DesiredSpec;

namespace Reeve.Workloads.Domain.DesiredSpec;

internal static class StorageQuantity
{
    private static readonly Regex QuantityPattern = new(
        @"^(?<num>[0-9]+(?:\.[0-9]+)?|\.[0-9]+)(?<exp>[eE][+-]?[0-9]+)?(?<suffix>m|k|M|G|T|P|E|Ki|Mi|Gi|Ti|Pi|Ei)?\z",
        RegexOptions.CultureInvariant | RegexOptions.Compiled);

    public static bool TryParseBytes(string? value, out long bytes)
    {
        bytes = 0;
        if (string.IsNullOrWhiteSpace(value))
        {
            return false;
        }

        var s = value.Trim();
        if (s.Length > 0 && s[0] == '+')
        {
            s = s[1..];
        }

        var match = QuantityPattern.Match(s);
        if (!match.Success)
        {
            return false;
        }

        if (!decimal.TryParse(match.Groups["num"].Value, NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out var mantissa))
        {
            return false;
        }

        var exp = match.Groups["exp"];
        var suffix = match.Groups["suffix"].Value;

        decimal result;
        try
        {
            if (exp.Success)
            {
                var power = int.Parse(exp.Value.AsSpan(1), NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture);
                result = mantissa * Pow10(power);
            }
            else if (suffix.Length == 0)
            {
                result = mantissa;
            }
            else if (!TryUnitFactor(suffix, out var factor))
            {
                return false;
            }
            else
            {
                result = mantissa * factor;
            }
        }
        catch (OverflowException)
        {
            return false;
        }

        if (result <= 0m || result > long.MaxValue)
        {
            return false;
        }

        bytes = (long)decimal.Truncate(result);
        return bytes > 0;
    }

    private static bool TryUnitFactor(string suffix, out decimal factor)
    {
        factor = suffix switch
        {
            "Ki" => 1024m,
            "Mi" => 1024m * 1024,
            "Gi" => 1024m * 1024 * 1024,
            "Ti" => 1024m * 1024 * 1024 * 1024,
            "Pi" => 1024m * 1024 * 1024 * 1024 * 1024,
            "Ei" => 1024m * 1024 * 1024 * 1024 * 1024 * 1024,
            "m" => 0.001m,
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

    private static decimal Pow10(int power)
    {
        if (power == 0)
        {
            return 1m;
        }

        var result = 1m;
        var magnitude = Math.Abs(power);
        for (var i = 0; i < magnitude; i++)
        {
            result *= 10m;
        }

        return power > 0 ? result : 1m / result;
    }
}
