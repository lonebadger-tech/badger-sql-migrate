using Badger.SqlMigrate.Core.Providers;

namespace Badger.SqlMigrate.Providers.SqlServer;

/// <summary>
/// Canonical text dump of user schema objects, sorted ordinally, used for the schema fingerprint.
/// System-generated constraint names (DF__Orders__Statu__3B75D760) differ between servers, so they are
/// replaced with a placeholder; otherwise identical schemas would never fingerprint the same.
/// Always Encrypted columns record the CEK name but never key paths or values, which differ per environment,
/// so identical schemas fingerprint the same in every environment. The bsm state schema is excluded. Catalog views mix collations, so each line is forced to a binary collation.
/// </summary>
public sealed class SqlServerSchemaInspector : ISchemaInspector
{
    private const string Sql = """
        SELECT CONCAT(N'schema|' COLLATE Latin1_General_BIN2, s.name) AS line
        FROM sys.schemas s
        WHERE s.schema_id > 4 AND s.schema_id < 16384 AND s.name <> @state

        UNION ALL
        SELECT CONCAT(N'column|' COLLATE Latin1_General_BIN2, s.name, '.', o.name, '|', c.column_id, '|', c.name, '|', TYPE_NAME(c.user_type_id),
               '|', c.max_length, '|', c.precision, '|', c.scale, '|', c.is_nullable, '|', c.is_identity,
               '|', ISNULL(c.collation_name, ''), '|', ISNULL(cc.definition, ''), '|', ISNULL(dc.definition, ''),
               '|', CASE WHEN dc.is_system_named = 1 THEN '<system>' ELSE ISNULL(dc.name, '') END,
               '|', c.is_sparse, '|', c.is_rowguidcol, '|', ISNULL(c.encryption_type_desc, ''), '|', ISNULL(c.encryption_algorithm_name, ''), '|', ISNULL(cek.name, ''))
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
        SELECT CONCAT(N'object|' COLLATE Latin1_General_BIN2, o.type, '|', s.name, '.', o.name)
        FROM sys.objects o
        JOIN sys.schemas s ON s.schema_id = o.schema_id
        WHERE o.is_ms_shipped = 0 AND o.parent_object_id = 0 AND o.type IN ('U', 'V', 'P', 'FN', 'IF', 'TF', 'SO', 'SN', 'TT') AND s.name <> @state;
        """;

    public async Task<string> CaptureAsync(IDatabaseSession session, string stateSchema, CancellationToken cancellationToken)
    {
        var rows = await session.QueryAsync(Sql, new Dictionary<string, object?> { ["state"] = stateSchema }, cancellationToken).ConfigureAwait(false);
        var lines = rows.Select(r => ((string)r["line"]!).Replace("\r\n", "\n", StringComparison.Ordinal)).ToList();
        lines.Sort(StringComparer.Ordinal);
        return string.Join('\n', lines);
    }
}
