using System.Text;
using System.Text.RegularExpressions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Xrm.Sdk.Metadata;
using Xrmd.Abstractions;
using Xrmd.Abstractions.Storage;
using Xrmd.Metadata;
using Xrmd.Pipeline.Registration;
using Xrmd.Pipeline.Workflows;
using Xrmd.Security;

namespace Xrmd.Admin.Migration.Import;

/// <summary>
/// Phase 9 of an import (spec §7.6 step 9, plan M4b task 3), after the data:
/// <list type="bullet">
/// <item>autonumber: for each string column of an imported table whose <c>AutoNumberFormat</c> has a <c>{SEQNUM:n}</c>, the
/// imported values that match the format give the highest number; the column's sequence is moved to that number plus 1, and never
/// lowered (<c>post/autonumber</c>);</item>
/// <item>caches: the metadata is reloaded and the security, step and process caches are read again, once (<c>post/caches</c>).</item>
/// </list>
/// </summary>
public sealed class PostProcessingPhase : IMigrationPhase
{
    public const int PhaseOrder = 9;

    public int Order => PhaseOrder;

    public string Name => "post";

    public async Task RunAsync(MigrationPhaseContext context, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(context);
        using var scope = context.Services.CreateScope();
        var services = scope.ServiceProvider;
        var factory = services.GetRequiredService<IDbConnectionFactory>();
        var metadata = services.GetRequiredService<IMetadataStore>();

        if (!await context.Store.IsChunkDoneAsync(context.RunId, "post/autonumber", ct))
        {
            var tables = context.Package.Manifest.Tables.Select(t => t.LogicalName).ToHashSet(StringComparer.Ordinal);
            await using var conn = await factory.OpenAsync(ct);
            foreach (var table in tables)
            {
                if (metadata.GetEntity(table) is not { Attributes: { } attributes } entity) continue;
                foreach (var column in attributes.OfType<StringAttributeMetadata>().Where(a => !string.IsNullOrEmpty(a.AutoNumberFormat)))
                {
                    if (Pattern(column.AutoNumberFormat) is not { } pattern) continue;
                    context.Progress.Report($"post: autonumber {table}.{column.LogicalName}");
                    long? highest = null;
                    var d = SqlDialects.For(conn);
                    await using (var cmd = conn.Command(
                        $"SELECT {d.Quote(column.LogicalName)} FROM {d.DataTable(entity.LogicalName)} WHERE {d.Quote(column.LogicalName)} IS NOT NULL"))
                    {
                        cmd.CommandTimeout = 0;
                        await using var rd = await cmd.ExecuteReaderAsync(ct);
                        while (await rd.ReadAsync(ct))
                            if (pattern.Match(rd.GetString(0)) is { Success: true } m && long.TryParse(m.Groups["n"].Value, out var n) && n > (highest ?? long.MinValue))
                                highest = n;
                    }
                    if (highest is not { } top) continue;
                    long? next = null;
                    await using (var cmd = conn.Command(AutoNumberSequences.EnsureSql(column, SqlDialects.For(conn)) + "\n" + AutoNumberSequences.ReadSql(column, SqlDialects.For(conn))))
                    await using (var rd = await cmd.ExecuteReaderAsync(ct))
                        if (await rd.ReadAsync(ct)) next = rd.GetInt64(1);
                    if (next is { } current && current > top) continue;
                    await using (var cmd = conn.Command(AutoNumberSequences.ResetSql(column, top + 1, SqlDialects.For(conn))))
                        await cmd.ExecuteNonQueryAsync(ct);
                    await context.Ledger.IssueAsync(new MigrationIssue(PhaseOrder, MigrationIssue.Info, "autonumber-seed", table, null, column.LogicalName,
                        $"The autonumber sequence of {table}.{column.LogicalName} now starts at {top + 1}, above the highest imported number {top}."), ct);
                }
            }
            await MarkAsync(factory, context, "post/autonumber", ct);
        }

        if (!await context.Store.IsChunkDoneAsync(context.RunId, "post/caches", ct))
        {
            await metadata.LoadAsync(ct);
            services.GetRequiredService<SecurityCache>().Invalidate();
            services.GetRequiredService<StepRegistry>().Invalidate();
            services.GetService<IWorkflowPipelineHook>()?.Invalidate();
            await MarkAsync(factory, context, "post/caches", ct);
        }
    }

    /// <summary>
    /// The regex matching a value of <paramref name="format"/>, with the sequence number in the group <c>n</c>; null for a format
    /// without <c>{SEQNUM:n}</c> or one that does not parse.
    /// </summary>
    internal static Regex? Pattern(string format)
    {
        try
        {
            if (!AutoNumberFormat.Parse(format).UsesSequence) return null;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return null;
        }
        var regex = new StringBuilder("^");
        var at = 0;
        foreach (Match m in Regex.Matches(format, @"\{(SEQNUM|RANDSTRING|DATETIMEUTC):([^}]*)\}"))
        {
            regex.Append(Regex.Escape(format[at..m.Index]));
            regex.Append(m.Groups[1].Value switch
            {
                "SEQNUM" => @"(?<n>\d+)",
                "RANDSTRING" => int.TryParse(m.Groups[2].Value, out var length) ? $"[A-Z0-9]{{{length}}}" : "[A-Z0-9]+",
                _ => ".+?",
            });
            at = m.Index + m.Length;
        }
        regex.Append(Regex.Escape(format[at..])).Append('$');
        return new Regex(regex.ToString(), RegexOptions.CultureInvariant);
    }

    private static async Task MarkAsync(IDbConnectionFactory factory, MigrationPhaseContext context, string key, CancellationToken ct)
    {
        await using var conn = await factory.OpenAsync(ct);
        await using var tx = conn.BeginTransaction();
        await context.Store.MarkChunkAsync(context.RunId, PhaseOrder, key, 1, 0, new DataSession(conn, tx), ct);
        await tx.CommitAsync(ct);
    }
}
