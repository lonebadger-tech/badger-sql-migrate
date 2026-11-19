using System.Globalization;
using Badger.SqlMigrate.Core.Conversion;
using Badger.SqlMigrate.Core.Providers;
using Badger.SqlMigrate.Providers.SqlServer.Scripting;

namespace Badger.SqlMigrate.Providers.SqlServer.Conversion;

/// <summary>
/// Reads a SQL Server database into a <see cref="PortableSchema"/> through the scripter's catalog model, plus trigger
/// parents and MS_Description comments. What has no counterpart elsewhere (partitioning, temporal periods, Always
/// Encrypted, masks, table types, synonyms, XML/spatial/full-text indexes) is reported as a warning.
/// </summary>
public sealed class SqlServerExporter : ISchemaExporter
{
    public async Task<PortableSchema> ExportAsync(IDatabaseSession session, ExportOptions options, CancellationToken cancellationToken)
    {
        var model = await SqlServerModel.ReadAsync(session, options.StateSchema, n => options.Excluded(new PortableName(n.Schema, n.Name)), cancellationToken).ConfigureAwait(false);
        var schema = new PortableSchema
        {
            SourceProvider = SqlServerProvider.ProviderId,
            DefaultSchema = "dbo",
            Collation = model.DefaultCollation,
            CaseInsensitive = model.DefaultCollation.Contains("_CI", StringComparison.OrdinalIgnoreCase),
        };
        schema.Schemas.AddRange(model.Schemas.Keys.Where(s => !string.Equals(s, "dbo", StringComparison.OrdinalIgnoreCase)));

        var comments = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var r in await session.QueryAsync(CommentsSql, null, cancellationToken).ConfigureAwait(false))
        {
            comments[$"{r["schema_name"]}.{r["table_name"]}.{r["column_name"]}"] = Convert.ToString(r["value"], CultureInfo.InvariantCulture) ?? string.Empty;
        }

        foreach (var table in model.Tables.Values.OrderBy(t => t.Name.Schema, StringComparer.OrdinalIgnoreCase).ThenBy(t => t.Name.Name, StringComparer.OrdinalIgnoreCase))
        {
            var name = new PortableName(table.Name.Schema, table.Name.Name);
            var columns = new List<PortableColumn>();
            foreach (var c in table.Columns)
            {
                if (c.GeneratedAlways is not null)
                {
                    schema.Warnings.Add($"{name}.{c.Name}: system-versioning period column ({c.GeneratedAlways}) converted as a plain column; the history is not maintained.");
                }

                if (c.Encryption is not null)
                {
                    schema.Warnings.Add($"{name}.{c.Name}: Always Encrypted column; it is converted as {c.Type} and its encrypted values cannot be copied.");
                }

                if (c.Mask is not null)
                {
                    schema.Warnings.Add($"{name}.{c.Name}: dynamic data mask {c.Mask} is not converted.");
                }

                columns.Add(new PortableColumn(c.Name, c.Type, c.Nullable)
                {
                    Default = c.Default?.Definition,
                    Identity = c.Identity is { } id ? new PortableIdentity(Long(id.Seed), Long(id.Increment), Always: false) : null,
                    Computed = c.Computed,
                    ComputedStored = c.Persisted,
                    Collation = c.Collation,
                    Comment = comments.GetValueOrDefault($"{name}.{c.Name}"),
                });
            }

            if (table.Temporal is not null)
            {
                schema.Warnings.Add($"{name}: system-versioned (temporal) table converted as a plain table.");
            }

            if (table.DataSpace?.PartitionColumn is not null)
            {
                schema.Warnings.Add($"{name}: partitioning is not converted.");
            }

            schema.Warnings.AddRange(table.Unsupported.Select(u => $"{name}: {u} is not converted."));

            schema.Tables.Add(new PortableTable(
                name,
                columns,
                table.PrimaryKey is { } pk ? new PortableKey(pk.Name, pk.Columns.Select(k => k.Name).ToList()) : null,
                table.Uniques.Select(u => new PortableKey(u.Name, u.Columns.Select(k => k.Name).ToList())).ToList(),
                table.Checks.Select(c => new PortableCheck(c.Name, c.Definition)).ToList(),
                table.ForeignKeys.Select(f => new PortableForeignKey(f.Name, f.Columns, new PortableName(f.Referenced.Schema, f.Referenced.Name), f.ReferencedColumns, f.OnDelete, f.OnUpdate)).ToList(),
                table.Indexes.Select(i => new PortableIndex(
                    i.Name,
                    i.Unique,
                    i.Columns.Select(k => new PortableIndexColumn(k.Name, false, k.Descending)).ToList(),
                    i.Included,
                    i.Filter,
                    i.Kind.Contains("COLUMNSTORE", StringComparison.OrdinalIgnoreCase) ? "columnstore" : null)).ToList())
            {
                Comment = comments.GetValueOrDefault($"{name}."),
            });
        }

        foreach (var sequence in model.Sequences.Values)
        {
            schema.Sequences.Add(new PortableSequence(new PortableName(sequence.Name.Schema, sequence.Name.Name), sequence.Type, Long(sequence.Start), Long(sequence.Increment),
                Long(sequence.Minimum), Long(sequence.Maximum), sequence.Cycle));
        }

        foreach (var alias in model.AliasTypes.Values)
        {
            schema.Domains.Add(new PortableDomain(new PortableName(alias.Name.Schema, alias.Name.Name), alias.BaseType, alias.Nullable, null, []));
        }

        schema.Warnings.AddRange(model.TableTypes.Values.Select(t => $"table type {t.Name} is not converted (PostgreSQL has no table-valued parameters; use arrays or a temporary table)."));
        schema.Warnings.AddRange(model.Synonyms.Values.Select(s => $"synonym {s.Name} -> {s.Target} is not converted."));

        var triggerTables = new Dictionary<string, PortableName>(StringComparer.OrdinalIgnoreCase);
        foreach (var r in await session.QueryAsync(TriggersSql, null, cancellationToken).ConfigureAwait(false))
        {
            triggerTables[$"{r["schema_name"]}.{r["name"]}"] = new PortableName((string)r["table_schema"]!, (string)r["table_name"]!);
        }

        var resultColumns = (await session.QueryAsync(FunctionColumnsSql, null, cancellationToken).ConfigureAwait(false))
            .GroupBy(r => $"{r["schema_name"]}.{r["function_name"]}", StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.OrderBy(r => Convert.ToInt32(r["column_id"], CultureInfo.InvariantCulture))
                .Select(r => new PortableColumn((string)r["name"]!, SqlServerModel.TypeName((string)r["type_schema"]!, (string)r["type_name"]!, r["is_user_defined"] is true,
                    Convert.ToInt32(r["max_length"], CultureInfo.InvariantCulture), Convert.ToInt32(r["precision"], CultureInfo.InvariantCulture), Convert.ToInt32(r["scale"], CultureInfo.InvariantCulture)), r["is_nullable"] is true)).ToList(),
                StringComparer.OrdinalIgnoreCase);

        var code = new List<PortableCode>();
        foreach (var module in model.Modules.Values.OrderBy(m => m.Name.Schema, StringComparer.OrdinalIgnoreCase).ThenBy(m => m.Name.Name, StringComparer.OrdinalIgnoreCase))
        {
            var name = new PortableName(module.Name.Schema, module.Name.Name);
            var kind = module.Type switch
            {
                "V" => PortableCodeKind.View,
                "P" => PortableCodeKind.Procedure,
                "TR" => PortableCodeKind.Trigger,
                _ => PortableCodeKind.Function,
            };
            code.Add(new PortableCode(kind, name, module.Definition)
            {
                Language = "tsql",
                Table = kind == PortableCodeKind.Trigger ? triggerTables.GetValueOrDefault(name.ToString()) : null,
                DependsOn = module.References.Where(r => !string.Equals(r, module.Name.Key, StringComparison.Ordinal)).ToList(),
                ResultColumns = resultColumns.GetValueOrDefault(name.ToString()) ?? [],
            });
        }

        schema.Code.AddRange(CodeOrder.Sort(code));
        return schema;
    }

    private static long Long(string? value) =>
        decimal.TryParse(value, NumberStyles.Number, CultureInfo.InvariantCulture, out var d) ? (long)Math.Clamp(d, long.MinValue, long.MaxValue) : 0;

    private const string TriggersSql = """
        SELECT s.name AS schema_name, tr.name, ps.name AS table_schema, p.name AS table_name
        FROM sys.triggers tr
        JOIN sys.objects o ON o.object_id = tr.object_id
        JOIN sys.schemas s ON s.schema_id = o.schema_id
        JOIN sys.objects p ON p.object_id = tr.parent_id
        JOIN sys.schemas ps ON ps.schema_id = p.schema_id
        WHERE tr.parent_class = 1;
        """;

    /// <summary>Result columns of table-valued functions (inline and multi-statement).</summary>
    private const string FunctionColumnsSql = """
        SELECT s.name AS schema_name, o.name AS function_name, c.column_id, c.name, ts.name AS type_schema, t.name AS type_name, t.is_user_defined,
               c.max_length, c.precision, c.scale, c.is_nullable
        FROM sys.columns c
        JOIN sys.objects o ON o.object_id = c.object_id
        JOIN sys.schemas s ON s.schema_id = o.schema_id
        JOIN sys.types t ON t.user_type_id = c.user_type_id
        JOIN sys.schemas ts ON ts.schema_id = t.schema_id
        WHERE o.type IN ('IF', 'TF') AND o.is_ms_shipped = 0;
        """;

    /// <summary>MS_Description on tables (column_name empty) and columns.</summary>
    private const string CommentsSql = """
        SELECT s.name AS schema_name, o.name AS table_name, COALESCE(c.name, '') AS column_name, CONVERT(nvarchar(4000), ep.value) AS value
        FROM sys.extended_properties ep
        JOIN sys.objects o ON o.object_id = ep.major_id
        JOIN sys.schemas s ON s.schema_id = o.schema_id
        LEFT JOIN sys.columns c ON c.object_id = ep.major_id AND c.column_id = ep.minor_id
        WHERE ep.class = 1 AND ep.name = 'MS_Description' AND o.type = 'U';
        """;
}
