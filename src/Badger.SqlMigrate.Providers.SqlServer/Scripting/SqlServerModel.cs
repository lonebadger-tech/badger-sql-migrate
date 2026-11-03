using System.Globalization;
using Badger.SqlMigrate.Core.Providers;

namespace Badger.SqlMigrate.Providers.SqlServer.Scripting;

/// <summary>"[schema].[name]"; identifiers are compared without regard to case, as SQL Server's default collations do.</summary>
internal readonly record struct ObjectName(string Schema, string Name)
{
    public string Quoted => $"{Sql.Quote(Schema)}.{Sql.Quote(Name)}";

    public string Key => $"{Schema}.{Name}".ToLowerInvariant();

    public override string ToString() => $"{Schema}.{Name}";
}

internal sealed record IdentitySpec(string Seed, string Increment);

internal sealed record ColumnDefault(string? Name, string Definition);

internal sealed record ColumnEncryption(string Key, string Type, string Algorithm);

internal sealed record Column(
    string Name,
    int Position,
    string Type,
    bool Nullable,
    IdentitySpec? Identity,
    string? Computed,
    bool Persisted,
    ColumnDefault? Default,
    string? Collation,
    bool Sparse,
    bool RowGuid,
    string? GeneratedAlways,
    bool Hidden,
    ColumnEncryption? Encryption,
    string? Mask)
{
    /// <summary>Everything that defines the column apart from its position and default.</summary>
    public string Shape => string.Join('|', Type, Nullable, Identity, Computed, Persisted, Collation, Sparse, RowGuid, GeneratedAlways, Hidden, Encryption, Mask);
}

internal sealed record IndexColumn(string Name, bool Descending);

/// <summary>A filegroup ("PRIMARY") or a partition scheme with its column.</summary>
internal sealed record DataSpace(string Name, string? PartitionColumn)
{
    public string Script => PartitionColumn is null ? Sql.Quote(Name) : $"{Sql.Quote(Name)} ({Sql.Quote(PartitionColumn)})";
}

internal sealed record KeyConstraint(string? Name, bool Primary, bool Clustered, IReadOnlyList<IndexColumn> Columns, DataSpace? DataSpace)
{
    public string Shape => string.Join('|', Primary, Clustered, string.Join(',', Columns), DataSpace);
}

internal sealed record IndexDefinition(
    string Name,
    string Kind,
    bool Unique,
    IReadOnlyList<IndexColumn> Columns,
    IReadOnlyList<string> Included,
    string? Filter,
    DataSpace? DataSpace,
    string? Compression)
{
    public string Shape => string.Join('|', Kind, Unique, string.Join(',', Columns), string.Join(',', Included), Filter, DataSpace, Compression);
}

internal sealed record ForeignKey(
    string? Name,
    IReadOnlyList<string> Columns,
    ObjectName Referenced,
    IReadOnlyList<string> ReferencedColumns,
    string OnDelete,
    string OnUpdate,
    bool Disabled,
    bool NotTrusted)
{
    public string Shape => string.Join('|', string.Join(',', Columns), Referenced.Key, string.Join(',', ReferencedColumns), OnDelete, OnUpdate, Disabled, NotTrusted);

    /// <summary>Identity for system-named keys, whose names differ per server.</summary>
    public string Match => Name ?? "<fk>" + string.Join(',', Columns).ToLowerInvariant() + ">" + Referenced.Key;
}

internal sealed record CheckConstraint(string? Name, string Definition, bool Disabled)
{
    public string Match => Name ?? "<check>" + Definition;
}

internal sealed record Temporal(string Start, string End, ObjectName? History, bool HistoryNamedBySystem, string? Retention);

internal sealed record Table(ObjectName Name, IReadOnlyList<Column> Columns, KeyConstraint? PrimaryKey, IReadOnlyList<KeyConstraint> Uniques, IReadOnlyList<IndexDefinition> Indexes,
    IReadOnlyList<ForeignKey> ForeignKeys, IReadOnlyList<CheckConstraint> Checks, DataSpace? DataSpace, Temporal? Temporal, string? Compression)
{
    /// <summary>Set when this table is the history table of a system-versioned table (SQL Server keeps it in step).</summary>
    public ObjectName? HistoryOf { get; init; }

    /// <summary>Index kinds this model cannot script (XML, spatial, full-text ...).</summary>
    public IReadOnlyList<string> Unsupported { get; init; } = [];
}

/// <param name="Type">V, P, FN, IF, TF or TR.</param>
internal sealed record Module(ObjectName Name, string Type, string Definition, IReadOnlyList<string> References);

internal sealed record TableType(ObjectName Name, IReadOnlyList<Column> Columns, KeyConstraint? PrimaryKey, IReadOnlyList<CheckConstraint> Checks)
{
    public string Shape => string.Join(';', Columns.Select(c => c.Name + ":" + c.Shape + ":" + c.Default?.Definition)) + ";" + PrimaryKey?.Shape + ";" + string.Join(',', Checks.Select(c => c.Definition));
}

internal sealed record AliasType(ObjectName Name, string BaseType, bool Nullable);

internal sealed record Sequence(ObjectName Name, string Type, string Start, string Increment, string Minimum, string Maximum, bool Cycle, string? Cache);

internal sealed record Synonym(ObjectName Name, string Target);

internal sealed record PartitionFunction(string Name, string Type, bool RangeRight, IReadOnlyList<string> Boundaries, bool QuoteBoundaries);

internal sealed record PartitionScheme(string Name, string Function, IReadOnlyList<string> Filegroups);

/// <summary>Everything the scripter compares, read from one database. The bsm journal schema and excluded objects are left out.</summary>
internal sealed class SqlServerModel
{
    public string DefaultCollation { get; set; } = string.Empty;

    public SortedDictionary<string, string> Schemas { get; } = new(StringComparer.OrdinalIgnoreCase);

    public Dictionary<string, Table> Tables { get; } = new(StringComparer.OrdinalIgnoreCase);

    public Dictionary<string, Module> Modules { get; } = new(StringComparer.OrdinalIgnoreCase);

    public Dictionary<string, TableType> TableTypes { get; } = new(StringComparer.OrdinalIgnoreCase);

    public Dictionary<string, AliasType> AliasTypes { get; } = new(StringComparer.OrdinalIgnoreCase);

    public Dictionary<string, Sequence> Sequences { get; } = new(StringComparer.OrdinalIgnoreCase);

    public Dictionary<string, Synonym> Synonyms { get; } = new(StringComparer.OrdinalIgnoreCase);

    public Dictionary<string, PartitionFunction> PartitionFunctions { get; } = new(StringComparer.OrdinalIgnoreCase);

    public Dictionary<string, PartitionScheme> PartitionSchemes { get; } = new(StringComparer.OrdinalIgnoreCase);

    public static async Task<SqlServerModel> ReadAsync(IDatabaseSession session, string stateSchema, Func<ObjectName, bool> excluded, CancellationToken ct)
    {
        var model = new SqlServerModel();
        var p = new Dictionary<string, object?> { ["state"] = stateSchema };
        async Task<IReadOnlyList<IReadOnlyDictionary<string, object?>>> Q(string sql) => await session.QueryAsync(sql, p, ct).ConfigureAwait(false);

        model.DefaultCollation = Str((await Q("SELECT CONVERT(nvarchar(128), DATABASEPROPERTYEX(DB_NAME(), 'Collation')) AS c;"))[0]["c"]) ?? string.Empty;

        foreach (var r in await Q("SELECT name FROM sys.schemas WHERE schema_id > 4 AND schema_id < 16384 AND name <> @state;"))
        {
            model.Schemas[Str(r["name"])!] = Str(r["name"])!;
        }

        // Columns of tables and table types.
        var columns = new Dictionary<int, List<Column>>();
        foreach (var r in await Q(ColumnsSql))
        {
            var objectId = Int(r["object_id"]);
            if (!columns.TryGetValue(objectId, out var list))
            {
                columns[objectId] = list = [];
            }

            var collation = Str(r["collation_name"]);
            list.Add(new Column(
                Str(r["name"])!,
                Int(r["column_id"]),
                TypeName(Str(r["type_schema"])!, Str(r["type_name"])!, Bool(r["is_user_defined"]), Int(r["max_length"]), Int(r["precision"]), Int(r["scale"])),
                Bool(r["is_nullable"]),
                Bool(r["is_identity"]) ? new IdentitySpec(Str(r["seed"]) ?? "1", Str(r["increment"]) ?? "1") : null,
                Str(r["computed"]),
                Bool(r["is_persisted"]),
                Str(r["default_definition"]) is { } def ? new ColumnDefault(Bool(r["default_system_named"]) ? null : Str(r["default_name"]), def) : null,
                collation is not null && !string.Equals(collation, model.DefaultCollation, StringComparison.Ordinal) ? collation : null,
                Bool(r["is_sparse"]),
                Bool(r["is_rowguidcol"]),
                Int(r["generated_always_type"]) switch { 1 => "ROW START", 2 => "ROW END", _ => null },
                Bool(r["is_hidden"]),
                Str(r["cek"]) is { } cek ? new ColumnEncryption(cek, Str(r["encryption_type_desc"])!, Str(r["encryption_algorithm_name"])!) : null,
                Str(r["masking_function"])));
        }

        // Index and key columns.
        var indexColumns = new Dictionary<(int, int), (List<IndexColumn> Keys, List<string> Included, string? Partition)>();
        foreach (var r in await Q(IndexColumnsSql))
        {
            var key = (Int(r["object_id"]), Int(r["index_id"]));
            if (!indexColumns.TryGetValue(key, out var entry))
            {
                indexColumns[key] = entry = ([], [], null);
            }

            if (Int(r["partition_ordinal"]) == 1)
            {
                entry.Partition = Str(r["name"]);
                indexColumns[key] = entry;
            }

            if (Bool(r["is_included_column"]))
            {
                entry.Included.Add(Str(r["name"])!);
            }
            else if (Int(r["key_ordinal"]) > 0)
            {
                entry.Keys.Add(new IndexColumn(Str(r["name"])!, Bool(r["is_descending_key"])));
            }
        }

        var keys = new Dictionary<int, List<(KeyConstraint Key, bool SystemNamed)>>();
        var indexes = new Dictionary<int, List<IndexDefinition>>();
        var unsupported = new Dictionary<int, List<string>>();
        var tableSpaces = new Dictionary<int, (DataSpace? Space, string? Compression)>();
        foreach (var r in await Q(IndexesSql))
        {
            var objectId = Int(r["object_id"]);
            var indexId = Int(r["index_id"]);
            var cols = indexColumns.GetValueOrDefault((objectId, indexId), ([], [], null));
            var space = Str(r["data_space"]) is { } ds ? new DataSpace(ds, Str(r["data_space_type"]) == "PS" ? cols.Partition : null) : null;
            var compression = Str(r["compression"]) is { } c && c != "NONE" ? c : null;
            var type = Int(r["type"]);
            if (indexId <= 1)
            {
                tableSpaces[objectId] = (space, compression);
            }

            if (type == 0)
            {
                continue;
            }

            if (Bool(r["is_primary_key"]) || Bool(r["is_unique_constraint"]))
            {
                if (!keys.TryGetValue(objectId, out var list))
                {
                    keys[objectId] = list = [];
                }

                list.Add((new KeyConstraint(Bool(r["is_system_named"]) ? null : Str(r["name"]), Bool(r["is_primary_key"]), type == 1, cols.Keys, space), Bool(r["is_system_named"])));
                continue;
            }

            if (type is not (1 or 2 or 5 or 6))
            {
                if (!unsupported.TryGetValue(objectId, out var u))
                {
                    unsupported[objectId] = u = [];
                }

                u.Add($"{Str(r["type_desc"])!.ToLowerInvariant()} index {Str(r["name"])}");
                continue;
            }

            if (!indexes.TryGetValue(objectId, out var ix))
            {
                indexes[objectId] = ix = [];
            }

            ix.Add(new IndexDefinition(Str(r["name"])!, Str(r["type_desc"])!, Bool(r["is_unique"]), cols.Keys, cols.Included, Str(r["filter_definition"]), space, compression));
        }

        var foreignKeys = new Dictionary<int, List<ForeignKey>>();
        var fkColumns = (await Q(ForeignKeyColumnsSql)).GroupBy(r => Int(r["constraint_object_id"])).ToDictionary(g => g.Key, g => g.OrderBy(r => Int(r["constraint_column_id"])).ToList());
        foreach (var r in await Q(ForeignKeysSql))
        {
            var parent = Int(r["parent_object_id"]);
            if (!foreignKeys.TryGetValue(parent, out var list))
            {
                foreignKeys[parent] = list = [];
            }

            var cols = fkColumns.GetValueOrDefault(Int(r["object_id"])) ?? [];
            list.Add(new ForeignKey(
                Bool(r["is_system_named"]) ? null : Str(r["name"]),
                cols.Select(c => Str(c["parent_column"])!).ToList(),
                new ObjectName(Str(r["ref_schema"])!, Str(r["ref_table"])!),
                cols.Select(c => Str(c["ref_column"])!).ToList(),
                Str(r["delete_referential_action_desc"])!.Replace('_', ' '),
                Str(r["update_referential_action_desc"])!.Replace('_', ' '),
                Bool(r["is_disabled"]),
                Bool(r["is_not_trusted"])));
        }

        var checks = (await Q(ChecksSql)).GroupBy(r => Int(r["parent_object_id"]))
            .ToDictionary(g => g.Key, g => g.Select(r => new CheckConstraint(Bool(r["is_system_named"]) ? null : Str(r["name"]), Str(r["definition"])!, Bool(r["is_disabled"]))).ToList());

        var tableRows = await Q(TablesSql);
        var names = tableRows.ToDictionary(r => Int(r["object_id"]), r => new ObjectName(Str(r["schema_name"])!, Str(r["name"])!));
        foreach (var r in tableRows)
        {
            var objectId = Int(r["object_id"]);
            var name = names[objectId];
            if (excluded(name))
            {
                continue;
            }

            Temporal? temporal = null;
            if (Str(r["period_start"]) is { } start)
            {
                var historyId = r["history_table_id"] is null ? (int?)null : Int(r["history_table_id"]);
                var history = historyId is { } h && names.TryGetValue(h, out var hn) ? hn : (ObjectName?)null;
                temporal = new Temporal(start, Str(r["period_end"])!, history, history is { } x && x.Name.StartsWith("MSSQL_TemporalHistoryFor_", StringComparison.Ordinal),
                    Int(r["temporal_type"]) == 2 && Str(r["retention_unit"]) is { } unit && unit != "INFINITE" ? $"{Int(r["retention_period"])} {unit}S" : null);
            }

            var tableKeys = keys.GetValueOrDefault(objectId) ?? [];
            var (space, compression) = tableSpaces.GetValueOrDefault(objectId);
            model.Tables[name.Key] = new Table(
                name,
                (columns.GetValueOrDefault(objectId) ?? []).OrderBy(c => c.Position).ToList(),
                tableKeys.Where(k => k.Key.Primary).Select(k => k.Key).FirstOrDefault(),
                tableKeys.Where(k => !k.Key.Primary).Select(k => k.Key).ToList(),
                indexes.GetValueOrDefault(objectId) ?? [],
                foreignKeys.GetValueOrDefault(objectId) ?? [],
                checks.GetValueOrDefault(objectId) ?? [],
                space,
                temporal,
                compression)
            {
                Unsupported = unsupported.GetValueOrDefault(objectId) ?? [],
            };
        }

        foreach (var table in model.Tables.Values.Where(t => t.Temporal?.History is not null).ToList())
        {
            if (model.Tables.TryGetValue(table.Temporal!.History!.Value.Key, out var history))
            {
                model.Tables[history.Name.Key] = history with { HistoryOf = table.Name };
            }
        }

        var dependencies = (await Q(DependenciesSql)).GroupBy(r => Int(r["referencing_id"])).ToDictionary(g => g.Key, g => g.Select(r => new ObjectName(Str(r["ref_schema"])!, Str(r["ref_name"])!).Key).Distinct().ToList());
        foreach (var r in await Q(ModulesSql))
        {
            var name = new ObjectName(Str(r["schema_name"])!, Str(r["name"])!);
            if (!excluded(name))
            {
                model.Modules[name.Key] = new Module(name, Str(r["type"])!.Trim(), SqlServerSchemaInspector.NormalizeDefinition(Str(r["definition"])!), dependencies.GetValueOrDefault(Int(r["object_id"])) ?? []);
            }
        }

        foreach (var r in await Q(TableTypesSql))
        {
            var name = new ObjectName(Str(r["schema_name"])!, Str(r["name"])!);
            var objectId = Int(r["type_table_object_id"]);
            var typeKeys = keys.GetValueOrDefault(objectId) ?? [];
            model.TableTypes[name.Key] = new TableType(name, (columns.GetValueOrDefault(objectId) ?? []).OrderBy(c => c.Position).ToList(),
                typeKeys.Select(k => k.Key).FirstOrDefault(k => k.Primary), checks.GetValueOrDefault(objectId) ?? []);
        }

        foreach (var r in await Q(AliasTypesSql))
        {
            var name = new ObjectName(Str(r["schema_name"])!, Str(r["name"])!);
            model.AliasTypes[name.Key] = new AliasType(name, TypeName("sys", Str(r["base_type"])!, false, Int(r["max_length"]), Int(r["precision"]), Int(r["scale"])), Bool(r["is_nullable"]));
        }

        foreach (var r in await Q(SequencesSql))
        {
            var name = new ObjectName(Str(r["schema_name"])!, Str(r["name"])!);
            model.Sequences[name.Key] = new Sequence(name, TypeName("sys", Str(r["type_name"])!, false, 0, Int(r["precision"]), Int(r["scale"])),
                Str(r["start_value"])!, Str(r["increment"])!, Str(r["minimum_value"])!, Str(r["maximum_value"])!, Bool(r["is_cycling"]), Bool(r["is_cached"]) ? Str(r["cache_size"]) ?? string.Empty : null);
        }

        foreach (var r in await Q(SynonymsSql))
        {
            var name = new ObjectName(Str(r["schema_name"])!, Str(r["name"])!);
            model.Synonyms[name.Key] = new Synonym(name, Str(r["base_object_name"])!);
        }

        var boundaries = (await Q(BoundariesSql)).GroupBy(r => Int(r["function_id"])).ToDictionary(g => g.Key, g => g.OrderBy(r => Int(r["boundary_id"])).Select(r => Str(r["value"])!).ToList());
        foreach (var r in await Q(PartitionFunctionsSql))
        {
            var type = Str(r["type_name"])!;
            model.PartitionFunctions[Str(r["name"])!] = new PartitionFunction(Str(r["name"])!, TypeName("sys", type, false, Int(r["max_length"]), Int(r["precision"]), Int(r["scale"])),
                Bool(r["boundary_value_on_right"]), boundaries.GetValueOrDefault(Int(r["function_id"])) ?? [], !NumericTypes.Contains(type));
        }

        var filegroups = (await Q(SchemeFilegroupsSql)).GroupBy(r => Int(r["partition_scheme_id"])).ToDictionary(g => g.Key, g => g.OrderBy(r => Int(r["destination_id"])).Select(r => Str(r["name"])!).ToList());
        foreach (var r in await Q(PartitionSchemesSql))
        {
            model.PartitionSchemes[Str(r["name"])!] = new PartitionScheme(Str(r["name"])!, Str(r["function_name"])!, filegroups.GetValueOrDefault(Int(r["data_space_id"])) ?? []);
        }

        return model;
    }

    private static readonly HashSet<string> NumericTypes = new(StringComparer.OrdinalIgnoreCase) { "tinyint", "smallint", "int", "bigint", "decimal", "numeric", "real", "float", "money", "smallmoney", "bit" };

    /// <summary>A column type as written in DDL: nvarchar(100), decimal(19,4), varbinary(max), [dbo].[Phone].</summary>
    public static string TypeName(string schema, string name, bool userDefined, int maxLength, int precision, int scale)
    {
        if (userDefined)
        {
            return $"{Sql.Quote(schema)}.{Sql.Quote(name)}";
        }

        return name.ToLowerInvariant() switch
        {
            "varchar" or "char" or "varbinary" or "binary" => $"{name}({(maxLength == -1 ? "max" : maxLength.ToString(CultureInfo.InvariantCulture))})",
            "nvarchar" or "nchar" => $"{name}({(maxLength == -1 ? "max" : (maxLength / 2).ToString(CultureInfo.InvariantCulture))})",
            "decimal" or "numeric" => $"{name}({precision},{scale})",
            "datetime2" or "time" or "datetimeoffset" => $"{name}({scale})",
            "float" => precision == 53 ? "float" : $"float({precision})",
            _ => name,
        };
    }

    private static string? Str(object? value) => value switch
    {
        null => null,
        string s => s,
        _ => Convert.ToString(value, CultureInfo.InvariantCulture),
    };

    private static int Int(object? value) => value is null ? 0 : Convert.ToInt32(value, CultureInfo.InvariantCulture);

    private static bool Bool(object? value) => value is not null && Convert.ToBoolean(value, CultureInfo.InvariantCulture);

    private const string UserObject = "o.is_ms_shipped = 0 AND s.name <> @state";

    private const string ColumnsSql = $"""
        SELECT c.object_id, c.column_id, c.name, ts.name AS type_schema, t.name AS type_name, t.is_user_defined,
               c.max_length, c.precision, c.scale, c.is_nullable, c.is_identity,
               CONVERT(nvarchar(64), ic.seed_value) AS seed, CONVERT(nvarchar(64), ic.increment_value) AS increment,
               cc.definition AS computed, CONVERT(bit, ISNULL(cc.is_persisted, 0)) AS is_persisted,
               dc.name AS default_name, dc.definition AS default_definition, CONVERT(bit, ISNULL(dc.is_system_named, 0)) AS default_system_named,
               c.collation_name, c.is_sparse, c.is_rowguidcol, c.generated_always_type, c.is_hidden,
               cek.name AS cek, c.encryption_type_desc, c.encryption_algorithm_name, mc.masking_function
        FROM sys.columns c
        JOIN sys.objects o ON o.object_id = c.object_id
        JOIN sys.schemas s ON s.schema_id = o.schema_id
        JOIN sys.types t ON t.user_type_id = c.user_type_id
        JOIN sys.schemas ts ON ts.schema_id = t.schema_id
        LEFT JOIN sys.identity_columns ic ON ic.object_id = c.object_id AND ic.column_id = c.column_id
        LEFT JOIN sys.computed_columns cc ON cc.object_id = c.object_id AND cc.column_id = c.column_id
        LEFT JOIN sys.default_constraints dc ON dc.object_id = c.default_object_id
        LEFT JOIN sys.column_encryption_keys cek ON cek.column_encryption_key_id = c.column_encryption_key_id
        LEFT JOIN sys.masked_columns mc ON mc.object_id = c.object_id AND mc.column_id = c.column_id
        WHERE o.type IN ('U', 'TT') AND {UserObject};
        """;

    private const string IndexColumnsSql = $"""
        SELECT ic.object_id, ic.index_id, ic.key_ordinal, ic.partition_ordinal, ic.is_descending_key, ic.is_included_column, c.name
        FROM sys.index_columns ic
        JOIN sys.columns c ON c.object_id = ic.object_id AND c.column_id = ic.column_id
        JOIN sys.objects o ON o.object_id = ic.object_id
        JOIN sys.schemas s ON s.schema_id = o.schema_id
        WHERE {UserObject}
        ORDER BY ic.object_id, ic.index_id, ic.key_ordinal, ic.index_column_id;
        """;

    private const string IndexesSql = $"""
        SELECT i.object_id, i.index_id, i.name, i.type, i.type_desc, i.is_unique, i.is_primary_key, i.is_unique_constraint, i.filter_definition,
               CONVERT(bit, ISNULL(kc.is_system_named, 0)) AS is_system_named, ds.name AS data_space, ds.type AS data_space_type,
               (SELECT TOP (1) p.data_compression_desc FROM sys.partitions p WHERE p.object_id = i.object_id AND p.index_id = i.index_id ORDER BY p.partition_number) AS compression
        FROM sys.indexes i
        JOIN sys.objects o ON o.object_id = i.object_id
        JOIN sys.schemas s ON s.schema_id = o.schema_id
        LEFT JOIN sys.data_spaces ds ON ds.data_space_id = i.data_space_id
        LEFT JOIN sys.key_constraints kc ON kc.parent_object_id = i.object_id AND kc.unique_index_id = i.index_id
        WHERE o.type IN ('U', 'TT') AND i.is_hypothetical = 0 AND {UserObject};
        """;

    private const string ForeignKeysSql = $"""
        SELECT fk.object_id, fk.parent_object_id, fk.name, fk.is_system_named, rs.name AS ref_schema, ro.name AS ref_table,
               fk.delete_referential_action_desc, fk.update_referential_action_desc, fk.is_disabled, fk.is_not_trusted
        FROM sys.foreign_keys fk
        JOIN sys.objects o ON o.object_id = fk.parent_object_id
        JOIN sys.schemas s ON s.schema_id = o.schema_id
        JOIN sys.objects ro ON ro.object_id = fk.referenced_object_id
        JOIN sys.schemas rs ON rs.schema_id = ro.schema_id
        WHERE {UserObject};
        """;

    private const string ForeignKeyColumnsSql = """
        SELECT fkc.constraint_object_id, fkc.constraint_column_id, pc.name AS parent_column, rc.name AS ref_column
        FROM sys.foreign_key_columns fkc
        JOIN sys.columns pc ON pc.object_id = fkc.parent_object_id AND pc.column_id = fkc.parent_column_id
        JOIN sys.columns rc ON rc.object_id = fkc.referenced_object_id AND rc.column_id = fkc.referenced_column_id;
        """;

    private const string ChecksSql = $"""
        SELECT ck.parent_object_id, ck.name, ck.is_system_named, ck.definition, ck.is_disabled
        FROM sys.check_constraints ck
        JOIN sys.objects o ON o.object_id = ck.parent_object_id
        JOIN sys.schemas s ON s.schema_id = o.schema_id
        WHERE {UserObject};
        """;

    private const string TablesSql = """
        SELECT t.object_id, s.name AS schema_name, t.name, t.temporal_type, t.history_table_id,
               t.history_retention_period AS retention_period, t.history_retention_period_unit_desc AS retention_unit,
               sc.name AS period_start, ec.name AS period_end
        FROM sys.tables t
        JOIN sys.schemas s ON s.schema_id = t.schema_id
        LEFT JOIN sys.periods p ON p.object_id = t.object_id
        LEFT JOIN sys.columns sc ON sc.object_id = p.object_id AND sc.column_id = p.start_column_id
        LEFT JOIN sys.columns ec ON ec.object_id = p.object_id AND ec.column_id = p.end_column_id
        WHERE t.is_ms_shipped = 0 AND s.name <> @state;
        """;

    private const string ModulesSql = $"""
        SELECT o.object_id, s.name AS schema_name, o.name, o.type, m.definition
        FROM sys.sql_modules m
        JOIN sys.objects o ON o.object_id = m.object_id
        JOIN sys.schemas s ON s.schema_id = o.schema_id
        WHERE o.type IN ('V', 'P', 'FN', 'IF', 'TF', 'TR') AND {UserObject};
        """;

    private const string DependenciesSql = """
        SELECT d.referencing_id, rs.name AS ref_schema, ro.name AS ref_name
        FROM sys.sql_expression_dependencies d
        JOIN sys.objects ro ON ro.object_id = d.referenced_id
        JOIN sys.schemas rs ON rs.schema_id = ro.schema_id
        WHERE d.referenced_id IS NOT NULL;
        """;

    private const string TableTypesSql = """
        SELECT s.name AS schema_name, tt.name, tt.type_table_object_id
        FROM sys.table_types tt
        JOIN sys.schemas s ON s.schema_id = tt.schema_id
        WHERE s.name <> @state;
        """;

    private const string AliasTypesSql = """
        SELECT s.name AS schema_name, t.name, TYPE_NAME(t.system_type_id) AS base_type, t.max_length, t.precision, t.scale, t.is_nullable
        FROM sys.types t
        JOIN sys.schemas s ON s.schema_id = t.schema_id
        WHERE t.is_user_defined = 1 AND t.is_table_type = 0 AND s.name <> @state;
        """;

    private const string SequencesSql = """
        SELECT s.name AS schema_name, sq.name, TYPE_NAME(sq.user_type_id) AS type_name, sq.precision, sq.scale,
               CONVERT(nvarchar(64), sq.start_value) AS start_value, CONVERT(nvarchar(64), sq.increment) AS increment,
               CONVERT(nvarchar(64), sq.minimum_value) AS minimum_value, CONVERT(nvarchar(64), sq.maximum_value) AS maximum_value,
               sq.is_cycling, sq.is_cached, CONVERT(nvarchar(64), sq.cache_size) AS cache_size
        FROM sys.sequences sq
        JOIN sys.schemas s ON s.schema_id = sq.schema_id
        WHERE s.name <> @state;
        """;

    private const string SynonymsSql = """
        SELECT s.name AS schema_name, sy.name, sy.base_object_name
        FROM sys.synonyms sy
        JOIN sys.schemas s ON s.schema_id = sy.schema_id
        WHERE s.name <> @state;
        """;

    private const string PartitionFunctionsSql = """
        SELECT pf.function_id, pf.name, pf.boundary_value_on_right, TYPE_NAME(pp.system_type_id) AS type_name, pp.max_length, pp.precision, pp.scale
        FROM sys.partition_functions pf
        JOIN sys.partition_parameters pp ON pp.function_id = pf.function_id;
        """;

    private const string BoundariesSql = "SELECT function_id, boundary_id, CONVERT(nvarchar(4000), value, 126) AS value FROM sys.partition_range_values;";

    private const string PartitionSchemesSql = """
        SELECT ps.data_space_id, ps.name, pf.name AS function_name
        FROM sys.partition_schemes ps
        JOIN sys.partition_functions pf ON pf.function_id = ps.function_id;
        """;

    private const string SchemeFilegroupsSql = """
        SELECT d.partition_scheme_id, d.destination_id, fg.name
        FROM sys.destination_data_spaces d
        JOIN sys.filegroups fg ON fg.data_space_id = d.data_space_id;
        """;
}

internal static class Sql
{
    public static string Quote(string name) => "[" + name.Replace("]", "]]", StringComparison.Ordinal) + "]";

    public static string Literal(string text) => "N'" + text.Replace("'", "''", StringComparison.Ordinal) + "'";
}
