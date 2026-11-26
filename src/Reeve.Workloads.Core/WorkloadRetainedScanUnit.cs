using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Reeve.Workloads.Abstractions;
using Reeve.Workloads.Abstractions.Domain.Entities;

namespace Reeve.Workloads.Core;

internal sealed class WorkloadRetainedScanUnit(
    IServiceScopeFactory scopeFactory,
    IWorkloadClock clock,
    IWorkloadDiagnosticsAnnouncer announcer,
    IOptions<WorkloadRetainedScanOptions> options,
    ILogger<WorkloadRetainedScanUnit> logger) : IWorkloadEngineUnit
{
    public const string Key = "workload-retained-scan";

    public const string AlertCode = "workloads.retained-volumes.90d";

    private readonly WorkloadRetainedScanOptions _options = options.Value;
    private CancellationTokenSource? _cts;
    private Task? _loop;

    private readonly object _publishGate = new();
    private string? _lastPublishedSignature;

    string IWorkloadEngineUnit.Key => Key;

    public Task StartAsync(CancellationToken cancellationToken)
    {
        if (_loop is not null)
        {
            return Task.CompletedTask;
        }

        _cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var token = _cts.Token;
        _loop = Task.Run(async () =>
        {
            using var timer = new PeriodicTimer(_options.ScanInterval);
            do
            {
                try
                {
                    await ScanAsync(token).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (token.IsCancellationRequested)
                {
                    break;
                }
                catch (Exception ex)
                {
                    logger.LogError(ex, "Retained-volume scan tick failed.");
                }
            } while (await timer.WaitForNextTickAsync(token).ConfigureAwait(false));
        }, token);

        logger.LogInformation(
            "Retained-volume scan started (threshold {ThresholdDays}d, interval {Interval}).",
            _options.ThresholdDays, _options.ScanInterval);
        return Task.CompletedTask;
    }

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        if (_cts is null)
        {
            return;
        }

        await _cts.CancelAsync().ConfigureAwait(false);
        if (_loop is not null)
        {
            try { await _loop.ConfigureAwait(false); } catch (OperationCanceledException) { }
        }

        _cts.Dispose();
        _cts = null;
        _loop = null;
    }

    public async Task ScanAsync(CancellationToken cancellationToken)
    {
        var now = clock.UtcNow;
        var scope = scopeFactory.CreateAsyncScope();
        await using var scopeLifetime = scope.ConfigureAwait(false);
        var service = scope.ServiceProvider.GetRequiredService<IRetainedVolumeService>();

        var stale = await service.ListOlderThanAsync(_options.Threshold, now, cancellationToken).ConfigureAwait(false);
        if (stale.Count == 0)
        {
            lock (_publishGate)
            {
                _lastPublishedSignature = null;
            }

            return;
        }

        var signature = ComputeSignature(stale);
        lock (_publishGate)
        {
            if (string.Equals(_lastPublishedSignature, signature, StringComparison.Ordinal))
            {
                return;
            }

            _lastPublishedSignature = signature;
        }

        await announcer.AnnounceAsync(
            new WorkloadDiagnostic
            {
                Code = AlertCode,
                Severity = WorkloadDiagnosticSeverity.Info,
                Message = BuildMessage(stale),
                FixHint = "These volumes were retained on uninstall and are kept indefinitely. Review them on the "
                    + "Retained data page and purge any you no longer need — nothing is deleted automatically.",
            },
            cancellationToken).ConfigureAwait(false);
        logger.LogInformation(
            "Retained-volume scan surfaced an informational item: {Count} retained volume(s) older than {ThresholdDays}d.",
            stale.Count, _options.ThresholdDays);
    }

    private static string ComputeSignature(IReadOnlyList<RetainedVolume> stale)
        => string.Join('|', stale
            .Select(r => $"{r.Id:N}:{r.Size}")
            .OrderBy(s => s, StringComparer.Ordinal));

    private string BuildMessage(IReadOnlyList<RetainedVolume> stale)
    {
        var totalBytes = stale.Sum(r => ParseSizeToBytes(r.Size));
        var noun = stale.Count == 1 ? "retained volume" : "retained volumes";
        return $"{stale.Count} {noun} from uninstalled plugins retained more than {_options.ThresholdDays} days, "
            + $"{FormatBytes(totalBytes)}.";
    }

    internal static long ParseSizeToBytes(string? size)
    {
        if (string.IsNullOrWhiteSpace(size))
        {
            return 0;
        }

        var trimmed = size.Trim();
        var i = 0;
        while (i < trimmed.Length && (char.IsDigit(trimmed[i]) || trimmed[i] == '.'))
        {
            i++;
        }

        if (i == 0 || !double.TryParse(trimmed[..i], System.Globalization.NumberStyles.Float,
                System.Globalization.CultureInfo.InvariantCulture, out var value))
        {
            return 0;
        }

        var suffix = trimmed[i..].Trim();
        var multiplier = suffix switch
        {
            "" or "B" => 1d,
            "k" or "K" => 1_000d,
            "M" => 1_000_000d,
            "G" => 1_000_000_000d,
            "T" => 1_000_000_000_000d,
            "Ki" => 1024d,
            "Mi" => 1024d * 1024,
            "Gi" => 1024d * 1024 * 1024,
            "Ti" => 1024d * 1024 * 1024 * 1024,
            _ => 0d,
        };

        return (long)(value * multiplier);
    }

    internal static string FormatBytes(long bytes)
    {
        if (bytes <= 0)
        {
            return "unknown size";
        }

        string[] units = ["B", "KiB", "MiB", "GiB", "TiB", "PiB"];
        double value = bytes;
        var unit = 0;
        while (value >= 1024 && unit < units.Length - 1)
        {
            value /= 1024;
            unit++;
        }

        return unit == 0
            ? $"{bytes} B"
            : $"{value.ToString("0.#", System.Globalization.CultureInfo.InvariantCulture)} {units[unit]}";
    }

}
