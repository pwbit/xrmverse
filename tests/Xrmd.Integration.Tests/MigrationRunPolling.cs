using System.Text.Json.Nodes;
using Xrmd.Admin.Migration;

namespace Xrmd.Integration.Tests;

/// <summary>
/// Polls a migration run's status until the run has settled (docs/migration.md, run states): neither <c>queued</c> nor
/// <c>running</c>, and not <c>completed</c> for a real run, which then finalizes itself (<c>completed</c> is the state a dry run ends
/// in). Shared by the tests that follow a run through the Admin API or the MCP tools.
/// </summary>
internal static class MigrationRunPolling
{
    /// <summary>True when <paramref name="status"/> (the run's status JSON, with <c>state</c> and <c>dryRun</c>) will not change on its own.</summary>
    public static bool IsSettled(JsonNode status)
    {
        ArgumentNullException.ThrowIfNull(status);
        return status["state"]!.GetValue<string>() switch
        {
            MigrationRunStates.Queued or MigrationRunStates.Running => false,
            MigrationRunStates.Completed => status["dryRun"]?.GetValue<bool>() == true,
            _ => true,
        };
    }

    /// <summary>
    /// Reads the status with <paramref name="read"/> every 250 ms until the run has settled or <paramref name="timeout"/> has passed;
    /// returns the last status read.
    /// </summary>
    public static async Task<T> WaitSettledAsync<T>(Func<Task<T>> read, TimeSpan timeout) where T : JsonNode
    {
        ArgumentNullException.ThrowIfNull(read);
        var until = DateTime.UtcNow + timeout;
        while (true)
        {
            await Task.Delay(250);
            var status = await read();
            if (IsSettled(status) || DateTime.UtcNow >= until) return status;
        }
    }
}
