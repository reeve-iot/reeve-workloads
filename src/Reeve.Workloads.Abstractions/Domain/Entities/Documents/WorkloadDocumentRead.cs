namespace Reeve.Workloads.Abstractions.Domain.Entities.Documents;

/// <summary>Reads a value from the typed document that holds it. Every read site follows these two rules, so what
/// "the document cannot answer" means is decided in a single place.</summary>
/// <remarks>A document that cannot be read is the end of the line. Two kinds of caller need different things from
/// that: most degrade (an empty map, a null), and two classify — see <see cref="Decide"/>.</remarks>
public static class WorkloadDocumentRead
{
    /// <summary>The document's value, or what <paramref name="whenUnreadable"/> returns: the caller's own
    /// degradation, applied when there is no document, when it records the column it replaced as undecodable, or
    /// when its inverse faults.</summary>
    /// <param name="document">The typed document, or null where the row has none.</param>
    /// <param name="fromDocument">The document's inverse. May throw; a throw degrades, it never faults.</param>
    /// <param name="whenUnreadable">What this read is worth when the document cannot answer.</param>
    public static TValue Read<TDocument, TValue>(
        TDocument? document,
        Func<TDocument, TValue> fromDocument,
        Func<TValue> whenUnreadable)
        where TDocument : class, IWorkloadJsonDocument
    {
        ArgumentNullException.ThrowIfNull(fromDocument);
        ArgumentNullException.ThrowIfNull(whenUnreadable);

        if (document is null || document.Unreadable)
        {
            return whenUnreadable();
        }

        try
        {
            return fromDocument(document);
        }
        catch (Exception ex) when (ex is FormatException or ArgumentException or NullReferenceException
            or InvalidOperationException or KeyNotFoundException or OverflowException)
        {
            return whenUnreadable();
        }
    }

    /// <summary>The same read for a caller whose "cannot read it" is a DECISION -- terminalize the intent, compute
    /// the desired spec fresh -- rather than a degradation to swallow. It decides nothing itself.</summary>
    /// <param name="document">The typed document, or null where the row has none.</param>
    /// <param name="fromDocument">The document's inverse. A throw is reported as unreadable, never faulted.</param>
    public static WorkloadDocumentReading<TValue> Decide<TDocument, TValue>(
        TDocument? document,
        Func<TDocument, TValue> fromDocument)
        where TDocument : class, IWorkloadJsonDocument
    {
        ArgumentNullException.ThrowIfNull(fromDocument);

        if (document is null)
        {
            return new(WorkloadDocumentVerdict.Absent, default);
        }

        if (document.Unreadable)
        {
            return new(WorkloadDocumentVerdict.Unreadable, default);
        }

        try
        {
            return new(WorkloadDocumentVerdict.Value, fromDocument(document));
        }
        catch (Exception ex) when (ex is FormatException or ArgumentException or NullReferenceException
            or InvalidOperationException or KeyNotFoundException or OverflowException)
        {
            return new(WorkloadDocumentVerdict.Unreadable, default);
        }
    }
}

/// <summary>What a read site learned from the document.</summary>
public enum WorkloadDocumentVerdict
{
    /// <summary>The row has no document: nothing was ever recorded here.</summary>
    Absent,

    /// <summary>Something is recorded and cannot be read -- the document says so outright, or its inverse faults.
    /// This is the only carrier of that fact.</summary>
    Unreadable,

    /// <summary>The document was read, and <see cref="WorkloadDocumentReading{TValue}.Value"/> holds it.</summary>
    Value,
}

/// <summary>A <see cref="WorkloadDocumentRead.Decide"/> outcome. <see cref="Value"/> is meaningful only when
/// <see cref="Verdict"/> is <see cref="WorkloadDocumentVerdict.Value"/>.</summary>
public readonly record struct WorkloadDocumentReading<TValue>(WorkloadDocumentVerdict Verdict, TValue? Value);
