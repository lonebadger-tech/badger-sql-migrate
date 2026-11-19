using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using Badger.SqlMigrate.Core.Conversion;
using Badger.SqlMigrate.Core.Import;
using Badger.SqlMigrate.Core.Plan;

namespace Badger.SqlMigrate.Conversion;

/// <param name="Project">Changes and repeatables to write as a new bsm project.</param>
/// <param name="Report">CONVERSION.md: mappings, renames and everything to review.</param>
public sealed record ConversionResult(ImportedProject Project, string Report, ConversionContext Context, IReadOnlyList<TranslatedCode> Code);

/// <summary>
/// Turns a <see cref="PortableSchema"/> read from one vendor into a bsm project for the other: changes for schemas,
/// types, sequences, tables, foreign keys and indexes (each with a revert), and repeatables for views, routines and
/// triggers translated by the dialect pair's <see cref="ITranslator"/>.
/// </summary>
public static partial class ConversionProject
{
    public const string Tool = "convert";

    public static ITranslator TranslatorFor(ConversionContext ctx) => (ctx.Source, ctx.Target) switch
    {
        (Dialect.SqlServer, Dialect.PostgreSql) => new TSqlToPg.TSqlToPgTranslator(ctx),
        (Dialect.PostgreSql, Dialect.SqlServer) => new PgToTSql.PgToTSqlTranslator(ctx),
        _ => throw new Core.MigrationException($"Converting {ctx.Source} to {ctx.Target} is not supported."),
    };

    public static ConversionResult Build(PortableSchema schema, ConversionOptions options)
    {
        var ctx = new ConversionContext(schema, options);
        var translator = TranslatorFor(ctx);
        var writer = new DdlWriter(ctx, translator);
        CheckNames(ctx);

        var project = new ImportedProject { Tool = Tool, SourceEngine = schema.SourceProvider, CanBaselineFromJournal = false };
        project.Warnings.AddRange(schema.Warnings);

        // Code first: functions that defaults, checks or computed columns call must exist before the tables.
        var translated = schema.Code.Select(c => translator.Code(c, schema.Code)).Where(t => t.Script.Length > 0).ToList();
        var tableDdl = writer.Tables();
        var early = translated.Where(t => t.Folder == "functions" && Regex.IsMatch(tableDdl.Deploy, $@"\b{Regex.Escape(ctx.Names.Name(t.Source.Name.Name))}\s*\(", RegexOptions.IgnoreCase)).ToList();

        void Change(string name, (string Deploy, string Revert) ddl, string what)
        {
            if (ddl.Deploy.Trim().Length == 0)
            {
                return;
            }

            project.Changes.Add(new ImportedChange(name, ddl.Deploy, ddl.Revert, ChangeFlags.None, $"convert: {what} from {schema.SourceProvider}", $"{schema.SourceProvider}: {what}"));
        }

        Change("convert-schemas", writer.Schemas(), "schemas");
        Change("convert-types", writer.Types(), "types");
        Change("convert-sequences", writer.Sequences(), "sequences");
        if (early.Count > 0)
        {
            Change("convert-functions", writer.Join(early.Select(t => t.Script)), "functions used by tables");
            project.Warnings.Add($"{string.Join(", ", early.Select(t => t.Source.Name))} are used by defaults, checks or computed columns, so they are created by the change convert-functions instead of as repeatables.");
        }

        Change("convert-tables", tableDdl, "tables");
        Change("convert-foreign-keys", writer.ForeignKeys(), "foreign keys");
        Change("convert-indexes", writer.Indexes(), "indexes");

        // Repeatables: views, routines and triggers, each requiring the repeatables it uses.
        var repeatables = translated.Except(early).ToList();
        var paths = repeatables.GroupBy(t => t.Source.Name.Key).ToDictionary(g => g.Key, g => g.Select(t => $"{t.Folder}/{t.FileName}").ToList());
        foreach (var t in repeatables)
        {
            var path = $"{t.Folder}/{t.FileName}";
            var requires = t.Source.DependsOn.Concat(t.Source.TriggerFunction is { } f ? [f.Key] : [])
                .SelectMany(d => paths.GetValueOrDefault(d) ?? []).Where(p => p != path).Distinct().ToList();
            var script = requires.Count == 0 ? t.Script : $"-- bsm:requires {string.Join(' ', requires)}\n{t.Script}";
            project.Repeatables.Add(new ImportedRepeatable(path, script, $"{schema.SourceProvider} {t.Source.Kind.ToString().ToLowerInvariant()} {t.Source.Name}"));
        }

        var review = translated.Count(t => t.Issues.Count > 0);
        if (review > 0)
        {
            project.Warnings.Add($"{review} of {translated.Count} views, routines and triggers have items to review: search the project for '{Review.Marker}' (listed in CONVERSION.md).");
        }

        return new ConversionResult(project, Report(ctx, translated), ctx, translated);
    }

    private static void CheckNames(ConversionContext ctx)
    {
        foreach (var group in ctx.Schema.Tables.GroupBy(t => ctx.Names.Schema(t.Name.Schema), StringComparer.Ordinal))
        {
            ctx.Names.CheckUnique($"tables in schema {group.Key}", group.Select(t => t.Name.Name), n => NameMapper.Apply(n, ctx.Names.Style));
        }

        foreach (var table in ctx.Schema.Tables)
        {
            ctx.Names.CheckUnique($"columns of {table.Name}", table.Columns.Select(c => c.Name), n => NameMapper.Apply(n, ctx.Names.Style));
        }
    }

    // ------------------------------------------------------------------ report

    private static string Report(ConversionContext ctx, IReadOnlyList<TranslatedCode> code)
    {
        var source = ctx.Source == Dialect.SqlServer ? "SQL Server" : "PostgreSQL";
        var target = ctx.Target == Dialect.SqlServer ? "SQL Server" : "PostgreSQL";
        var sb = new StringBuilder();
        sb.Append($"# Conversion from {source} to {target}\n\n");
        sb.Append($"Generated by `bsm convert schema` from {ctx.Schema.Tables.Count} tables, {ctx.Schema.Sequences.Count} sequences and {ctx.Schema.Code.Count} views, routines and triggers. ");
        sb.Append("Tables, keys and indexes are converted mechanically. Views, routines and triggers are translated as a best effort: ");
        sb.Append($"every place that needs a person is marked `{Review.Marker}` in the script.\n\n");
        sb.Append("Next: review this file, set the connection in `bsm.yaml`, run `bsm plan check`, `bsm deploy`, then `bsm convert data` to copy the rows.\n\n");

        sb.Append("## Needs review\n\n");
        var byObject = ctx.Issues.GroupBy(i => i.Object, StringComparer.Ordinal).OrderBy(g => g.Key, StringComparer.Ordinal).ToList();
        if (byObject.Count == 0)
        {
            sb.Append("Nothing was flagged.\n\n");
        }

        foreach (var group in byObject)
        {
            sb.Append($"- **{group.Key}**\n");
            foreach (var message in group.Select(i => i.Message).Distinct())
            {
                sb.Append($"  - {message.Replace("\n", " ", StringComparison.Ordinal)}\n");
            }
        }

        if (ctx.Schema.Warnings.Count > 0)
        {
            sb.Append("\n## Not converted\n\n");
            foreach (var warning in ctx.Schema.Warnings.Distinct())
            {
                sb.Append($"- {warning}\n");
            }
        }

        sb.Append("\n## Type mappings used\n\n| " + source + " | " + target + " |\n|---|---|\n");
        foreach (var (from, to) in ctx.TypeMappings)
        {
            sb.Append($"| `{from}` | `{to}` |\n");
        }

        if (ctx.Names.Renamed.Count > 0)
        {
            sb.Append($"\n## Renamed identifiers ({ctx.Names.Style.ToString().ToLowerInvariant()})\n\nQueries in the application that use the old names must change.\n\n| {source} | {target} |\n|---|---|\n");
            foreach (var (from, to) in ctx.Names.Renamed)
            {
                sb.Append($"| `{from}` | `{to}` |\n");
            }
        }

        if (ctx.CaseInsensitiveCollation)
        {
            sb.Append($"\n## Case-insensitive text\n\nThe source compares text case-insensitively ({ctx.Schema.Collation}). Text columns use the nondeterministic collation `{ConversionContext.CaseInsensitiveCollationName}` so that equality, ordering and unique keys behave the same. ");
            sb.Append("PostgreSQL before 18 cannot use LIKE on such columns: translated LIKE uses `COLLATE \"default\" ILIKE`. Pass `--case-sensitive` to leave text case-sensitive.\n");
        }

        sb.Append("\n## Translated objects\n\n| Object | File | Items to review |\n|---|---|---|\n");
        foreach (var t in code)
        {
            sb.Append($"| {t.Source.Kind.ToString().ToLowerInvariant()} `{t.Source.Name}` | `repeatable/{t.Folder}/{t.FileName}` | {t.Issues.Count} |\n");
        }

        return sb.ToString();
    }

    // ------------------------------------------------------------------ DDL

    /// <summary>Writes DDL in the target dialect for the portable schema.</summary>
    private sealed class DdlWriter(ConversionContext ctx, ITranslator translator)
    {
        private readonly bool pg = ctx.Target == Dialect.PostgreSql;
        private readonly Dictionary<string, HashSet<string>> objectNames = new(StringComparer.OrdinalIgnoreCase);
        private readonly List<(string Table, string Sql, string Drop)> extraIndexes = [];

        public (string, string) Join(IEnumerable<string> statements) => (string.Join(Separator, statements), string.Empty);

        private string Separator => pg ? "\n\n" : "\nGO\n\n";

        private (string, string) Ddl(List<string> deploy, List<string> revert) =>
            (deploy.Count == 0 ? string.Empty : string.Join(Separator, deploy) + "\n", revert.Count == 0 ? string.Empty : string.Join(Separator, Enumerable.Reverse(revert)) + "\n");

        /// <summary>Constraint and index names must be unique per schema in PostgreSQL (indexes) and SQL Server (all constraints), and at most 63 bytes in PostgreSQL.</summary>
        private string Unique(string schema, string name, string table)
        {
            var mapped = ctx.Names.Name(name);
            if (pg && Encoding.UTF8.GetByteCount(mapped) > 63)
            {
                var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(mapped)))[..8].ToLowerInvariant();
                mapped = mapped[..50] + "_" + hash;
            }

            if (!objectNames.TryGetValue(schema, out var names))
            {
                objectNames[schema] = names = new HashSet<string>(ctx.Schema.Tables.Where(t => ctx.Names.Schema(t.Name.Schema) == schema).Select(t => ctx.Names.Name(t.Name.Name)), StringComparer.OrdinalIgnoreCase);
            }

            var candidate = mapped;
            if (names.Contains(candidate))
            {
                candidate = $"{ctx.Names.Name(table)}_{mapped}";
            }

            for (var n = 2; names.Contains(candidate); n++)
            {
                candidate = $"{mapped}_{n}";
            }

            names.Add(candidate);
            return candidate;
        }

        private string Generated(string table, string suffix) => ctx.Names.Name(table) + "_" + suffix;

        public (string, string) Schemas()
        {
            var deploy = new List<string>();
            var revert = new List<string>();
            foreach (var s in ctx.Schema.Schemas.Select(ctx.Names.Schema).Distinct(StringComparer.OrdinalIgnoreCase))
            {
                deploy.Add(pg ? $"CREATE SCHEMA IF NOT EXISTS {ctx.Quote(s)};" : $"CREATE SCHEMA {ctx.Quote(s)};");
                revert.Add(pg ? $"DROP SCHEMA IF EXISTS {ctx.Quote(s)};" : $"DROP SCHEMA {ctx.Quote(s)};");
            }

            return Ddl(deploy, revert);
        }

        public (string, string) Types()
        {
            var deploy = new List<string>();
            var revert = new List<string>();
            if (ctx.CaseInsensitiveCollation)
            {
                deploy.Add($"CREATE COLLATION IF NOT EXISTS public.{ConversionContext.CaseInsensitiveCollationName} (provider = icu, locale = 'und-u-ks-level2', deterministic = false);");
                revert.Add($"DROP COLLATION IF EXISTS public.{ConversionContext.CaseInsensitiveCollationName};");
            }

            foreach (var d in ctx.Schema.Domains)
            {
                var name = ctx.Qualified(d.Name);
                var baseType = Map(d.BaseType, d.Name.ToString());
                if (pg)
                {
                    var sb = new StringBuilder($"CREATE DOMAIN {name} AS {baseType.Type}");
                    if (baseType.IsText && ctx.CaseInsensitiveCollation)
                    {
                        sb.Append($" COLLATE public.{ConversionContext.CaseInsensitiveCollationName}");
                    }

                    if (d.Default is not null)
                    {
                        sb.Append(" DEFAULT ").Append(translator.Expression(d.Default, new ExpressionSite(d.Name.ToString(), baseType.Type)));
                    }

                    if (!d.Nullable)
                    {
                        sb.Append(" NOT NULL");
                    }

                    deploy.Add(sb.Append(';').ToString());
                    revert.Add($"DROP DOMAIN {name};");
                }
                else
                {
                    deploy.Add($"CREATE TYPE {name} FROM {baseType.Type}{(d.Nullable ? string.Empty : " NOT NULL")};");
                    revert.Add($"DROP TYPE {name};");
                    if (d.Checks.Count > 0 || d.Default is not null)
                    {
                        ctx.Issue(d.Name.ToString(), "domain checks and default are applied to each column that uses the type (SQL Server alias types have none)");
                    }
                }
            }

            return Ddl(deploy, revert);
        }

        public (string, string) Sequences()
        {
            var deploy = new List<string>();
            var revert = new List<string>();
            foreach (var s in ctx.Schema.Sequences)
            {
                var name = ctx.Qualified(s.Name);
                var type = Map(s.Type, s.Name.ToString()).Type;
                if (pg && type is not ("smallint" or "integer" or "bigint"))
                {
                    type = "bigint";
                }
                else if (!pg && type is not ("tinyint" or "smallint" or "int" or "bigint") && !type.StartsWith("decimal", StringComparison.Ordinal))
                {
                    type = "bigint";
                }

                var sb = new StringBuilder($"CREATE SEQUENCE {name} AS {type} START WITH {s.Start} INCREMENT BY {s.Increment}");
                if (s.Minimum is { } min)
                {
                    sb.Append(CultureInfo.InvariantCulture, $" MINVALUE {min}");
                }

                if (s.Maximum is { } max)
                {
                    sb.Append(CultureInfo.InvariantCulture, $" MAXVALUE {max}");
                }

                sb.Append(s.Cycle ? " CYCLE" : pg ? " NO CYCLE" : " NO CYCLE");
                deploy.Add(sb.Append(';').ToString());
                revert.Add($"DROP SEQUENCE {name};");
            }

            return Ddl(deploy, revert);
        }

        private TypeMapping Map(string type, string owner)
        {
            var mapping = TypeMapper.Map(type, ctx);
            ctx.TypeMappings[type] = mapping.Type;
            if (mapping.Note is not null)
            {
                ctx.Issue(owner, mapping.Note);
            }

            return mapping;
        }

        public (string Deploy, string Revert) Tables()
        {
            var deploy = new List<string>();
            var revert = new List<string>();
            foreach (var table in ctx.Schema.Tables)
            {
                var (create, comments) = Table(table);
                deploy.Add(create);
                deploy.AddRange(comments);
                revert.Add($"DROP TABLE {ctx.Qualified(table.Name)};");
            }

            return Ddl(deploy, revert);
        }

        private (string Create, List<string> Comments) Table(PortableTable table)
        {
            var owner = table.Name.ToString();
            var schema = ctx.Names.Schema(table.Name.Schema);
            var tableName = ctx.Qualified(table.Name);
            var lines = new List<string>();
            var constraints = new List<string>();
            var comments = new List<string>();
            foreach (var c in table.Columns)
            {
                var column = ctx.Ident(c.Name);
                var mapping = Map(c.Type, owner);
                var sb = new StringBuilder(column);
                var domain = DomainOf(c.Type);
                if (c.Computed is not null)
                {
                    var expr = translator.Expression(c.Computed, new ExpressionSite(owner, mapping.Type));
                    if (pg)
                    {
                        sb.Append($" {mapping.Type} GENERATED ALWAYS AS ({expr}) STORED");
                        if (!c.ComputedStored)
                        {
                            ctx.Issue(owner, $"computed column {c.Name} is stored (PostgreSQL before 18 has no virtual generated columns)");
                        }
                    }
                    else
                    {
                        // A SQL Server computed column takes the expression's type; cast it to the source column's type.
                        sb.Append($" AS (CAST({expr} AS {mapping.Type})){(c.ComputedStored ? " PERSISTED" : string.Empty)}");
                    }

                    lines.Add(sb.ToString());
                    continue;
                }

                sb.Append(' ').Append(mapping.Type);
                if (pg && mapping.IsText && ctx.CaseInsensitiveCollation && domain is null && !(c.Collation?.Contains("_CS", StringComparison.OrdinalIgnoreCase) ?? false))
                {
                    sb.Append($" COLLATE public.{ConversionContext.CaseInsensitiveCollationName}");
                }
                else if (c.Collation is not null)
                {
                    ctx.Issue(owner, $"collation {c.Collation} of column {c.Name} is not converted");
                }

                if (c.Identity is { } id)
                {
                    sb.Append(pg
                        ? $" GENERATED {(id.Always ? "BY DEFAULT" : "BY DEFAULT")} AS IDENTITY (START WITH {id.Seed} INCREMENT BY {id.Increment})"
                        : $" IDENTITY({id.Seed}, {id.Increment})");
                }

                var defaultExpr = c.Default ?? domain?.Default;
                if (defaultExpr is not null)
                {
                    var value = translator.Expression(StripParens(defaultExpr), new ExpressionSite(owner, mapping.Type));
                    sb.Append(pg ? $" DEFAULT {value}" : $" CONSTRAINT {ctx.Quote(Unique(schema, $"DF_{table.Name.Name}_{c.Name}", table.Name.Name))} DEFAULT ({value})");
                }

                sb.Append(c.Nullable ? (pg ? string.Empty : " NULL") : " NOT NULL");
                lines.Add(sb.ToString());

                if (mapping.Check is not null)
                {
                    constraints.Add($"CONSTRAINT {ctx.Quote(Unique(schema, pg ? Generated(table.Name.Name, c.Name + "_check") : $"CK_{table.Name.Name}_{c.Name}", table.Name.Name))} CHECK ({string.Format(CultureInfo.InvariantCulture, mapping.Check, column)})");
                }

                foreach (var check in domain?.Checks ?? [])
                {
                    var expression = Regex.Replace(check.Expression, @"\bVALUE\b", ctx.Source == Dialect.PostgreSql ? $"\"{c.Name}\"" : $"[{c.Name}]", RegexOptions.IgnoreCase);
                    constraints.Add($"CONSTRAINT {ctx.Quote(Unique(schema, $"CK_{table.Name.Name}_{c.Name}_{check.Name ?? "domain"}", table.Name.Name))} CHECK ({translator.Predicate(expression, new ExpressionSite(owner))})");
                }

                if (c.Comment is { Length: > 0 } comment)
                {
                    comments.Add(pg
                        ? $"COMMENT ON COLUMN {tableName}.{column} IS {ctx.Literal(comment)};"
                        : $"EXEC sys.sp_addextendedproperty @name = N'MS_Description', @value = {ctx.Literal(comment)}, @level0type = N'SCHEMA', @level0name = {ctx.Literal(schema)}, @level1type = N'TABLE', @level1name = {ctx.Literal(ctx.Names.Name(table.Name.Name))}, @level2type = N'COLUMN', @level2name = {ctx.Literal(ctx.Names.Name(c.Name))};");
                }
            }

            if (table.PrimaryKey is { } pk)
            {
                var name = Unique(schema, pk.Name ?? (pg ? Generated(table.Name.Name, "pkey") : $"PK_{table.Name.Name}"), table.Name.Name);
                constraints.Insert(0, $"CONSTRAINT {ctx.Quote(name)} PRIMARY KEY ({Columns(pk.Columns)})");
            }

            foreach (var u in table.Uniques)
            {
                var name = Unique(schema, u.Name ?? (pg ? Generated(table.Name.Name, string.Join('_', u.Columns) + "_key") : $"UQ_{table.Name.Name}_{string.Join('_', u.Columns)}"), table.Name.Name);
                var nullable = u.Columns.Any(col => table.Columns.FirstOrDefault(c => c.Name == col)?.Nullable ?? false);
                if (!pg && nullable)
                {
                    // SQL Server allows one NULL in a unique constraint; PostgreSQL allows many. A filtered index keeps PostgreSQL's rule.
                    extraIndexes.Add((tableName,
                        $"CREATE UNIQUE NONCLUSTERED INDEX {ctx.Quote(name)} ON {tableName} ({Columns(u.Columns)}) WHERE {string.Join(" AND ", u.Columns.Select(col => $"{ctx.Ident(col)} IS NOT NULL"))};",
                        $"DROP INDEX {ctx.Quote(name)} ON {tableName};"));
                    ctx.Issue(owner, $"unique constraint {u.Name} on nullable columns became a filtered unique index (SQL Server would allow only one NULL)");
                    continue;
                }

                constraints.Add($"CONSTRAINT {ctx.Quote(name)} UNIQUE ({Columns(u.Columns)})");
                if (pg && nullable)
                {
                    ctx.Issue(owner, $"unique constraint {u.Name} on nullable columns now allows several NULLs (PostgreSQL's rule); add NULLS NOT DISTINCT (PostgreSQL 15+) to keep SQL Server's");
                }
            }

            foreach (var check in table.Checks)
            {
                var name = Unique(schema, check.Name ?? (pg ? Generated(table.Name.Name, "check") : $"CK_{table.Name.Name}"), table.Name.Name);
                constraints.Add($"CONSTRAINT {ctx.Quote(name)} CHECK ({translator.Predicate(StripParens(check.Expression), new ExpressionSite(owner))})");
            }

            if (table.Comment is { Length: > 0 } tableComment)
            {
                comments.Insert(0, pg
                    ? $"COMMENT ON TABLE {tableName} IS {ctx.Literal(tableComment)};"
                    : $"EXEC sys.sp_addextendedproperty @name = N'MS_Description', @value = {ctx.Literal(tableComment)}, @level0type = N'SCHEMA', @level0name = {ctx.Literal(schema)}, @level1type = N'TABLE', @level1name = {ctx.Literal(ctx.Names.Name(table.Name.Name))};");
            }

            var body = string.Join(",\n", lines.Concat(constraints).Select(l => "    " + l));
            return ($"CREATE TABLE {tableName} (\n{body}\n);", comments);
        }

        private PortableDomain? DomainOf(string type)
        {
            var m = Regex.Match(type, """^(?:"?\[?(?<s>[^".\[\]]+)\]?"?)\.(?:"?\[?(?<n>[^".\[\]]+)\]?"?)$""");
            return m.Success ? ctx.Domain(m.Groups["s"].Value, m.Groups["n"].Value) : null;
        }

        /// <summary>SQL Server stores defaults and checks wrapped in parentheses: ((0)), ([a]>(0)).</summary>
        private static string StripParens(string expression)
        {
            var e = expression.Trim();
            while (e.Length >= 2 && e[0] == '(' && e[^1] == ')' && Balanced(e[1..^1]))
            {
                e = e[1..^1].Trim();
            }

            return e;
        }

        private static bool Balanced(string text)
        {
            var depth = 0;
            var quote = false;
            foreach (var c in text)
            {
                if (c == '\'')
                {
                    quote = !quote;
                }
                else if (!quote && c == '(')
                {
                    depth++;
                }
                else if (!quote && c == ')' && --depth < 0)
                {
                    return false;
                }
            }

            return depth == 0;
        }

        private string Columns(IEnumerable<string> columns) => string.Join(", ", columns.Select(ctx.Ident));

        public (string, string) ForeignKeys()
        {
            var deploy = new List<string>();
            var revert = new List<string>();
            var cascades = false;
            foreach (var table in ctx.Schema.Tables)
            {
                var schema = ctx.Names.Schema(table.Name.Schema);
                var tableName = ctx.Qualified(table.Name);
                foreach (var fk in table.ForeignKeys)
                {
                    var name = ctx.Quote(Unique(schema, fk.Name ?? (pg ? Generated(table.Name.Name, string.Join('_', fk.Columns) + "_fkey") : $"FK_{table.Name.Name}_{fk.Referenced.Name}"), table.Name.Name));
                    var sb = new StringBuilder($"ALTER TABLE {tableName} ADD CONSTRAINT {name} FOREIGN KEY ({Columns(fk.Columns)}) REFERENCES {ctx.Qualified(fk.Referenced)} ({Columns(fk.ReferencedColumns)})");
                    sb.Append(Action("DELETE", fk.OnDelete)).Append(Action("UPDATE", fk.OnUpdate));
                    cascades |= fk.OnDelete is not ("NO ACTION" or "RESTRICT") || fk.OnUpdate is not ("NO ACTION" or "RESTRICT");
                    if (pg)
                    {
                        sb.Append(" DEFERRABLE INITIALLY IMMEDIATE");
                    }

                    deploy.Add(sb.Append(';').ToString());
                    revert.Add($"ALTER TABLE {tableName} DROP CONSTRAINT {name};");
                }
            }

            if (!pg && cascades)
            {
                ctx.Issue("foreign keys", "SQL Server rejects ON DELETE/UPDATE actions that could reach a table along two paths or a cycle; if 'bsm deploy' reports that, change one of them to NO ACTION and handle it in a trigger");
            }

            return Ddl(deploy, revert);
        }

        private string Action(string verb, string action) => action switch
        {
            "NO ACTION" => string.Empty,
            "RESTRICT" => pg ? $" ON {verb} RESTRICT" : string.Empty,
            _ => $" ON {verb} {action}",
        };

        public (string, string) Indexes()
        {
            var deploy = new List<string>();
            var revert = new List<string>();
            foreach (var (_, sql, drop) in extraIndexes)
            {
                deploy.Add(sql);
                revert.Add(drop);
            }

            foreach (var table in ctx.Schema.Tables)
            {
                var owner = table.Name.ToString();
                var schema = ctx.Names.Schema(table.Name.Schema);
                var tableName = ctx.Qualified(table.Name);
                foreach (var index in table.Indexes)
                {
                    var method = index.Method;
                    if (method is "columnstore" or "gin" or "gist" or "spgist")
                    {
                        ctx.Issue(owner, $"{method} index {index.Name} is not converted");
                        continue;
                    }

                    if (!pg && index.Columns.Any(c => c.IsExpression))
                    {
                        ctx.Issue(owner, $"expression index {index.Name} is not converted: add a computed column and index it");
                        continue;
                    }

                    if (method is not null and not "btree")
                    {
                        ctx.Issue(owner, $"{method} index {index.Name} became a B-tree index");
                    }

                    var name = ctx.Quote(Unique(schema, index.Name, table.Name.Name));
                    var columns = string.Join(", ", index.Columns.Select(c => (c.IsExpression ? $"({translator.Expression(c.Text, new ExpressionSite(owner))})" : ctx.Ident(c.Text)) + (c.Descending ? " DESC" : string.Empty)));
                    var sb = new StringBuilder($"CREATE {(index.Unique ? "UNIQUE " : string.Empty)}{(pg ? string.Empty : "NONCLUSTERED ")}INDEX {name} ON {tableName} ({columns})");
                    if (index.Included.Count > 0)
                    {
                        sb.Append($" INCLUDE ({Columns(index.Included)})");
                    }

                    if (index.Filter is not null)
                    {
                        sb.Append(" WHERE ").Append(translator.Predicate(StripParens(index.Filter), new ExpressionSite(owner)));
                    }

                    deploy.Add(sb.Append(';').ToString());
                    revert.Add(pg ? $"DROP INDEX {ctx.Quote(schema)}.{name};" : $"DROP INDEX {name} ON {tableName};");
                }
            }

            return Ddl(deploy, revert);
        }
    }
}
