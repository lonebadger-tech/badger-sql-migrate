using Badger.SqlMigrate.Core.Providers;

namespace Badger.SqlMigrate.Providers.SqlServer;

/// <summary>
/// Canonical text dump of user schema objects, sorted ordinally, used for the schema fingerprint.
/// System-generated constraint names (DF__Orders__Statu__3B75D760) differ between servers, so they are
/// replaced with a placeholder; otherwise identical schemas would never fingerprint the same.
/// Always Encrypted columns record the CEK name but never key paths or values, which differ per environment,
/// so identical schemas fingerprint the same in every environment. The bsm state schema is excluded. Catalog views mix collations, so each line is forced to a binary collation.
/// System-versioned (temporal) tables record their period columns, history table and retention; a history table SQL Server
/// named itself (MSSQL_TemporalHistoryFor_&lt;object id&gt;) is renamed after its table, because the id differs per server.
/// Partitioning records functions (type, direction, boundaries), schemes (filegroups) and each partitioned index's
/// scheme and column. Lines for these features appear only when they are used, so other schemas fingerprint as before.
/// </summary>
public sealed partial class SqlServerSchemaInspector : ISchemaInspector
{
    private const string Sql = """
        SELECT CONCAT(N'schema|' COLLATE Latin1_General_BIN2, s.name) AS line
        FROM sys.schemas s
        WHERE s.schema_id > 4 AND s.schema_id < 16384 AND s.name <> @state

        UNION ALL
        SELECT CONCAT(N'column|' COLLATE Latin1_General_BIN2, s.name, '.', o.name, '|', ROW_NUMBER() OVER (PARTITION BY c.object_id ORDER BY c.column_id), '|', c.name, '|', TYPE_NAME(c.user_type_id),
               '|', c.max_length, '|', c.precision, '|', c.scale, '|', c.is_nullable, '|', c.is_identity,
               '|', ISNULL(c.collation_name, ''), '|', ISNULL(cc.definition, ''), '|', ISNULL(dc.definition, ''),
               '|', CASE WHEN dc.is_system_named = 1 THEN '<system>' ELSE ISNULL(dc.name, '') END,
               '|', c.is_sparse, '|', c.is_rowguidcol, '|', ISNULL(c.encryption_type_desc, ''), '|', ISNULL(c.encryption_algorithm_name, ''), '|', ISNULL(cek.name, ''),
               CASE WHEN c.generated_always_type <> 0 THEN CONCAT('|', c.generated_always_type_desc, CASE WHEN c.is_hidden = 1 THEN '|hidden' ELSE '' END) ELSE '' END)
        FROM sys.columns c
        JOIN sys.objects o ON o.object_id = c.object_id
        JOIN sys.schemas s ON s.schema_id = o.schema_id
        LEFT JOIN sys.computed_columns cc ON cc.object_id = c.object_id AND cc.column_id = c.column_id
        LEFT JOIN sys.default_constraints dc ON dc.object_id = c.default_object_id
        LEFT JOIN sys.column_encryption_keys cek ON cek.column_encryption_key_id = c.column_encryption_key_id
        WHERE o.type IN ('U', 'TT') AND o.is_ms_shipped = 0 AND s.name <> @state

        UNION ALL
        SELECT CONCAT(N'index|' COLLATE Latin1_General_BIN2, s.name, '.', o.name, '|',
               CASE WHEN kc.is_system_named = 1 THEN '<system>' ELSE i.name END,
               '|', i.type_desc, '|', i.is_unique, '|', i.is_primary_key, '|', i.is_unique_constraint, '|', ISNULL(i.filter_definition, ''),
               '|', STUFF((SELECT CONCAT(',', c.name, CASE WHEN ic.is_descending_key = 1 THEN ' DESC' ELSE '' END, CASE WHEN ic.is_included_column = 1 THEN ' INCLUDE' ELSE '' END)
                           FROM sys.index_columns ic JOIN sys.columns c ON c.object_id = ic.object_id AND c.column_id = ic.column_id
                           WHERE ic.object_id = i.object_id AND ic.index_id = i.index_id
                           ORDER BY ic.is_included_column, ic.key_ordinal, c.name
                           FOR XML PATH(''), TYPE).value('.', 'nvarchar(max)'), 1, 1, ''))
        FROM sys.indexes i
        JOIN sys.objects o ON o.object_id = i.object_id
        JOIN sys.schemas s ON s.schema_id = o.schema_id
        LEFT JOIN sys.key_constraints kc ON kc.parent_object_id = i.object_id AND kc.unique_index_id = i.index_id
        WHERE i.type > 0 AND o.is_ms_shipped = 0 AND o.type IN ('U', 'V') AND s.name <> @state

        UNION ALL
        SELECT CONCAT(N'fk|' COLLATE Latin1_General_BIN2, s.name, '.', o.name, '|', CASE WHEN fk.is_system_named = 1 THEN '<system>' ELSE fk.name END,
               '|', rs.name, '.', ro.name, '|', fk.delete_referential_action_desc, '|', fk.update_referential_action_desc,
               '|', fk.is_disabled, '|', fk.is_not_trusted,
               '|', STUFF((SELECT CONCAT(',', pc.name, '->', rc.name)
                           FROM sys.foreign_key_columns fkc
                           JOIN sys.columns pc ON pc.object_id = fkc.parent_object_id AND pc.column_id = fkc.parent_column_id
                           JOIN sys.columns rc ON rc.object_id = fkc.referenced_object_id AND rc.column_id = fkc.referenced_column_id
                           WHERE fkc.constraint_object_id = fk.object_id ORDER BY fkc.constraint_column_id
                           FOR XML PATH(''), TYPE).value('.', 'nvarchar(max)'), 1, 1, ''))
        FROM sys.foreign_keys fk
        JOIN sys.objects o ON o.object_id = fk.parent_object_id
        JOIN sys.schemas s ON s.schema_id = o.schema_id
        JOIN sys.objects ro ON ro.object_id = fk.referenced_object_id
        JOIN sys.schemas rs ON rs.schema_id = ro.schema_id
        WHERE s.name <> @state

        UNION ALL
        SELECT CONCAT(N'check|' COLLATE Latin1_General_BIN2, s.name, '.', o.name, '|', CASE WHEN ck.is_system_named = 1 THEN '<system>' ELSE ck.name END, '|', ck.definition, '|', ck.is_disabled)
        FROM sys.check_constraints ck
        JOIN sys.objects o ON o.object_id = ck.parent_object_id
        JOIN sys.schemas s ON s.schema_id = o.schema_id
        WHERE s.name <> @state

        UNION ALL
        SELECT CONCAT(N'module|' COLLATE Latin1_General_BIN2, o.type, '|', s.name, '.', o.name, '|', m.definition)
        FROM sys.sql_modules m
        JOIN sys.objects o ON o.object_id = m.object_id
        JOIN sys.schemas s ON s.schema_id = o.schema_id
        WHERE o.is_ms_shipped = 0 AND s.name <> @state

        UNION ALL
        SELECT CONCAT(N'type|' COLLATE Latin1_General_BIN2, s.name, '.', t.name, '|', TYPE_NAME(t.system_type_id), '|', t.max_length, '|', t.precision, '|', t.scale, '|', t.is_nullable, '|', t.is_table_type)
        FROM sys.types t
        JOIN sys.schemas s ON s.schema_id = t.schema_id
        WHERE t.is_user_defined = 1 AND s.name <> @state

        UNION ALL
        SELECT CONCAT(N'sequence|' COLLATE Latin1_General_BIN2, s.name, '.', sq.name, '|', TYPE_NAME(sq.user_type_id), '|', CAST(sq.start_value AS nvarchar(64)), '|', CAST(sq.increment AS nvarchar(64)), '|', sq.is_cycling)
        FROM sys.sequences sq
        JOIN sys.schemas s ON s.schema_id = sq.schema_id
        WHERE s.name <> @state

        UNION ALL
        SELECT CONCAT(N'synonym|' COLLATE Latin1_General_BIN2, s.name, '.', sy.name, '|', sy.base_object_name)
        FROM sys.synonyms sy
        JOIN sys.schemas s ON s.schema_id = sy.schema_id
        WHERE s.name <> @state

        UNION ALL
        SELECT CONCAT(N'temporal|' COLLATE Latin1_General_BIN2, s.name, '.', t.name, '|', t.temporal_type_desc, '|', sc.name, '|', ec.name,
               '|', ISNULL(hs.name + '.' + h.name, ''), '|', ISNULL(CONVERT(nvarchar(20), t.history_retention_period), ''), '|', ISNULL(t.history_retention_period_unit_desc, ''))
        FROM sys.periods p
        JOIN sys.tables t ON t.object_id = p.object_id
        JOIN sys.schemas s ON s.schema_id = t.schema_id
        JOIN sys.columns sc ON sc.object_id = p.object_id AND sc.column_id = p.start_column_id
        JOIN sys.columns ec ON ec.object_id = p.object_id AND ec.column_id = p.end_column_id
        LEFT JOIN sys.tables h ON h.object_id = t.history_table_id
        LEFT JOIN sys.schemas hs ON hs.schema_id = h.schema_id
        WHERE s.name <> @state

        UNION ALL
        SELECT CONCAT(N'pfunction|' COLLATE Latin1_General_BIN2, pf.name, '|', TYPE_NAME(pp.user_type_id), '|', pp.max_length, '|', pp.precision, '|', pp.scale,
               '|', ISNULL(pp.collation_name, ''), '|', CASE WHEN pf.boundary_value_on_right = 1 THEN 'RIGHT' ELSE 'LEFT' END,
               CASE WHEN @boundaries = 1 THEN CONCAT('|', STUFF((SELECT CONCAT(',', CONVERT(nvarchar(4000), v.value, 126))
                           FROM sys.partition_range_values v WHERE v.function_id = pf.function_id ORDER BY v.boundary_id
                           FOR XML PATH(''), TYPE).value('.', 'nvarchar(max)'), 1, 1, '')) ELSE '' END)
        FROM sys.partition_functions pf
        JOIN sys.partition_parameters pp ON pp.function_id = pf.function_id

        UNION ALL
        SELECT CONCAT(N'pscheme|' COLLATE Latin1_General_BIN2, ps.name, '|', pf.name, '|',
               CASE WHEN @boundaries = 1
                    THEN STUFF((SELECT CONCAT(',', fg.name) FROM sys.destination_data_spaces d JOIN sys.filegroups fg ON fg.data_space_id = d.data_space_id
                                WHERE d.partition_scheme_id = ps.data_space_id ORDER BY d.destination_id
                                FOR XML PATH(''), TYPE).value('.', 'nvarchar(max)'), 1, 1, '')
                    ELSE STUFF((SELECT CONCAT(',', q.name) FROM (SELECT DISTINCT fg.name FROM sys.destination_data_spaces d JOIN sys.filegroups fg ON fg.data_space_id = d.data_space_id
                                WHERE d.partition_scheme_id = ps.data_space_id) q ORDER BY q.name
                                FOR XML PATH(''), TYPE).value('.', 'nvarchar(max)'), 1, 1, '') END)
        FROM sys.partition_schemes ps
        JOIN sys.partition_functions pf ON pf.function_id = ps.function_id

        UNION ALL
        SELECT CONCAT(N'partitioned|' COLLATE Latin1_General_BIN2, s.name, '.', o.name, '|',
               CASE WHEN i.index_id = 0 THEN '<heap>' WHEN kc.is_system_named = 1 THEN '<system>' ELSE i.name END, '|', ps.name, '|', c.name)
        FROM sys.indexes i
        JOIN sys.partition_schemes ps ON ps.data_space_id = i.data_space_id
        JOIN sys.objects o ON o.object_id = i.object_id
        JOIN sys.schemas s ON s.schema_id = o.schema_id
        JOIN sys.index_columns ic ON ic.object_id = i.object_id AND ic.index_id = i.index_id AND ic.partition_ordinal = 1
        JOIN sys.columns c ON c.object_id = ic.object_id AND c.column_id = ic.column_id
        LEFT JOIN sys.key_constraints kc ON kc.parent_object_id = i.object_id AND kc.unique_index_id = i.index_id
        WHERE o.is_ms_shipped = 0 AND s.name <> @state

        UNION ALL
        SELECT CONCAT(N'object|' COLLATE Latin1_General_BIN2, o.type, '|', s.name, '.', o.name)
        FROM sys.objects o
        JOIN sys.schemas s ON s.schema_id = o.schema_id
        WHERE o.is_ms_shipped = 0 AND o.parent_object_id = 0 AND o.type IN ('U', 'V', 'P', 'FN', 'IF', 'TF', 'SO', 'SN', 'TT') AND s.name <> @state;
        """;

    /// <summary>History tables SQL Server named itself, with the table they belong to.</summary>
    private const string AnonymousHistorySql = """
        SELECT h.name AS history, CONCAT(s.name, N'.', t.name) AS parent
        FROM sys.tables t
        JOIN sys.tables h ON h.object_id = t.history_table_id
        JOIN sys.schemas s ON s.schema_id = t.schema_id
        WHERE h.name LIKE N'MSSQL[_]TemporalHistoryFor[_]%';
        """;

    /// <summary>
    /// 2: system-versioned tables and partitioning; module definitions normalized (whitespace, leading CREATE);
    /// columns numbered by position (SQL Server never reuses a dropped column's id).
    /// </summary>
    public int FormatVersion => 2;

    public async Task<string> CaptureAsync(IDatabaseSession session, string stateSchema, CancellationToken cancellationToken)
    {
        var boundaries = session is not SqlServerSession { Fingerprint.PartitionBoundaries: PartitionBoundaries.Ignore };
        var rows = await session.QueryAsync(Sql, new Dictionary<string, object?> { ["state"] = stateSchema, ["boundaries"] = boundaries ? 1 : 0 }, cancellationToken).ConfigureAwait(false);
        var renames = (await session.QueryAsync(AnonymousHistorySql, null, cancellationToken).ConfigureAwait(false))
            .Select(r => (From: (string)r["history"]!, To: $"MSSQL_TemporalHistoryFor_<{r["parent"]}>"))
            .OrderByDescending(r => r.From.Length)
            .ToList();
        var lines = rows.Select(r =>
        {
            var line = ((string)r["line"]!).Replace("\r\n", "\n", StringComparison.Ordinal);
            foreach (var (from, to) in renames)
            {
                line = line.Replace(from, to, StringComparison.Ordinal);
            }

            return line.StartsWith("module|", StringComparison.Ordinal) ? TrimDefinition(line) : line;
        }).ToList();
        lines.Sort(StringComparer.Ordinal);
        return string.Join('\n', lines);
    }

    /// <summary>
    /// Groups dump lines into entries (module definitions span lines) keyed by kind and object name, e.g.
    /// "column dbo.Customer.Email", "index dbo.Customer.IX_Customer_Email", "module dbo.vCustomer".
    /// </summary>
    public IReadOnlyList<SchemaEntry> Parse(string dump)
    {
        var entries = new List<SchemaEntry>();
        var current = new List<string>();
        foreach (var line in dump.Split('\n'))
        {
            var bar = line.IndexOf('|', StringComparison.Ordinal);
            if (bar > 0 && Kinds.Contains(line[..bar]) && current.Count > 0)
            {
                entries.Add(Entry(current));
                current.Clear();
            }

            current.Add(line);
        }

        if (current.Count > 0 && !(current.Count == 1 && current[0].Length == 0))
        {
            entries.Add(Entry(current));
        }

        return entries;

        static SchemaEntry Entry(List<string> lines)
        {
            var text = string.Join('\n', lines);
            var f = lines[0].Split('|');
            string Field(int i) => i < f.Length ? f[i] : string.Empty;
            var key = f[0] switch
            {
                "column" => $"column {Field(1)}.{Field(3)}",
                "index" or "fk" or "check" or "partitioned" => $"{f[0]} {Field(1)}.{Field(2)}",
                "module" or "object" => $"{f[0]} {Field(2)}",
                "schema" or "type" or "sequence" or "synonym" or "temporal" or "pfunction" or "pscheme" => $"{f[0]} {Field(1)}",
                _ => text,
            };
            return new SchemaEntry(key, text);
        }
    }

    /// <summary>
    /// SQL Server stores a module's text exactly as sent, so whitespace around it depends on the tool that ran the
    /// script (bsm trims batches; others send the file as written). It is not part of the schema.
    /// </summary>
    private static string TrimDefinition(string line)
    {
        var bar = -1;
        for (var i = 0; i < 3; i++)
        {
            bar = line.IndexOf('|', bar + 1);
            if (bar < 0)
            {
                return line;
            }
        }

        return line[..(bar + 1)] + NormalizeDefinition(line[(bar + 1)..]);
    }

    /// <summary>
    /// A module definition without surrounding whitespace and with its leading CREATE written one way: SQL Server
    /// stores "CREATE OR ALTER VIEW" as "CREATE   VIEW" and ALTER VIEW as "CREATE VIEW", so the same view would
    /// otherwise differ depending on how it was last created.
    /// </summary>
    public static string NormalizeDefinition(string definition) =>
        LeadingCreateRegex().Replace(definition.Trim(), m => m.Groups["lead"].Value + "CREATE ", 1);

    [System.Text.RegularExpressions.GeneratedRegex(@"\A(?<lead>(?:\s+|--[^\n]*\n|/\*.*?\*/)*)CREATE\s+(?:OR\s+ALTER\s+)?(?=VIEW|PROC|FUNCTION|TRIGGER)", System.Text.RegularExpressions.RegexOptions.IgnoreCase | System.Text.RegularExpressions.RegexOptions.Singleline)]
    private static partial System.Text.RegularExpressions.Regex LeadingCreateRegex();

    private static readonly HashSet<string> Kinds = new(StringComparer.Ordinal) { "schema", "column", "index", "fk", "check", "module", "type", "sequence", "synonym", "temporal", "pfunction", "pscheme", "partitioned", "object" };
}
