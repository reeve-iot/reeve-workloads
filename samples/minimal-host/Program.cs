using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Reeve.Workloads.Abstractions;
using Reeve.Workloads.Abstractions.Contracts;
using Reeve.Workloads.Abstractions.Domain.Entities;
using Reeve.Workloads.Abstractions.Domain.Network;
using Reeve.Workloads.Abstractions.Providers;
using Reeve.Workloads.DependencyInjection;
using Reeve.Workloads.Samples.MinimalHost;
using Reeve.Workloads.Testing;
using Manifests = Reeve.Workloads.DeploymentFixtures.DeploymentManifestBuilder;

var runTheEngine = !(args.Contains("--no-engine", StringComparer.Ordinal)
    || string.Equals(Environment.GetEnvironmentVariable("MINIMAL_HOST_NO_ENGINE"), "1", StringComparison.Ordinal));

var postgres = ValueAfter("--postgres") ?? Environment.GetEnvironmentVariable("MINIMAL_HOST_POSTGRES");

const string GroupId = "acme.stack";
const string Version = "1.0.0";

Say($"store         {(postgres is null ? "Entity Framework InMemory (no server, no schema, no migrations)" : "PostgreSQL")}");
Say($"engine        {(runTheEngine ? "will be started (RunReeveWorkloadsAsync)" : "will NOT be started (--no-engine)")}");

var builder = Host.CreateApplicationBuilder(args);

builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?>
{
    ["Workloads:Reconciler:HealthBarrierTimeoutSeconds"] = "5",
    ["Workloads:Reconciler:HealthBarrierDriveSeconds"] = "5",
    ["Workloads:Reconciler:HealthBarrierPollMilliseconds"] = "50",
});

builder.Logging.SetMinimumLevel(LogLevel.Warning);
builder.Logging.AddFilter("Reeve.Workloads", LogLevel.Information);

builder.Services.AddDataProtection().UseEphemeralDataProtectionProvider();

var provider = new InMemoryWorkloadProvider(new WorkloadProviderDescriptor
{
    Id = WorkloadProviderId.Parse("in-memory"),
    Materializes = true,
});
builder.Services.AddSingleton<IWorkloadProvider>(provider);

var manifests = new InMemoryManifestSource();

var workloads = builder.Services
    .AddReeveWorkloads(builder.Configuration, ports => ports
        .Use<IWorkloadGroupManifestSource>(_ => manifests, ServiceLifetime.Singleton)
        .Use<IWorkloadOrchestratorSettingsSource>(
            _ => new JsonFileSettingsSource(builder.Configuration["MinimalHost:SettingsFile"] ?? "workload-settings.json"),
            ServiceLifetime.Singleton))
    .AddEngine()
    .AddEntityFrameworkStores(options =>
    {
        if (postgres is null)
        {
            options.UseInMemoryDatabase("minimal-host");
        }
        else
        {
            options.UseNpgsql(postgres);
        }
    })
    .ValidateHostPorts();

Say("ports standing on a library default:");
foreach (var port in workloads.FindHostPortsOnLibraryDefaults().OrderBy(p => p.Name, StringComparer.Ordinal))
{
    Say($"  {port.Name}");
}

var app = builder.Build();

if (runTheEngine)
{
    await app.Services.RunReeveWorkloadsAsync(runMigrations: postgres is not null);
    Say("engine started");
}

await app.StartAsync();

if (!runTheEngine)
{
    Say("application started with no engine — the start guard has had its say");
    await app.WaitForShutdownAsync();
    Say("the process stopped itself. A host that composes the engine and never runs it does not boot green.");
    return 1;
}

manifests.Publish(GroupId, Version, Manifests.Manifest(Manifests.Workload("db"), Manifests.Workload("api")));

var groupInstanceId = Guid.NewGuid();
Say($"installing {GroupId} {Version} as {groupInstanceId}");

await using (var scope = app.Services.CreateAsyncScope())
{
    var requirements = await scope.ServiceProvider.GetRequiredService<IWorkloadRequirementsService>()
        .ForInstallAsync(new WorkloadInstallRequirementsQuery
        {
            GroupId = GroupId,
            SourceKey = HostPorts.SourceKey,
            Version = Version,
        });

    if (requirements.Read is not { } read)
    {
        Say($"the install has nothing to judge: {requirements.Status} {requirements.Message}");
        await app.StopAsync();
        return 1;
    }

    var installed = await scope.ServiceProvider.GetRequiredService<IWorkloadLifecycleService>()
        .MaterializeAsync(new WorkloadGroupInstall
        {
            GroupInstanceId = groupInstanceId,
            GroupId = GroupId,
            SourceKey = HostPorts.SourceKey,
            Version = Version,
            AttachmentPins = new Dictionary<string, WorkloadAttachmentPin>(StringComparer.Ordinal),
            RequirementsFingerprint = read.Fingerprint,
        });

    Say($"install outcome {installed.Outcome}");
}

Say("install done — watching the rows settle");

var printed = new Dictionary<Guid, string>();
var deadline = DateTime.UtcNow.AddSeconds(30);
var settled = false;

while (DateTime.UtcNow < deadline && !settled)
{
    await using var scope = app.Services.CreateAsyncScope();
    var instances = await scope.ServiceProvider.GetRequiredService<IWorkloadInstanceRepository>()
        .ListByPluginInstanceAsync(groupInstanceId, CancellationToken.None);

    foreach (var instance in instances)
    {
        var line = $"{instance.WorkloadId,-4} {instance.State}/{instance.StateReason}"
            + $" applies={provider.ApplyCountFor(instance.Id)}";
        if (printed.GetValueOrDefault(instance.Id) != line)
        {
            printed[instance.Id] = line;
            Say($"  {line}");
        }

        if (provider.IsTracked(instance.Id))
        {
            provider.SimulateReady(instance.Id);
        }
    }

    settled = instances.Count > 0 && instances.All(i => i.State == WorkloadInstanceState.Healthy);
    if (!settled)
    {
        await Task.Delay(TimeSpan.FromMilliseconds(200));
    }
}

Say(settled
    ? "every workload of the module is Healthy — the engine deployed them, and nothing here drove it"
    : "the workloads did not all reach Healthy within 30s; the lines above are how far they got");

await app.StopAsync();
Say("stopped. The engine drained its units and resigned its leadership before this line returned.");

return settled ? 0 : 1;

static void Say(string text) => Console.WriteLine($"[sample] {text}");

string? ValueAfter(string flag)
{
    var at = Array.IndexOf(args, flag);
    return at >= 0 && at + 1 < args.Length ? args[at + 1] : null;
}
