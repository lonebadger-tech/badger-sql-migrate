using System.Runtime.CompilerServices;
using Badger.SqlMigrate.Core;
using Badger.SqlMigrate.Core.Conversion;
using Badger.SqlMigrate.Core.Providers;

namespace Badger.SqlMigrate.Conversion;

public sealed record CopyOptions
{
    /// <summary>schema.name globs (source names) of tables to copy; empty = all.</summary>
    public Func<PortableName, bool> Include { get; init; } = _ => true;

    /// <summary>Empty the target tables first (otherwise they must be empty).</summary>
    public bool Truncate { get; init; }
}

/// <param name="Skipped">Source columns with no column of the mapped name in the target.</param>
public sealed record TableCopy(PortableName Source, PortableName Target, long SourceRows, long TargetRows, IReadOnlyList<string> Skipped);

/// <summary>
/// Copies every table's rows from the source to the converted target: parents before children, names mapped the same
/// way 'convert schema' mapped them, and row counts compared afterwards.
/// </summary>
public static class DataCopier
{
    public static async Task<IReadOnlyList<TableCopy>> CopyAsync(
        ConversionContext ctx,
        IDataTransfer source,
        IDatabaseSession sourceSession,
        IDataTransfer target,
        IDatabaseSession targetSession,
        CopyOptions options,
        Action<string> progress,
        CancellationToken cancellationToken)
    {
        var tables = Order(ctx.Schema.Tables.Where(t => options.Include(t.Name)).ToList());
        var targets = tables.Select(t => new PortableName(ctx.Names.Schema(t.Name.Schema), ctx.Names.Name(t.Name.Name))).ToList();

        // Every target table must exist, and be empty unless --truncate.
        var plans = new List<(PortableTable Table, PortableName Target, List<TransferColumn> From, List<TransferColumn> To, List<string> Skipped)>();
        for (var i = 0; i < tables.Count; i++)
        {
            var from = await source.ColumnsAsync(sourceSession, tables[i].Name, cancellationToken).ConfigureAwait(false);
            var to = await target.ColumnsAsync(targetSession, targets[i], cancellationToken).ConfigureAwait(false);
            if (to.Count == 0)
            {
                throw new MigrationException($"{targets[i]} does not exist in the target. Deploy the converted project first ('bsm deploy'), with the same --names as 'convert schema'.");
            }

            var byName = to.ToDictionary(c => c.Name, StringComparer.OrdinalIgnoreCase);
            var pairs = from.Select(c => (Source: c, Target: byName.GetValueOrDefault(ctx.Names.Name(c.Name)))).ToList();
            plans.Add((tables[i], targets[i], pairs.Where(p => p.Target is not null).Select(p => p.Source).ToList(), pairs.Where(p => p.Target is not null).Select(p => p.Target!).ToList(),
                pairs.Where(p => p.Target is null).Select(p => p.Source.Name).ToList()));
        }

        if (options.Truncate)
        {
            await target.TruncateAsync(targetSession, Enumerable.Reverse(targets).ToList(), cancellationToken).ConfigureAwait(false);
        }
        else
        {
            foreach (var t in targets)
            {
                if (await target.CountAsync(targetSession, t, cancellationToken).ConfigureAwait(false) > 0)
                {
                    throw new MigrationException($"{t} already has rows. Pass --truncate to empty the target tables first.");
                }
            }
        }

        var results = new List<TableCopy>();
        await target.BeginLoadAsync(targetSession, targets, cancellationToken).ConfigureAwait(false);
        try
        {
            foreach (var (table, to, fromColumns, toColumns, skipped) in plans)
            {
                progress($"{table.Name} -> {to}");
                var rows = source.ReadAsync(sourceSession, table.Name, fromColumns, cancellationToken);
                var written = await target.WriteAsync(targetSession, to, toColumns, Arrays(rows, cancellationToken), cancellationToken).ConfigureAwait(false);
                results.Add(new TableCopy(table.Name, to, -1, written, skipped));
            }

            await target.EndLoadAsync(targetSession, targets, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            if (targetSession.InTransaction)
            {
                await targetSession.RollbackAsync(CancellationToken.None).ConfigureAwait(false);
            }

            if (ex is System.Data.Common.DbException db)
            {
                throw new MigrationException($"Copying data failed{(targetSession.InTransaction ? string.Empty : " (rows already loaded stay; rerun with --truncate)")}: {db.Message}", db);
            }

            throw;
        }

        // Compare row counts.
        for (var i = 0; i < results.Count; i++)
        {
            var r = results[i];
            results[i] = r with
            {
                SourceRows = await source.CountAsync(sourceSession, r.Source, cancellationToken).ConfigureAwait(false),
                TargetRows = await target.CountAsync(targetSession, r.Target, cancellationToken).ConfigureAwait(false),
            };
        }

        return results;
    }

    /// <summary>Arrays (PostgreSQL) arrive as JSON text already; anything else enumerable is serialized the same way.</summary>
    private static async IAsyncEnumerable<object?[]> Arrays(IAsyncEnumerable<object?[]> rows, [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        await foreach (var row in rows.WithCancellation(cancellationToken).ConfigureAwait(false))
        {
            for (var i = 0; i < row.Length; i++)
            {
                if (row[i] is System.Collections.IEnumerable e and not string and not byte[])
                {
                    row[i] = System.Text.Json.JsonSerializer.Serialize(e);
                }
            }

            yield return row;
        }
    }

    /// <summary>Parents before children (by foreign key); tables in a cycle keep their order.</summary>
    public static List<PortableTable> Order(List<PortableTable> tables)
    {
        var byKey = tables.ToDictionary(t => t.Name.Key);
        var result = new List<PortableTable>();
        var state = new Dictionary<string, int>();

        void Visit(PortableTable t)
        {
            if (state.ContainsKey(t.Name.Key))
            {
                return;
            }

            state[t.Name.Key] = 1;
            foreach (var fk in t.ForeignKeys)
            {
                if (byKey.TryGetValue(fk.Referenced.Key, out var parent) && !state.ContainsKey(parent.Name.Key))
                {
                    Visit(parent);
                }
            }

            state[t.Name.Key] = 2;
            result.Add(t);
        }

        foreach (var t in tables)
        {
            Visit(t);
        }

        return result;
    }
}
