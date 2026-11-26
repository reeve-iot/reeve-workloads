using System.Data.Common;
using Microsoft.EntityFrameworkCore;

namespace Reeve.Workloads.EntityFrameworkCore;

internal static class DatabaseContention
{
    private const string SerializationFailure = "40001";
    private const string DeadlockDetected = "40P01";

    public static bool IsSerializationFailureOrDeadlock(Exception exception)
    {
        for (Exception? current = exception; current is not null; current = current.InnerException)
        {
            if (current is DbException { SqlState: SerializationFailure or DeadlockDetected })
            {
                return true;
            }
        }

        return false;
    }

    public static bool IsLostOptimisticRace(Exception exception) =>
        exception is DbUpdateConcurrencyException && !IsSerializationFailureOrDeadlock(exception);
}
