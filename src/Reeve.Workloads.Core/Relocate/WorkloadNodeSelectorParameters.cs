using System;
using System.Collections.Generic;
using System.Linq;
using Reeve.Workloads.Abstractions.Contracts.Deployment;
using Reeve.Workloads.Abstractions.Domain.DesiredSpec;
using Reeve.Workloads.Domain.DesiredSpec;

namespace Reeve.Workloads.Core.Relocate;

/// <summary>Writes a node-selector map back into the instance's placement parameters. The whole map goes into ONE
/// parameter, so a placement is never silently narrowed to whatever happened to fit.</summary>
public static class WorkloadNodeSelectorParameters
{
    /// <summary>Returns a copy of the parameters with the placement rewritten; others are untouched. Every
    /// workload has somewhere to put it: the reserved operator name exists whether or not the manifest declares one.</summary>
    public static IReadOnlyDictionary<string, string?> ApplyNodeSelector(
        IReadOnlyList<WorkloadParameter> parameters,
        IReadOnlyDictionary<string, string?> currentParameters,
        IReadOnlyDictionary<string, string> newNodeSelector)
    {
        ArgumentNullException.ThrowIfNull(parameters);
        ArgumentNullException.ThrowIfNull(currentParameters);
        ArgumentNullException.ThrowIfNull(newNodeSelector);

        var updated = new Dictionary<string, string?>(currentParameters, StringComparer.Ordinal);
        var selectorParamNames = SelectorParameterNames(parameters);

        updated[selectorParamNames[0]] = WorkloadNodeSelectorGrammar.Format(newNodeSelector);
        for (var i = 1; i < selectorParamNames.Count; i++)
            updated[selectorParamNames[i]] = null;

        return updated;
    }

    private static List<string> SelectorParameterNames(IReadOnlyList<WorkloadParameter> parameters)
    {
        var names = parameters
            .Where(p => p.Type == WorkloadParameterType.NodeSelector && !string.IsNullOrWhiteSpace(p.Name))
            .Select(p => p.Name)
            .Distinct(StringComparer.Ordinal)
            .OrderBy(n => n, StringComparer.Ordinal)
            .ToList();

        if (!names.Contains(WorkloadParameterResolver.NodeSelectorParameterName, StringComparer.Ordinal))
            names.Add(WorkloadParameterResolver.NodeSelectorParameterName);

        return names;
    }

    /// <summary>True when the selector round-trips losslessly through the declared parameters. A relocate MUST NOT
    /// delete a PVC when this is false: the encoding drops entries silently and would destroy data.</summary>
    public static bool CanRepresent(
        IReadOnlyList<WorkloadParameter> parameters,
        IReadOnlyDictionary<string, string> newNodeSelector)
    {
        ArgumentNullException.ThrowIfNull(parameters);
        ArgumentNullException.ThrowIfNull(newNodeSelector);

        var encoded = ApplyNodeSelector(parameters, new Dictionary<string, string?>(StringComparer.Ordinal), newNodeSelector);

        var decoded = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var name in SelectorParameterNames(parameters))
        {
            if (!encoded.TryGetValue(name, out var raw))
                continue;

            foreach (var (key, value) in WorkloadNodeSelectorGrammar.Parse(raw))
                decoded[key] = value;
        }

        if (decoded.Count != newNodeSelector.Count)
        {
            return false;
        }

        foreach (var (key, value) in newNodeSelector)
        {
            if (!decoded.TryGetValue(key, out var actual) || !string.Equals(actual, value, StringComparison.Ordinal))
            {
                return false;
            }
        }

        return true;
    }
}
