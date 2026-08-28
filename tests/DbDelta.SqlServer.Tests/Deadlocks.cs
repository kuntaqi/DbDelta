using Microsoft.Data.SqlClient;

namespace DbDelta.SqlServer.Tests;

// Enumerating every database on the instance takes server-level metadata locks, and the collections running
// in parallel spend their time creating and dropping databases — which takes the same locks. SQL Server
// resolves the conflict by killing one side with error 1205 and saying "Rerun the transaction".
//
// So that is what this does, and only around the instance-wide reads: two test classes ask what is on the
// whole instance, and nothing else in the suite is exposed to it. The product is not: it never enumerates
// databases while creating them, so the retry belongs here rather than in the provider.
//
// A pure read is safe to repeat. Nothing in here retries anything that writes.
internal static class Deadlocks
{
    private const int DeadlockVictim = 1205;

    public static async Task<T> RetryingAsync<T>(Func<Task<T>> read, int attempts = 4)
    {
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                return await read();
            }
            catch (SqlException ex) when (ex.Number == DeadlockVictim && attempt < attempts)
            {
                // Short and growing: the DDL that won the deadlock is usually finished within a moment.
                await Task.Delay(100 * attempt);
            }
        }
    }
}
