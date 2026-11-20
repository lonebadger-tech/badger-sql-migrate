using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using Badger.Trek.Core.Conversion;
using Badger.Trek.Core.Import;
using Badger.Trek.Core.Plan;

namespace Badger.Trek.Conversion;

/// <param name="Project">Changes and repeatables to write as a new trek project.</param>
/// <param name="Report">CONVERSION.md: mappings, renames and everything to review.</param>
public sealed record ConversionResult(ImportedProject Project, string Report, ConversionContext Context, IReadOnlyList<TranslatedCode> Code);

/// <summary>
/// Turns a <see cref="PortableSchema"/> read from one vendor into a trek project for the other: changes for schemas,
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
        (Dialect.PostgreSql, Dialect.Sqlite) => new Sqlite.PgToSqliteTranslator(ctx),
        (Dialect.Sqlite, Dialect.PostgreSql) => new Sqlite.SqliteToPgTranslator(ctx),
        (Dialect.SqlServer, Dialect.Sqlite) or (Dialect.Sqlite, Dialect.SqlServer) => new ChainTranslator(ctx),
        (Dialect.PostgreSql, Dialect.MySql) => new MySql.PgToMySqlTranslator(ctx),
        (Dialect.MySql, Dialect.PostgreSql) => new MySql.MySqlToPgTranslator(ctx),
        (Dialect.SqlServer or Dialect.Sqlite, Dialect.MySql) or (Dialect.MySql, Dialect.SqlServer or Dialect.Sqlite) => new ChainTranslator(ctx),
        _ => throw new Core.MigrationException($"Converting {ctx.Source} to {ctx.Target} is not supported."),
    };

    /// <summary>
    /// A view that calls a routine written commented out (not translated) cannot be created, so it is commented out too,
    /// and so on for views that use those views.
    /// </summary>
    private static List<TranslatedCode> CommentOutBrokenViews(ConversionContext ctx, List<TranslatedCode> translated)
    {
        static bool Inactive(string script) =>
            Regex.Replace(Regex.Replace(script, @"/\*.*?\*/", string.Empty, RegexOptions.Singleline), @"--[^\n]*", string.Empty).Trim().Length == 0;

        bool changed;
        do
        {
            changed = false;
            // A missing view is matched by name; a missing routine only where it is called, name( or `name`(.
            var missing = translated.Where(t => Inactive(t.Script))
                .Select(t => (Name: ctx.TargetName(t.Source.Name).Name, Call: t.Source.Kind != PortableCodeKind.View)).ToList();
            for (var i = 0; i < translated.Count; i++)
            {
                var t = translated[i];
                if (t.Source.Kind != PortableCodeKind.View || Inactive(t.Script))
                {
                    continue;
                }

                var used = missing.FirstOrDefault(m => Regex.IsMatch(t.Script, $@"(?<![\w$]){Regex.Escape(m.Name)}" + (m.Call ? @"[`""\]]?\s*\(" : @"(?![\w$])"), RegexOptions.IgnoreCase)).Name;
                if (used is null)
                {
                    continue;
                }

                var message = $"view uses {used}, which was not translated; it is commented out until {used} is rewritten";
                ctx.Issue(t.Source.Name.ToString(), message);
                translated[i] = t with { Script = Review.CommentedOut(t.Script.TrimEnd(), Review.Comment(message)) + "\n", Issues = [.. t.Issues, message] };
                changed = true;
            }
        }
        while (changed);

        return translated;
    }

    public static ConversionResult Build(PortableSchema schema, ConversionOptions options)
    {
        var ctx = new ConversionContext(schema, options);
        var translator = TranslatorFor(ctx);
        var writer = new DdlWriter(ctx, translator);
        CheckNames(ctx);

        var project = new ImportedProject { Tool = Tool, SourceEngine = schema.SourceProvider, CanBaselineFromJournal = false };
        project.Warnings.AddRange(schema.Warnings);

        // Code first: functions that defaults, checks or computed columns call must exist before the tables.
        var translated = CommentOutBrokenViews(ctx, schema.Code.Select(c => translator.Code(c, schema.Code)).Where(t => t.Script.Length > 0).ToList());
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
            var script = requires.Count == 0 ? t.Script : $"-- trek:requires {string.Join(' ', requires)}\n{t.Script}";
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
        var tables = ctx.Schema.Tables.Select(t => t.Name).Concat(ctx.Schema.Code.Where(c => c.Kind == PortableCodeKind.View).Select(c => c.Name)).ToList();
        foreach (var group in tables.GroupBy(t => ctx.TargetName(t).Schema, StringComparer.Ordinal))
        {
            ctx.Names.CheckUnique($"tables and views in schema {group.Key}", group.Select(t => t.ToString()), n =>
            {
                var dot = n.IndexOf('.', StringComparison.Ordinal);
                return ctx.TargetName(n[..dot], n[(dot + 1)..]).Name.ToLowerInvariant();
            });
        }

        foreach (var table in ctx.Schema.Tables)
        {
            ctx.Names.CheckUnique($"columns of {table.Name}", table.Columns.Select(c => c.Name), n => NameMapper.Apply(n, ctx.Names.Style));
        }
    }

    // ------------------------------------------------------------------ report

    private static string Report(ConversionContext ctx, IReadOnlyList<TranslatedCode> code)
    {
        static string Display(Dialect d) => d switch { Dialect.SqlServer => "SQL Server", Dialect.Sqlite => "SQLite", Dialect.MySql => "MySQL", _ => "PostgreSQL" };
        var source = Display(ctx.Source);
        var target = Display(ctx.Target);
        var sb = new StringBuilder();
        sb.Append($"# Conversion from {source} to {target}\n\n");
        sb.Append($"Generated by `trek convert schema` from {ctx.Schema.Tables.Count} tables, {ctx.Schema.Sequences.Count} sequences and {ctx.Schema.Code.Count} views, routines and triggers. ");
        sb.Append("Tables, keys and indexes are converted mechanically. Views, routines and triggers are translated as a best effort: ");
        sb.Append($"every place that needs a person is marked `{Review.Marker}` in the script.\n\n");
        sb.Append("Next: review this file, set the connection in `trek.yaml`, run `trek plan check`, `trek deploy`, then `trek convert data` to copy the rows.\n\n");
        if (ctx.Target == Dialect.Sqlite)
        {
            sb.Append("SQLite has no schemas: tables outside the default schema are named schema_table. It has no stored functions or procedures (they are kept commented out; simple single-expression functions are written in place where views use them) and its triggers hold only SQL statements, so PL/pgSQL and T-SQL trigger logic is flattened into guarded statements.\n\n");
        }


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

        if (ctx.CaseInsensitiveCollation && ctx.Target == Dialect.Sqlite)
        {
            sb.Append($"\n## Case-insensitive text\n\nThe source compares text case-insensitively ({ctx.Schema.Collation}). Text columns use `COLLATE NOCASE`, which ignores the case of ASCII letters only (É and é still differ). SQLite's LIKE already ignores ASCII case. Pass `--case-sensitive` to leave text case-sensitive.\n");
        }
        else if (ctx.CaseInsensitiveCollation)
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
        private readonly bool lite = ctx.Target == Dialect.Sqlite;
        private readonly bool ss = ctx.Target == Dialect.SqlServer;
        private readonly bool my = ctx.Target == Dialect.MySql;

        /// <summary>Columns that compare case-insensitively need PostgreSQL's trek_ci collation.</summary>
        private bool NeedsCaseInsensitiveCollation =>
            ctx.Target == Dialect.PostgreSql && (ctx.CaseInsensitiveCollation || ctx.Schema.Tables.SelectMany(t => t.Columns).Any(c => IsNoCase(c.Collation)));

        private static bool IsNoCase(string? collation) => collation?.Equals("NOCASE", StringComparison.OrdinalIgnoreCase) == true;
        private readonly Dictionary<string, HashSet<string>> objectNames = new(StringComparer.OrdinalIgnoreCase);
        private readonly List<(string Table, string Sql, string Drop)> extraIndexes = [];

        public (string, string) Join(IEnumerable<string> statements) => (string.Join(Separator, statements), string.Empty);

        private string Separator => ss ? "\nGO\n\n" : "\n\n";

        private (string, string) Ddl(List<string> deploy, List<string> revert) =>
            (deploy.Count == 0 ? string.Empty : string.Join(Separator, deploy) + "\n", revert.Count == 0 ? string.Empty : string.Join(Separator, Enumerable.Reverse(revert)) + "\n");

        /// <summary>Constraint and index names must be unique per schema in PostgreSQL (indexes) and SQL Server (all constraints), and at most 63 bytes in PostgreSQL.</summary>
        private string Unique(string schema, string name, string table)
        {
            var mapped = ctx.Names.Name(name);
            if ((pg && Encoding.UTF8.GetByteCount(mapped) > 63) || (my && mapped.Length > 64))
            {
                var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(mapped)))[..8].ToLowerInvariant();
                mapped = mapped[..50] + "_" + hash;
            }

            if (!objectNames.TryGetValue(schema, out var names))
            {
                objectNames[schema] = names = new HashSet<string>(ctx.Schema.Tables.Select(t => ctx.TargetName(t.Name)).Where(t => t.Schema == schema).Select(t => t.Name), StringComparer.OrdinalIgnoreCase);
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
            if (lite || my)
            {
                return (string.Empty, string.Empty);
            }

            var deploy = new List<string>();
            var revert = new List<string>();
            // The target's default schema (dbo, public) is already there, whichever source schema maps to it.
            var builtIn = ConversionContext.DefaultSchemaOf(ctx.Target);
            foreach (var s in ctx.Schema.Schemas.Select(ctx.Names.Schema).Distinct(StringComparer.OrdinalIgnoreCase)
                .Where(s => !string.Equals(s, builtIn, StringComparison.OrdinalIgnoreCase)))
            {
                deploy.Add(pg ? $"CREATE SCHEMA IF NOT EXISTS {ctx.Quote(s)};" : $"CREATE SCHEMA {ctx.Quote(s)};");
                revert.Add(pg ? $"DROP SCHEMA IF EXISTS {ctx.Quote(s)};" : $"DROP SCHEMA {ctx.Quote(s)};");
            }

            return Ddl(deploy, revert);
        }

        public (string, string) Types()
        {
            if (lite || my)
            {
                // Enums and domains become their base type with checks on each column (MySQL: an ENUM column); NOCASE is built in.
                return (string.Empty, string.Empty);
            }

            var deploy = new List<string>();
            var revert = new List<string>();
            if (NeedsCaseInsensitiveCollation)
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
            if (lite || my)
            {
                foreach (var s in ctx.Schema.Sequences)
                {
                    ctx.Issue(s.Name.ToString(), $"{(my ? "MySQL" : "SQLite")} has no sequences: the sequence is not converted, and defaults that draw from it are dropped");
                }

                return (string.Empty, string.Empty);
            }

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
            var schema = ctx.TargetName(table.Name).Schema;
            var tableName = ctx.Qualified(table.Name);
            var lines = new List<string>();
            var constraints = new List<string>();
            var comments = new List<string>();
            var rowidKey = false;
            var tableOptions = new List<string>();

            // MySQL indexes only the start of TEXT and BLOB columns: one in a key or index gets a bounded type instead.
            var keyColumns = (table.PrimaryKey?.Columns ?? [])
                .Concat(table.Uniques.SelectMany(u => u.Columns))
                .Concat(table.ForeignKeys.SelectMany(f => f.Columns))
                .Concat(table.Indexes.SelectMany(i => i.Columns.Where(c => !c.IsExpression).Select(c => c.Text)))
                .Concat(ctx.Schema.Tables.SelectMany(other => other.ForeignKeys.Where(f => f.Referenced.Key == table.Name.Key).SelectMany(f => f.ReferencedColumns)))
                .ToHashSet(StringComparer.OrdinalIgnoreCase);
            foreach (var c in table.Columns)
            {
                var column = ctx.Ident(c.Name);
                var mapping = Map(c.Type, owner);
                if (my && keyColumns.Contains(c.Name) && mapping.Type is "LONGTEXT" or "LONGBLOB")
                {
                    var bounded = mapping.Type == "LONGTEXT" ? "VARCHAR(255)" : "VARBINARY(255)";
                    ctx.Issue(owner, $"column {c.Name} ({c.Type}) is in a key or index, which MySQL cannot build on unbounded text: it became {bounded}; check the longest value");
                    mapping = mapping with { Type = bounded };
                }

                var sb = new StringBuilder(column);
                var domain = DomainOf(c.Type);
                if (c.Computed is not null)
                {
                    var expr = translator.Expression(c.Computed, new ExpressionSite(owner, mapping.Type));
                    if (lite)
                    {
                        // SQLite does not round to a declared NUMERIC(p,s); do it in the expression.
                        if (Regex.Match(mapping.Type, @"^NUMERIC\(\d+,(?<s>\d+)\)$") is { Success: true } scale)
                        {
                            expr = $"round({expr}, {scale.Groups["s"].Value})";
                        }

                        sb.Append($" {mapping.Type} GENERATED ALWAYS AS ({expr}) {(c.ComputedStored ? "STORED" : "VIRTUAL")}");
                        ctx.Issue(owner, $"computed column {c.Name} is a generated column, which needs SQLite 3.31, newer than sqlite.minVersion (3.26): raise sqlite.minVersion if every program that opens the database has 3.31, or compute it in a view or trigger instead");
                    }
                    else if (my)
                    {
                        sb.Append($" {mapping.Type} GENERATED ALWAYS AS ({expr}) {(c.ComputedStored ? "STORED" : "VIRTUAL")}");
                    }
                    else if (pg)
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
                if (pg && mapping.IsText && domain is null && (IsNoCase(c.Collation) || (ctx.CaseInsensitiveCollation && !(c.Collation?.Contains("_CS", StringComparison.OrdinalIgnoreCase) ?? false))))
                {
                    sb.Append($" COLLATE public.{ConversionContext.CaseInsensitiveCollationName}");
                }
                else if (lite && mapping.IsText && (c.Collation?.Equals("NOCASE", StringComparison.OrdinalIgnoreCase) == true
                    || (ctx.CaseInsensitiveCollation && !(c.Collation?.Contains("_CS", StringComparison.OrdinalIgnoreCase) ?? false))))
                {
                    sb.Append(" COLLATE NOCASE");
                }
                else if (my && mapping.IsText && IsNoCase(c.Collation))
                {
                    sb.Append(" COLLATE utf8mb4_0900_as_ci");
                }
                else if (c.Collation?.Equals("NOCASE", StringComparison.OrdinalIgnoreCase) == true && mapping.IsText && ss)
                {
                    // SQL Server's default collation is already case-insensitive.
                }
                else if (c.Collation is not null)
                {
                    ctx.Issue(owner, $"collation {c.Collation} of column {c.Name} is not converted");
                }

                if (c.Identity is not null && lite)
                {
                    if (table.PrimaryKey is { Columns: [var only] } && only == c.Name && mapping.Type == "INTEGER")
                    {
                        // The rowid: SQLite numbers rows itself; AUTOINCREMENT never reuses a number, like an identity.
                        sb.Append(" PRIMARY KEY AUTOINCREMENT");
                        rowidKey = true;
                    }
                    else
                    {
                        ctx.Issue(owner, $"identity column {c.Name} is not the table's single integer primary key, so SQLite cannot number it: values must be supplied");
                    }
                }
                else if (c.Identity is { } myId && my)
                {
                    sb.Append(" AUTO_INCREMENT");
                    if (table.PrimaryKey?.Columns is not [var first, ..] || !string.Equals(first, c.Name, StringComparison.OrdinalIgnoreCase))
                    {
                        // MySQL needs an AUTO_INCREMENT column to start an index.
                        constraints.Add($"KEY {ctx.Quote(Unique(schema, Generated(table.Name.Name, c.Name + "_seq"), table.Name.Name))} ({column})");
                    }

                    if (myId.Seed != 1)
                    {
                        tableOptions.Add($"AUTO_INCREMENT={myId.Seed}");
                    }

                    if (myId.Increment != 1)
                    {
                        ctx.Issue(owner, $"identity column {c.Name} increments by {myId.Increment}; MySQL increments by the server's auto_increment_increment (1 by default)");
                    }
                }
                else if (c.Identity is { } id)
                {
                    sb.Append(pg
                        ? $" GENERATED {(id.Always ? "BY DEFAULT" : "BY DEFAULT")} AS IDENTITY (START WITH {id.Seed} INCREMENT BY {id.Increment})"
                        : $" IDENTITY({id.Seed}, {id.Increment})");
                }

                var defaultExpr = c.Default ?? domain?.Default;
                if (defaultExpr is not null)
                {
                    var value = translator.Expression(StripParens(defaultExpr), new ExpressionSite(owner, mapping.Type));
                    sb.Append(my ? MySqlDefault(value, mapping.Type) : lite ? $" DEFAULT ({value})" : pg ? $" DEFAULT {value}" : $" CONSTRAINT {ctx.Quote(Unique(schema, $"DF_{table.Name.Name}_{c.Name}", table.Name.Name))} DEFAULT ({value})");
                }

                sb.Append(c.Nullable ? (ss ? " NULL" : string.Empty) : " NOT NULL");
                if (my && c.Comment is { Length: > 0 } inline)
                {
                    sb.Append(" COMMENT ").Append(ctx.Literal(inline));
                }

                lines.Add(sb.ToString());

                if (mapping.Check is not null)
                {
                    constraints.Add($"CONSTRAINT {ctx.Quote(Unique(schema, !ss ? Generated(table.Name.Name, c.Name + "_check") : $"CK_{table.Name.Name}_{c.Name}", table.Name.Name))} CHECK ({string.Format(CultureInfo.InvariantCulture, mapping.Check, column)})");
                }

                foreach (var check in domain?.Checks ?? [])
                {
                    var expression = Regex.Replace(check.Expression, @"\bVALUE\b", ctx.Source == Dialect.PostgreSql ? $"\"{c.Name}\"" : $"[{c.Name}]", RegexOptions.IgnoreCase);
                    constraints.Add($"CONSTRAINT {ctx.Quote(Unique(schema, $"CK_{table.Name.Name}_{c.Name}_{check.Name ?? "domain"}", table.Name.Name))} CHECK ({translator.Predicate(expression, new ExpressionSite(owner))})");
                }

                if (c.Comment is { Length: > 0 } && lite)
                {
                    comments.Add($"-- {ctx.Names.Name(c.Name)}: {c.Comment.Replace('\n', ' ')}");
                }
                else if (c.Comment is { Length: > 0 } comment && !my)
                {
                    comments.Add(pg
                        ? $"COMMENT ON COLUMN {tableName}.{column} IS {ctx.Literal(comment)};"
                        : $"EXEC sys.sp_addextendedproperty @name = N'MS_Description', @value = {ctx.Literal(comment)}, @level0type = N'SCHEMA', @level0name = {ctx.Literal(schema)}, @level1type = N'TABLE', @level1name = {ctx.Literal(ctx.Names.Name(table.Name.Name))}, @level2type = N'COLUMN', @level2name = {ctx.Literal(ctx.Names.Name(c.Name))};");
                }
            }

            if (table.PrimaryKey is { } pk && !rowidKey)
            {
                var name = Unique(schema, pk.Name ?? (!ss ? Generated(table.Name.Name, "pkey") : $"PK_{table.Name.Name}"), table.Name.Name);
                constraints.Insert(0, $"CONSTRAINT {ctx.Quote(name)} PRIMARY KEY ({Columns(pk.Columns)})");
            }

            foreach (var u in table.Uniques)
            {
                var name = Unique(schema, u.Name ?? (!ss ? Generated(table.Name.Name, string.Join('_', u.Columns) + "_key") : $"UQ_{table.Name.Name}_{string.Join('_', u.Columns)}"), table.Name.Name);
                var nullable = u.Columns.Any(col => table.Columns.FirstOrDefault(c => c.Name == col)?.Nullable ?? false);
                if (ss && nullable)
                {
                    // SQL Server allows one NULL in a unique constraint; PostgreSQL allows many. A filtered index keeps PostgreSQL's rule.
                    extraIndexes.Add((tableName,
                        $"CREATE UNIQUE NONCLUSTERED INDEX {ctx.Quote(name)} ON {tableName} ({Columns(u.Columns)}) WHERE {string.Join(" AND ", u.Columns.Select(col => $"{ctx.Ident(col)} IS NOT NULL"))};",
                        $"DROP INDEX {ctx.Quote(name)} ON {tableName};"));
                    ctx.Issue(owner, $"unique constraint {u.Name} on nullable columns became a filtered unique index (SQL Server would allow only one NULL)");
                    continue;
                }

                constraints.Add($"CONSTRAINT {ctx.Quote(name)} UNIQUE ({Columns(u.Columns)})");
                if (!ss && nullable && ctx.Source == Dialect.SqlServer)
                {
                    ctx.Issue(owner, pg
                        ? $"unique constraint {u.Name} on nullable columns now allows several NULLs (PostgreSQL's rule); add NULLS NOT DISTINCT (PostgreSQL 15+) to keep SQL Server's"
                        : $"unique constraint {u.Name} on nullable columns now allows several NULLs (SQLite's rule)");
                }
            }

            foreach (var check in table.Checks)
            {
                var name = Unique(schema, check.Name ?? (!ss ? Generated(table.Name.Name, "check") : $"CK_{table.Name.Name}"), table.Name.Name);
                constraints.Add($"CONSTRAINT {ctx.Quote(name)} CHECK ({translator.Predicate(StripParens(check.Expression), new ExpressionSite(owner))})");
            }

            if (lite)
            {
                // SQLite cannot add foreign keys to an existing table: they are part of CREATE TABLE.
                foreach (var fk in table.ForeignKeys)
                {
                    var name = ctx.Quote(Unique(schema, fk.Name ?? Generated(table.Name.Name, string.Join('_', fk.Columns) + "_fkey"), table.Name.Name));
                    constraints.Add($"CONSTRAINT {name} FOREIGN KEY ({Columns(fk.Columns)}) REFERENCES {ctx.Qualified(fk.Referenced)} ({Columns(fk.ReferencedColumns)}){Action("DELETE", fk.OnDelete)}{Action("UPDATE", fk.OnUpdate)}");
                }
            }

            if (table.Comment is { Length: > 0 } && lite)
            {
                comments.Insert(0, $"-- {ctx.Names.Name(table.Name.Name)}: {table.Comment.Replace('\n', ' ')}");
            }
            else if (table.Comment is { Length: > 0 } myComment && my)
            {
                tableOptions.Add($"COMMENT={ctx.Literal(myComment)}");
            }
            else if (table.Comment is { Length: > 0 } tableComment)
            {
                comments.Insert(0, pg
                    ? $"COMMENT ON TABLE {tableName} IS {ctx.Literal(tableComment)};"
                    : $"EXEC sys.sp_addextendedproperty @name = N'MS_Description', @value = {ctx.Literal(tableComment)}, @level0type = N'SCHEMA', @level0name = {ctx.Literal(schema)}, @level1type = N'TABLE', @level1name = {ctx.Literal(ctx.Names.Name(table.Name.Name))};");
            }

            var body = string.Join(",\n", lines.Concat(constraints).Select(l => "    " + l));
            if (lite)
            {
                // Comments go above the table: SQLite keeps them in sqlite_schema only inside the statement.
                return (string.Concat(comments.Select(c => c + "\n")) + $"CREATE TABLE {tableName} (\n{body}\n);", []);
            }

            if (my)
            {
                // Comparisons behave as in the source: case-insensitive (SQL Server) or case-sensitive (PostgreSQL, SQLite).
                var collation = ctx.CaseInsensitiveCollation ? "utf8mb4_0900_as_ci" : "utf8mb4_0900_as_cs";
                tableOptions.InsertRange(0, ["ENGINE=InnoDB", "DEFAULT CHARSET=utf8mb4", $"COLLATE={collation}"]);
                return ($"CREATE TABLE {tableName} (\n{body}\n) {string.Join(' ', tableOptions)};", comments);
            }

            return ($"CREATE TABLE {tableName} (\n{body}\n);", comments);
        }

        /// <summary>
        /// A MySQL column default: CURRENT_TIMESTAMP on a DATETIME or TIMESTAMP is written bare, with the column's
        /// fractional precision (MySQL requires them to match); anything else is an expression default in parentheses,
        /// which MySQL accepts for every type, TEXT, BLOB and JSON included.
        /// </summary>
        private static string MySqlDefault(string value, string type)
        {
            var precision = Regex.Match(type, @"^(?:DATETIME|TIMESTAMP)(?:\((?<p>\d)\))?$", RegexOptions.IgnoreCase);
            if (precision.Success && Regex.IsMatch(value.Trim(), @"^(?:CURRENT_TIMESTAMP|NOW|LOCALTIMESTAMP)\s*(?:\(\s*\d?\s*\))?$", RegexOptions.IgnoreCase))
            {
                var p = precision.Groups["p"].Success ? $"({precision.Groups["p"].Value})" : string.Empty;
                return $" DEFAULT CURRENT_TIMESTAMP{p}";
            }

            return $" DEFAULT ({value})";
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
            if (lite)
            {
                return (string.Empty, string.Empty);
            }

            var deploy = new List<string>();
            var revert = new List<string>();
            var cascades = false;
            foreach (var table in ctx.Schema.Tables)
            {
                var schema = ctx.TargetName(table.Name).Schema;
                var tableName = ctx.Qualified(table.Name);
                foreach (var fk in table.ForeignKeys)
                {
                    var name = ctx.Quote(Unique(schema, fk.Name ?? (pg || my ? Generated(table.Name.Name, string.Join('_', fk.Columns) + "_fkey") : $"FK_{table.Name.Name}_{fk.Referenced.Name}"), table.Name.Name));
                    if (my && (fk.OnDelete == "SET DEFAULT" || fk.OnUpdate == "SET DEFAULT"))
                    {
                        ctx.Issue(table.Name.ToString(), $"foreign key {fk.Name}: InnoDB does not support SET DEFAULT; it became NO ACTION");
                    }
                    var sb = new StringBuilder($"ALTER TABLE {tableName} ADD CONSTRAINT {name} FOREIGN KEY ({Columns(fk.Columns)}) REFERENCES {ctx.Qualified(fk.Referenced)} ({Columns(fk.ReferencedColumns)})");
                    sb.Append(Action("DELETE", fk.OnDelete)).Append(Action("UPDATE", fk.OnUpdate));
                    cascades |= fk.OnDelete is not ("NO ACTION" or "RESTRICT") || fk.OnUpdate is not ("NO ACTION" or "RESTRICT");
                    if (pg)
                    {
                        sb.Append(" DEFERRABLE INITIALLY IMMEDIATE");
                    }

                    deploy.Add(sb.Append(';').ToString());
                    revert.Add(my ? $"ALTER TABLE {tableName} DROP FOREIGN KEY {name};" : $"ALTER TABLE {tableName} DROP CONSTRAINT {name};");
                }
            }

            if (ss && cascades)
            {
                ctx.Issue("foreign keys", "SQL Server rejects ON DELETE/UPDATE actions that could reach a table along two paths or a cycle; if 'trek deploy' reports that, change one of them to NO ACTION and handle it in a trigger");
            }

            return Ddl(deploy, revert);
        }

        private string Action(string verb, string action) => action switch
        {
            "NO ACTION" => string.Empty,
            "SET DEFAULT" when my => string.Empty,
            "RESTRICT" => ss ? string.Empty : $" ON {verb} RESTRICT",
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
                var schema = ctx.TargetName(table.Name).Schema;
                var tableName = ctx.Qualified(table.Name);
                foreach (var index in table.Indexes)
                {
                    var method = index.Method;
                    if (method is "columnstore" or "gin" or "gist" or "spgist")
                    {
                        ctx.Issue(owner, $"{method} index {index.Name} is not converted");
                        continue;
                    }

                    if (ss && index.Columns.Any(c => c.IsExpression))
                    {
                        ctx.Issue(owner, $"expression index {index.Name} is not converted: add a computed column and index it");
                        continue;
                    }

                    var kind = my && method is "FULLTEXT" or "SPATIAL" ? method + " " : string.Empty;
                    if (method is "FULLTEXT" or "SPATIAL" && !my)
                    {
                        ctx.Issue(owner, $"MySQL {method} index {index.Name} became a B-tree index; use the target's full-text or spatial features instead");
                    }
                    else if (method is not null and not "btree" && kind.Length == 0)
                    {
                        ctx.Issue(owner, $"{method} index {index.Name} became a B-tree index");
                    }

                    if (my && index.Filter is not null && index.Unique)
                    {
                        ctx.Issue(owner, $"filtered unique index {index.Name} is not converted: MySQL has no filtered indexes; enforce the rule with a generated column or a trigger");
                        continue;
                    }

                    var name = ctx.Quote(Unique(schema, index.Name, table.Name.Name));
                    string Key(PortableIndexColumn c)
                    {
                        if (!c.IsExpression)
                        {
                            return ctx.Ident(c.Text);
                        }

                        var expression = translator.Expression(c.Text, new ExpressionSite(owner));
                        // MySQL cannot index an expression whose result is TEXT: one over a text column is cast to CHAR(255).
                        if (my && table.Columns.Any(col => ctx.KindOf(col.Type) == ConversionContext.ColumnKind.Text
                            && Regex.IsMatch(c.Text, $@"(?<![\w$]){Regex.Escape(col.Name)}(?![\w$])", RegexOptions.IgnoreCase)))
                        {
                            ctx.Issue(owner, $"index {index.Name}: the expression is cast to CHAR(255) because MySQL cannot index TEXT; MySQL uses the index only for queries that write the same CAST");
                            return $"(CAST({expression} AS CHAR(255)))";
                        }

                        return $"({expression})";
                    }

                    var columns = string.Join(", ", index.Columns.Select(c => Key(c) + (c.Descending ? " DESC" : string.Empty)));
                    var sb = new StringBuilder($"CREATE {(index.Unique ? "UNIQUE " : string.Empty)}{kind}{(ss ? "NONCLUSTERED " : string.Empty)}INDEX {name} ON {tableName} ({columns})");
                    if (index.Included.Count > 0 && (lite || my))
                    {
                        ctx.Issue(owner, $"index {index.Name}: {(my ? "MySQL" : "SQLite")} has no INCLUDE columns; they were left out");
                    }
                    else if (index.Included.Count > 0)
                    {
                        sb.Append($" INCLUDE ({Columns(index.Included)})");
                    }

                    if (index.Filter is not null && my)
                    {
                        ctx.Issue(owner, $"index {index.Name}: MySQL has no filtered indexes; it indexes every row");
                    }
                    else if (index.Filter is not null)
                    {
                        sb.Append(" WHERE ").Append(translator.Predicate(StripParens(index.Filter), new ExpressionSite(owner)));
                    }

                    deploy.Add(sb.Append(';').ToString());
                    revert.Add(lite ? $"DROP INDEX {name};" : pg ? $"DROP INDEX {ctx.Quote(schema)}.{name};" : $"DROP INDEX {name} ON {tableName};");
                }
            }

            return Ddl(deploy, revert);
        }
    }
}
