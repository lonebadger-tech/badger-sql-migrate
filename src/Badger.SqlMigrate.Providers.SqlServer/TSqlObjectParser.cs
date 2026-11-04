using System.Text;
using System.Text.RegularExpressions;
using Badger.SqlMigrate.Core.Import;
using Microsoft.SqlServer.TransactSql.ScriptDom;

namespace Badger.SqlMigrate.Providers.SqlServer;

/// <summary>
/// Reads SQL project (.sqlproj) scripts with ScriptDom: what each statement creates, what it references, how to
/// turn a module into CREATE OR ALTER and how to undo it. Names are taken from the original text, so $(variables)
/// in names survive.
/// </summary>
public sealed partial class TSqlObjectParser : ISqlObjectParser
{
    public SqlParseResult Parse(string script)
    {
        // $(name) is not T-SQL: replace it with a same-length identifier so offsets still match the original text.
        var parseable = SqlCmdVariableRegex().Replace(script, m => new string('x', m.Length));
        var parser = new TSql170Parser(initialQuotedIdentifiers: true);
        using var reader = new StringReader(parseable);
        var fragment = parser.Parse(reader, out var errors);
        if (errors.Count > 0 || fragment is not TSqlScript tsql)
        {
            return new SqlParseResult([], errors.Select(e => $"line {e.Line}: {e.Message}").ToList());
        }

        var statements = new List<SqlStatementInfo>();
        for (var b = 0; b < tsql.Batches.Count; b++)
        {
            foreach (var statement in tsql.Batches[b].Statements)
            {
                statements.Add(new Reader(script).Read(statement, b));
            }
        }

        return new SqlParseResult(statements, []);
    }

    [GeneratedRegex(@"\$\([A-Za-z_][A-Za-z0-9_.]*\)")]
    private static partial Regex SqlCmdVariableRegex();

    private sealed class Reader(string text)
    {
        private string Text(TSqlFragment f) => text.Substring(f.StartOffset, f.FragmentLength);

        /// <summary>An identifier's value from the original text (keeps $(var) text), without brackets or quotes.</summary>
        private string Value(Identifier id)
        {
            var raw = Text(id);
            return raw.Length >= 2 && ((raw[0] == '[' && raw[^1] == ']') || (raw[0] == '"' && raw[^1] == '"'))
                ? raw[1..^1].Replace("]]", "]", StringComparison.Ordinal).Replace("\"\"", "\"", StringComparison.Ordinal)
                : raw;
        }

        /// <summary>A one- or two-part name; null for names in another database or server.</summary>
        private SqlObjectName? Name(SchemaObjectName? name) =>
            name?.BaseIdentifier is null || name.DatabaseIdentifier is not null || name.ServerIdentifier is not null
                ? null
                : new SqlObjectName(name.SchemaIdentifier is null ? null : Value(name.SchemaIdentifier), Value(name.BaseIdentifier));

        public SqlStatementInfo Read(TSqlStatement statement, int batch)
        {
            var info = new SqlStatementInfo { Kind = SqlObjectKind.Other, Offset = statement.StartOffset, Length = statement.FragmentLength, Batch = batch };
            var collector = new References(this);
            statement.Accept(collector);
            info = Describe(statement, info);
            var self = info.Target;
            var references = collector.Found
                .Where(r => !(r.Kind == SqlReferenceKind.Object && self is not null && info.Kind < SqlObjectKind.Index && r.Name == self))
                .Where(r => !(r.Kind == SqlReferenceKind.Object && r.Name.Schema is null && collector.CteNames.Contains(r.Name.Name)))
                .Distinct()
                .ToList();
            return info with { References = references };
        }

        private SqlStatementInfo Describe(TSqlStatement statement, SqlStatementInfo info)
        {
            switch (statement)
            {
                case CreateSchemaStatement s:
                    return info with { Kind = SqlObjectKind.Schema, Target = new(null, Value(s.Name)), Revert = $"DROP SCHEMA {Text(s.Name)};" };
                case CreateTableStatement t:
                    return Table(t, info);
                case ViewStatementBody v when v is not AlterViewStatement:
                    return Module(v, info, SqlObjectKind.View, v.SchemaObjectName, "VIEW",
                        v.ViewOptions.Any(o => o.OptionKind == ViewOptionKind.SchemaBinding), deferred: false);
                case ProcedureStatementBody p when p is not AlterProcedureStatement:
                    return Module(p, info, SqlObjectKind.Procedure, p.ProcedureReference.Name, "PROCEDURE", false, deferred: true);
                case FunctionStatementBody f when f is not AlterFunctionStatement:
                    var schemaBound = f.Options.Any(o => o.OptionKind == FunctionOptionKind.SchemaBinding);
                    return Module(f, info, SqlObjectKind.Function, f.Name, "FUNCTION", schemaBound,
                        deferred: !schemaBound && f.ReturnType is not SelectFunctionReturnType && f.MethodSpecifier is null);
                case TriggerStatementBody tr when tr is not AlterTriggerStatement:
                    var server = tr.TriggerObject.TriggerScope == TriggerScope.AllServer;
                    var database = tr.TriggerObject.TriggerScope == TriggerScope.Database;
                    var revert = $"DROP TRIGGER {Text(tr.Name)}{(server ? " ON ALL SERVER" : database ? " ON DATABASE" : string.Empty)};";
                    return Module(tr, info, SqlObjectKind.Trigger, tr.Name, "TRIGGER", false, deferred: true) with { Revert = revert };
                case CreateTypeStatement ty:
                    return info with { Kind = SqlObjectKind.Type, Target = Name(ty.Name), Revert = $"DROP TYPE {Text(ty.Name)};" };
                case CreateSequenceStatement sq:
                    return info with { Kind = SqlObjectKind.Sequence, Target = Name(sq.Name), Revert = $"DROP SEQUENCE {Text(sq.Name)};" };
                case CreateSynonymStatement sy:
                    return info with { Kind = SqlObjectKind.Synonym, Target = Name(sy.Name), Revert = $"DROP SYNONYM {Text(sy.Name)};" };
                case CreateFullTextIndexStatement ft:
                    return info with { Kind = SqlObjectKind.FullText, Target = Name(ft.OnName), Revert = $"DROP FULLTEXT INDEX ON {Text(ft.OnName)};" };
                case CreateFullTextCatalogStatement fc:
                    return info with { Kind = SqlObjectKind.FullText, Target = new(null, Value(fc.Name)), Revert = $"DROP FULLTEXT CATALOG {Text(fc.Name)};" };
                case IndexStatement ix when statement is CreateIndexStatement or CreateXmlIndexStatement or CreateSelectiveXmlIndexStatement or CreateSpatialIndexStatement:
                    return info with { Kind = SqlObjectKind.Index, Target = Name(ix.OnName), ChildName = Value(ix.Name), Revert = $"DROP INDEX {Text(ix.Name)} ON {Text(ix.OnName)};" };
                case CreateColumnStoreIndexStatement cs:
                    return info with { Kind = SqlObjectKind.Index, Target = Name(cs.OnName), ChildName = Value(cs.Name), Revert = $"DROP INDEX {Text(cs.Name)} ON {Text(cs.OnName)};" };
                case AlterTableAddTableElementStatement add:
                    var constraints = add.Definition.TableConstraints;
                    var named = constraints.Count == 1 && constraints[0].ConstraintIdentifier is { } id ? id : null;
                    return info with
                    {
                        Kind = constraints.Count > 0 && add.Definition.ColumnDefinitions.Count == 0 ? SqlObjectKind.Constraint : SqlObjectKind.Other,
                        Target = Name(add.SchemaObjectName),
                        ChildName = named is null ? null : Value(named),
                        Revert = named is null || add.Definition.ColumnDefinitions.Count > 0 ? null : $"ALTER TABLE {Text(add.SchemaObjectName)} DROP CONSTRAINT {Text(named)};",
                    };
                case AlterTableStatement at:
                    return info with { Kind = at is AlterTableConstraintModificationStatement ? SqlObjectKind.Constraint : SqlObjectKind.Other, Target = Name(at.SchemaObjectName) };
                case CreateUserStatement u:
                    return info with { Kind = SqlObjectKind.User, Target = new(null, Value(u.Name)), Revert = $"DROP USER {Text(u.Name)};" };
                case CreateRoleStatement r:
                    return info with { Kind = SqlObjectKind.Role, Target = new(null, Value(r.Name)), Revert = $"DROP ROLE {Text(r.Name)};" };
                case CreateApplicationRoleStatement ar:
                    return info with { Kind = SqlObjectKind.Role, Target = new(null, Value(ar.Name)), Revert = $"DROP APPLICATION ROLE {Text(ar.Name)};" };
                case AlterRoleStatement { Action: AddMemberAlterRoleAction member } role:
                    return info with { Kind = SqlObjectKind.RoleMembership, Target = new(null, Value(role.Name)), Revert = $"ALTER ROLE {Text(role.Name)} DROP MEMBER {Text(member.Member)};" };
                case SecurityStatement sec:
                    return info with { Kind = SqlObjectKind.Permission, Target = sec.SecurityTargetObject?.ObjectName?.MultiPartIdentifier is { } target ? MultiPart(target) : null };
                case CreatePartitionFunctionStatement pf:
                    return info with { Kind = SqlObjectKind.PartitionFunction, Target = new(null, Value(pf.Name)), Revert = $"DROP PARTITION FUNCTION {Text(pf.Name)};" };
                case CreatePartitionSchemeStatement ps:
                    return info with { Kind = SqlObjectKind.PartitionScheme, Target = new(null, Value(ps.Name)), Revert = $"DROP PARTITION SCHEME {Text(ps.Name)};" };
                case CreateXmlSchemaCollectionStatement xs:
                    return info with { Kind = SqlObjectKind.XmlSchemaCollection, Target = Name(xs.Name), Revert = $"DROP XML SCHEMA COLLECTION {Text(xs.Name)};" };
                case CreateAssemblyStatement asm:
                    return info with { Kind = SqlObjectKind.Assembly, Target = new(null, Value(asm.Name)), Revert = $"DROP ASSEMBLY {Text(asm.Name)};" };
                case ExecuteStatement { ExecuteSpecification.ExecutableEntity: ExecutableProcedureReference { ProcedureReference.ProcedureReference.Name: { } proc } call }:
                    var procName = proc.BaseIdentifier.Value;
                    if (procName.Equals("sp_addextendedproperty", StringComparison.OrdinalIgnoreCase))
                    {
                        return info with { Kind = SqlObjectKind.ExtendedProperty, Target = ExtendedPropertyTarget(call) };
                    }

                    if (procName.Equals("sp_addrolemember", StringComparison.OrdinalIgnoreCase) && call.Parameters.Count == 2)
                    {
                        var roleName = StringValue(call.Parameters[0]);
                        var memberName = StringValue(call.Parameters[1]);
                        return roleName is null || memberName is null
                            ? info with { Kind = SqlObjectKind.RoleMembership }
                            : info with
                            {
                                Kind = SqlObjectKind.RoleMembership,
                                Target = new(null, roleName),
                                Revert = $"ALTER ROLE [{roleName.Replace("]", "]]", StringComparison.Ordinal)}] DROP MEMBER [{memberName.Replace("]", "]]", StringComparison.Ordinal)}];",
                            };
                    }

                    return info;
                default:
                    return info;
            }
        }

        private SqlStatementInfo Module(TSqlStatement statement, SqlStatementInfo info, SqlObjectKind kind, SchemaObjectName name, string keyword, bool schemaBound, bool deferred)
        {
            SqlTextEdit? orAlter = null;
            var isCreateOrAlter = statement.GetType().Name.StartsWith("CreateOrAlter", StringComparison.Ordinal);
            if (!isCreateOrAlter)
            {
                var first = statement.ScriptTokenStream[statement.FirstTokenIndex];
                if (first.TokenType == TSqlTokenType.Create)
                {
                    orAlter = new SqlTextEdit(first.Offset + first.Text.Length, 0, " OR ALTER");
                }
            }

            return info with
            {
                Kind = kind,
                Target = Name(name),
                CreateOrAlter = orAlter,
                SchemaBound = schemaBound,
                Deferred = deferred,
                Revert = $"DROP {keyword} {Text(name)};",
            };
        }

        private SqlStatementInfo Table(CreateTableStatement table, SqlStatementInfo info)
        {
            var name = Text(table.SchemaObjectName);
            var revert = new StringBuilder();
            if (table.Options.OfType<SystemVersioningTableOption>().FirstOrDefault() is { OptionState: OptionState.On } versioning)
            {
                revert.Append($"ALTER TABLE {name} SET (SYSTEM_VERSIONING = OFF);\n");
                revert.Append($"DROP TABLE {name};\n");
                if (versioning.HistoryTable is not null)
                {
                    revert.Append($"DROP TABLE {Text(versioning.HistoryTable)};\n");
                }
            }
            else
            {
                revert.Append($"DROP TABLE {name};\n");
            }

            var foreignKeys = new List<SqlForeignKey>();
            if (table.Definition is { } definition)
            {
                foreach (var fk in definition.TableConstraints.OfType<ForeignKeyConstraintDefinition>())
                {
                    foreignKeys.Add(ForeignKey(table, fk, column: null));
                }

                foreach (var column in definition.ColumnDefinitions)
                {
                    foreach (var fk in column.Constraints.OfType<ForeignKeyConstraintDefinition>())
                    {
                        foreignKeys.Add(ForeignKey(table, fk, column));
                    }
                }
            }

            return info with { Kind = SqlObjectKind.Table, Target = Name(table.SchemaObjectName), Revert = revert.ToString().TrimEnd('\n'), ForeignKeys = foreignKeys };
        }

        private SqlForeignKey ForeignKey(CreateTableStatement table, ForeignKeyConstraintDefinition fk, ColumnDefinition? column)
        {
            var tokens = fk.ScriptTokenStream;
            var start = fk.StartOffset;
            var end = fk.StartOffset + fk.FragmentLength;
            SqlTextEdit removal;
            if (column is null)
            {
                // Table-level: remove ", CONSTRAINT ... REFERENCES ..." including the comma before it.
                var i = fk.FirstTokenIndex - 1;
                while (i >= 0 && tokens[i].TokenType is TSqlTokenType.WhiteSpace or TSqlTokenType.SingleLineComment or TSqlTokenType.MultilineComment)
                {
                    i--;
                }

                var removeFrom = i >= 0 && tokens[i].TokenType == TSqlTokenType.Comma ? tokens[i].Offset : start;
                removal = new SqlTextEdit(removeFrom, end - removeFrom, string.Empty);
            }
            else
            {
                removal = new SqlTextEdit(start, end - start, string.Empty);
            }

            var name = fk.ConstraintIdentifier is null ? null : Value(fk.ConstraintIdentifier);
            var tableName = Text(table.SchemaObjectName);
            string definition;
            if (column is null)
            {
                definition = text[start..end];
            }
            else
            {
                // Column-level "[CONSTRAINT n] [FOREIGN KEY] REFERENCES t (c)" becomes "[CONSTRAINT n] FOREIGN KEY (col) REFERENCES t (c)".
                var references = Enumerable.Range(fk.FirstTokenIndex, fk.LastTokenIndex - fk.FirstTokenIndex + 1)
                    .First(t => tokens[t].TokenType == TSqlTokenType.References);
                var prefix = fk.ConstraintIdentifier is null ? string.Empty : $"CONSTRAINT {Text(fk.ConstraintIdentifier)} ";
                definition = $"{prefix}FOREIGN KEY ({Text(column.ColumnIdentifier)}) {text[tokens[references].Offset..end]}";
            }

            var referenced = Name(fk.ReferenceTableName) ?? new SqlObjectName(null, fk.ReferenceTableName.BaseIdentifier.Value);
            var drop = fk.ConstraintIdentifier is not null
                ? $"ALTER TABLE {tableName} DROP CONSTRAINT {Text(fk.ConstraintIdentifier)};"
                : $"""
                   DECLARE @fk sysname = (SELECT TOP (1) name FROM sys.foreign_keys WHERE parent_object_id = OBJECT_ID(N'{Escape(tableName)}') AND referenced_object_id = OBJECT_ID(N'{Escape(Text(fk.ReferenceTableName))}') AND is_system_named = 1);
                   IF @fk IS NOT NULL EXEC (N'ALTER TABLE {Escape(tableName)} DROP CONSTRAINT ' + QUOTENAME(@fk));
                   """;
            return new SqlForeignKey(name, referenced, removal, $"ALTER TABLE {tableName} ADD {definition};", drop);
        }

        private static string Escape(string sql) => sql.Replace("'", "''", StringComparison.Ordinal);

        private SqlObjectName? MultiPart(MultiPartIdentifier id) => id.Identifiers.Count switch
        {
            1 => new SqlObjectName(null, Value(id.Identifiers[0])),
            2 => new SqlObjectName(Value(id.Identifiers[0]), Value(id.Identifiers[1])),
            _ => null,
        };

        private static string? StringValue(ExecuteParameter parameter) => parameter.ParameterValue switch
        {
            StringLiteral s => s.Value,
            ColumnReferenceExpression { MultiPartIdentifier.Identifiers: [var single] } => single.Value,
            _ => null,
        };

        /// <summary>The object an sp_addextendedproperty call describes (@level1name in @level0name), when given as literals.</summary>
        private static SqlObjectName? ExtendedPropertyTarget(ExecutableProcedureReference call)
        {
            var named = call.Parameters.Where(p => p.Variable is not null).ToDictionary(p => p.Variable.Name.TrimStart('@'), StringValue, StringComparer.OrdinalIgnoreCase);
            if (named.Count == 0 && call.Parameters.Count >= 6)
            {
                named = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase)
                {
                    ["level0name"] = StringValue(call.Parameters[3]),
                    ["level1name"] = StringValue(call.Parameters[5]),
                };
            }

            return named.GetValueOrDefault("level1name") is { } name ? new SqlObjectName(named.GetValueOrDefault("level0name"), name) : null;
        }

        /// <summary>Collects every object, principal, schema and partition scheme a statement names.</summary>
        private sealed class References(Reader reader) : TSqlFragmentVisitor
        {
            public List<SqlReference> Found { get; } = [];

            public HashSet<string> CteNames { get; } = new(StringComparer.OrdinalIgnoreCase);

            public override void Visit(SchemaObjectName node)
            {
                if (reader.Name(node) is { } name && !name.Name.StartsWith('#') && !name.Name.StartsWith('@'))
                {
                    Found.Add(new SqlReference(SqlReferenceKind.Object, name));
                }
            }

            public override void Visit(CommonTableExpression node) => CteNames.Add(node.ExpressionName.Value);

            public override void Visit(FunctionCall node)
            {
                if (node.CallTarget is MultiPartIdentifierCallTarget { MultiPartIdentifier.Identifiers.Count: 1 } target)
                {
                    Found.Add(new SqlReference(SqlReferenceKind.Object, new SqlObjectName(reader.Value(target.MultiPartIdentifier.Identifiers[0]), reader.Value(node.FunctionName))));
                }
            }

            public override void Visit(SecurityPrincipal node)
            {
                if (node.Identifier is not null)
                {
                    Found.Add(new SqlReference(SqlReferenceKind.Principal, new SqlObjectName(null, reader.Value(node.Identifier))));
                }
            }

            public override void Visit(CreateSchemaStatement node) => Principal(node.Owner);

            public override void Visit(CreateRoleStatement node) => Principal(node.Owner);

            public override void Visit(AlterRoleStatement node)
            {
                Principal(node.Name);
                if (node.Action is AddMemberAlterRoleAction member)
                {
                    Principal(member.Member);
                }
            }

            public override void Visit(SecurityTargetObject node)
            {
                if (node.ObjectName?.MultiPartIdentifier is { } id && reader.MultiPart(id) is { } name)
                {
                    Found.Add(new SqlReference(node.ObjectKind == SecurityObjectKind.Schema ? SqlReferenceKind.Schema : SqlReferenceKind.Object, name));
                }
            }

            public override void Visit(ExecutableProcedureReference node)
            {
                var proc = node.ProcedureReference?.ProcedureReference?.Name?.BaseIdentifier?.Value;
                if (string.Equals(proc, "sp_addrolemember", StringComparison.OrdinalIgnoreCase))
                {
                    foreach (var value in node.Parameters.Select(StringValue).OfType<string>())
                    {
                        Found.Add(new SqlReference(SqlReferenceKind.Principal, new SqlObjectName(null, value)));
                    }
                }
            }

            public override void Visit(FileGroupOrPartitionScheme node)
            {
                if (node.Name?.Identifier is { } id)
                {
                    Found.Add(new SqlReference(SqlReferenceKind.Partition, new SqlObjectName(null, reader.Value(id))));
                }
            }

            public override void Visit(CreatePartitionSchemeStatement node) =>
                Found.Add(new SqlReference(SqlReferenceKind.Partition, new SqlObjectName(null, reader.Value(node.PartitionFunction))));

            private void Principal(Identifier? id)
            {
                if (id is not null)
                {
                    Found.Add(new SqlReference(SqlReferenceKind.Principal, new SqlObjectName(null, reader.Value(id))));
                }
            }
        }
    }
}
