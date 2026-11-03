using System.Globalization;
using System.Text.RegularExpressions;
using Badger.SqlMigrate.Core;
using Badger.SqlMigrate.Core.Providers;

namespace Badger.SqlMigrate.Providers.SqlServer;

/// <summary>
/// The bsm journal in the target database:
/// [schema].[changes], [repeatables], [events], [schema_snapshots], [meta].
/// All writes are parameterized (required for Always Encrypted connections).
/// </summary>
public sealed partial class SqlServerStateStore : IStateStore
{
    /// <summary>Version of the state table layout; bump with an upgrade step in <see cref="EnsureCreatedAsync"/>.</summary>
    public const int StateVersion = 1;

    public async Task<bool> ExistsAsync(IDatabaseSession session, string schema, CancellationToken cancellationToken)
    {
        var rows = await session.QueryAsync(
            "SELECT CASE WHEN OBJECT_ID(@name, N'U') IS NULL THEN 0 ELSE 1 END AS present;",
            new Dictionary<string, object?> { ["name"] = $"{Quote(schema)}.[changes]" },
            cancellationToken).ConfigureAwait(false);
        return Convert.ToInt32(rows[0]["present"], CultureInfo.InvariantCulture) == 1;
    }

    public async Task EnsureCreatedAsync(IDatabaseSession session, string schema, CancellationToken cancellationToken)
    {
        var s = Quote(schema);
        var p = new Dictionary<string, object?> { ["schema"] = schema };
        await session.ExecuteAsync($"IF SCHEMA_ID(@schema) IS NULL EXEC(N'CREATE SCHEMA {s.Replace("'", "''", StringComparison.Ordinal)}');", p, cancellationToken).ConfigureAwait(false);

        await session.ExecuteAsync($"""
            IF OBJECT_ID(N'{s}.[meta]', N'U') IS NULL
            CREATE TABLE {s}.[meta] (
                [key] nvarchar(128) NOT NULL CONSTRAINT [PK_badger_meta] PRIMARY KEY,
                [value] nvarchar(4000) NOT NULL);

            IF OBJECT_ID(N'{s}.[changes]', N'U') IS NULL
            CREATE TABLE {s}.[changes] (
                [sequence] bigint IDENTITY(1,1) NOT NULL CONSTRAINT [PK_badger_changes] PRIMARY KEY,
                [name] nvarchar(256) NOT NULL CONSTRAINT [UQ_badger_changes_name] UNIQUE,
                [script_hash] char(64) NOT NULL,
                [chain_id] char(64) NOT NULL,
                [deploy_script] nvarchar(max) NOT NULL,
                [revert_script] nvarchar(max) NULL,
                [verify_script] nvarchar(max) NULL,
                [flags] int NOT NULL,
                [deployed_at] datetimeoffset(3) NOT NULL,
                [deployed_by] nvarchar(256) NOT NULL,
                [host] nvarchar(256) NOT NULL,
                [tool_version] nvarchar(64) NOT NULL,
                [duration_ms] bigint NOT NULL,
                [applied_out_of_order] bit NOT NULL,
                [baselined] bit NOT NULL,
                [rendered_hash] char(64) NULL,
                [rendered_revert_hash] char(64) NULL);

            IF OBJECT_ID(N'{s}.[repeatables]', N'U') IS NULL
            CREATE TABLE {s}.[repeatables] (
                [name] nvarchar(450) NOT NULL CONSTRAINT [PK_badger_repeatables] PRIMARY KEY,
                [hash] char(64) NOT NULL,
                [script] nvarchar(max) NOT NULL,
                [previous_hash] char(64) NULL,
                [previous_script] nvarchar(max) NULL,
                [applied_at] datetimeoffset(3) NOT NULL,
                [applied_by] nvarchar(256) NOT NULL);

            IF OBJECT_ID(N'{s}.[events]', N'U') IS NULL
            CREATE TABLE {s}.[events] (
                [id] bigint IDENTITY(1,1) NOT NULL CONSTRAINT [PK_badger_events] PRIMARY KEY,
                [kind] nvarchar(32) NOT NULL,
                [name] nvarchar(450) NOT NULL,
                [script_hash] char(64) NULL,
                [chain_id] char(64) NULL,
                [message] nvarchar(max) NULL,
                [at] datetimeoffset(3) NOT NULL,
                [actor] nvarchar(256) NOT NULL,
                [host] nvarchar(256) NOT NULL,
                [tool_version] nvarchar(64) NOT NULL,
                [environment] nvarchar(128) NULL);

            IF OBJECT_ID(N'{s}.[schema_snapshots]', N'U') IS NULL
            CREATE TABLE {s}.[schema_snapshots] (
                [id] bigint IDENTITY(1,1) NOT NULL CONSTRAINT [PK_badger_schema_snapshots] PRIMARY KEY,
                [fingerprint] char(64) NOT NULL,
                [dump] nvarchar(max) NOT NULL,
                [captured_at] datetimeoffset(3) NOT NULL,
                [head_chain_id] char(64) NULL);
            """, null, cancellationToken).ConfigureAwait(false);

        var rows = await session.QueryAsync($"SELECT [value] FROM {s}.[meta] WHERE [key] = N'state_version';", null, cancellationToken).ConfigureAwait(false);
        if (rows.Count == 0)
        {
            await session.ExecuteAsync(
                $"INSERT INTO {s}.[meta] ([key], [value]) VALUES (N'state_version', @v);",
                new Dictionary<string, object?> { ["v"] = StateVersion.ToString(CultureInfo.InvariantCulture) },
                cancellationToken).ConfigureAwait(false);
        }
        else if (int.Parse((string)rows[0]["value"]!, CultureInfo.InvariantCulture) > StateVersion)
        {
            throw new MigrationException($"The bsm state tables in {s} were created by a newer version of Badger SQL Migrate (state version {rows[0]["value"]}). Upgrade the tool.");
        }
    }

    public async Task<string?> GetMetaAsync(IDatabaseSession session, string schema, string key, CancellationToken cancellationToken)
    {
        var rows = await session.QueryAsync($"SELECT [value] FROM {Quote(schema)}.[meta] WHERE [key] = @key;", new Dictionary<string, object?> { ["key"] = key }, cancellationToken).ConfigureAwait(false);
        return rows.Count == 0 ? null : (string?)rows[0]["value"];
    }

    public Task SetMetaAsync(IDatabaseSession session, string schema, string key, string value, CancellationToken cancellationToken) =>
        session.ExecuteAsync($"""
            UPDATE {Quote(schema)}.[meta] SET [value] = @value WHERE [key] = @key;
            IF @@ROWCOUNT = 0 INSERT INTO {Quote(schema)}.[meta] ([key], [value]) VALUES (@key, @value);
            """, new Dictionary<string, object?> { ["key"] = key, ["value"] = value }, cancellationToken);

    public async Task<IReadOnlyList<AppliedChange>> GetAppliedChangesAsync(IDatabaseSession session, string schema, CancellationToken cancellationToken)
    {
        var rows = await session.QueryAsync($"SELECT * FROM {Quote(schema)}.[changes] ORDER BY [sequence];", null, cancellationToken).ConfigureAwait(false);
        return rows.Select(r => new AppliedChange
        {
            Sequence = (long)r["sequence"]!,
            Name = (string)r["name"]!,
            ScriptHash = (string)r["script_hash"]!,
            ChainId = (string)r["chain_id"]!,
            DeployScript = (string)r["deploy_script"]!,
            RevertScript = (string?)r["revert_script"],
            VerifyScript = (string?)r["verify_script"],
            Flags = (int)r["flags"]!,
            DeployedAtUtc = (DateTimeOffset)r["deployed_at"]!,
            DeployedBy = (string)r["deployed_by"]!,
            Host = (string)r["host"]!,
            ToolVersion = (string)r["tool_version"]!,
            DurationMs = (long)r["duration_ms"]!,
            AppliedOutOfOrder = (bool)r["applied_out_of_order"]!,
            Baselined = (bool)r["baselined"]!,
            RenderedHash = (string?)r["rendered_hash"],
            RenderedRevertHash = (string?)r["rendered_revert_hash"],
        }).ToList();
    }

    public Task InsertChangeAsync(IDatabaseSession session, string schema, AppliedChange change, CancellationToken cancellationToken) =>
        session.ExecuteAsync($"""
            INSERT INTO {Quote(schema)}.[changes]
                ([name], [script_hash], [chain_id], [deploy_script], [revert_script], [verify_script], [flags],
                 [deployed_at], [deployed_by], [host], [tool_version], [duration_ms], [applied_out_of_order], [baselined],
                 [rendered_hash], [rendered_revert_hash])
            VALUES
                (@name, @script_hash, @chain_id, @deploy_script, @revert_script, @verify_script, @flags,
                 @deployed_at, @deployed_by, @host, @tool_version, @duration_ms, @applied_out_of_order, @baselined,
                 @rendered_hash, @rendered_revert_hash);
            """,
            new Dictionary<string, object?>
            {
                ["name"] = change.Name,
                ["script_hash"] = change.ScriptHash,
                ["chain_id"] = change.ChainId,
                ["deploy_script"] = change.DeployScript,
                ["revert_script"] = change.RevertScript,
                ["verify_script"] = change.VerifyScript,
                ["flags"] = change.Flags,
                ["deployed_at"] = change.DeployedAtUtc,
                ["deployed_by"] = change.DeployedBy,
                ["host"] = change.Host,
                ["tool_version"] = change.ToolVersion,
                ["duration_ms"] = change.DurationMs,
                ["applied_out_of_order"] = change.AppliedOutOfOrder,
                ["baselined"] = change.Baselined,
                ["rendered_hash"] = change.RenderedHash,
                ["rendered_revert_hash"] = change.RenderedRevertHash,
            },
            cancellationToken);

    public Task DeleteChangeAsync(IDatabaseSession session, string schema, string name, CancellationToken cancellationToken) =>
        session.ExecuteAsync($"DELETE FROM {Quote(schema)}.[changes] WHERE [name] = @name;", new Dictionary<string, object?> { ["name"] = name }, cancellationToken);

    public Task UpdateChangeAsync(IDatabaseSession session, string schema, AppliedChange change, CancellationToken cancellationToken) =>
        session.ExecuteAsync($"""
            UPDATE {Quote(schema)}.[changes]
               SET [script_hash] = @script_hash, [chain_id] = @chain_id, [deploy_script] = @deploy_script,
                   [revert_script] = @revert_script, [verify_script] = @verify_script, [flags] = @flags,
                   [rendered_hash] = @rendered_hash, [rendered_revert_hash] = @rendered_revert_hash
             WHERE [name] = @name;
            """,
            new Dictionary<string, object?>
            {
                ["name"] = change.Name,
                ["script_hash"] = change.ScriptHash,
                ["chain_id"] = change.ChainId,
                ["deploy_script"] = change.DeployScript,
                ["revert_script"] = change.RevertScript,
                ["verify_script"] = change.VerifyScript,
                ["flags"] = change.Flags,
                ["rendered_hash"] = change.RenderedHash,
                ["rendered_revert_hash"] = change.RenderedRevertHash,
            },
            cancellationToken);

    public async Task<IReadOnlyList<AppliedRepeatable>> GetRepeatablesAsync(IDatabaseSession session, string schema, CancellationToken cancellationToken)
    {
        var rows = await session.QueryAsync($"SELECT * FROM {Quote(schema)}.[repeatables] ORDER BY [name];", null, cancellationToken).ConfigureAwait(false);
        return rows.Select(r => new AppliedRepeatable
        {
            Name = (string)r["name"]!,
            Hash = (string)r["hash"]!,
            Script = (string)r["script"]!,
            PreviousHash = (string?)r["previous_hash"],
            PreviousScript = (string?)r["previous_script"],
            AppliedAtUtc = (DateTimeOffset)r["applied_at"]!,
            AppliedBy = (string)r["applied_by"]!,
        }).ToList();
    }

    public Task UpsertRepeatableAsync(IDatabaseSession session, string schema, AppliedRepeatable repeatable, CancellationToken cancellationToken) =>
        session.ExecuteAsync($"""
            UPDATE {Quote(schema)}.[repeatables]
               SET [hash] = @hash, [script] = @script, [previous_hash] = @previous_hash, [previous_script] = @previous_script,
                   [applied_at] = @applied_at, [applied_by] = @applied_by
             WHERE [name] = @name;
            IF @@ROWCOUNT = 0
                INSERT INTO {Quote(schema)}.[repeatables] ([name], [hash], [script], [previous_hash], [previous_script], [applied_at], [applied_by])
                VALUES (@name, @hash, @script, @previous_hash, @previous_script, @applied_at, @applied_by);
            """,
            new Dictionary<string, object?>
            {
                ["name"] = repeatable.Name,
                ["hash"] = repeatable.Hash,
                ["script"] = repeatable.Script,
                ["previous_hash"] = repeatable.PreviousHash,
                ["previous_script"] = repeatable.PreviousScript,
                ["applied_at"] = repeatable.AppliedAtUtc,
                ["applied_by"] = repeatable.AppliedBy,
            },
            cancellationToken);

    public Task AppendEventAsync(IDatabaseSession session, string schema, MigrationEvent migrationEvent, CancellationToken cancellationToken) =>
        session.ExecuteAsync($"""
            INSERT INTO {Quote(schema)}.[events] ([kind], [name], [script_hash], [chain_id], [message], [at], [actor], [host], [tool_version], [environment])
            VALUES (@kind, @name, @script_hash, @chain_id, @message, @at, @actor, @host, @tool_version, @environment);
            """,
            new Dictionary<string, object?>
            {
                ["kind"] = migrationEvent.Kind.ToString().ToLowerInvariant(),
                ["name"] = migrationEvent.Name,
                ["script_hash"] = migrationEvent.ScriptHash,
                ["chain_id"] = migrationEvent.ChainId,
                ["message"] = migrationEvent.Message,
                ["at"] = migrationEvent.AtUtc,
                ["actor"] = migrationEvent.Actor,
                ["host"] = migrationEvent.Host,
                ["tool_version"] = migrationEvent.ToolVersion,
                ["environment"] = migrationEvent.Environment,
            },
            cancellationToken);

    public async Task<IReadOnlyList<MigrationEvent>> GetEventsAsync(IDatabaseSession session, string schema, int limit, CancellationToken cancellationToken)
    {
        var rows = await session.QueryAsync(
            $"SELECT TOP (@limit) * FROM {Quote(schema)}.[events] ORDER BY [id] DESC;",
            new Dictionary<string, object?> { ["limit"] = limit },
            cancellationToken).ConfigureAwait(false);
        return rows.Select(r => new MigrationEvent
        {
            Id = (long)r["id"]!,
            Kind = Enum.Parse<MigrationEventKind>((string)r["kind"]!, ignoreCase: true),
            Name = (string)r["name"]!,
            ScriptHash = (string?)r["script_hash"],
            ChainId = (string?)r["chain_id"],
            Message = (string?)r["message"],
            AtUtc = (DateTimeOffset)r["at"]!,
            Actor = (string)r["actor"]!,
            Host = (string)r["host"]!,
            ToolVersion = (string)r["tool_version"]!,
            Environment = (string?)r["environment"],
        }).ToList();
    }

    public Task InsertSchemaSnapshotAsync(IDatabaseSession session, string schema, SchemaSnapshot snapshot, CancellationToken cancellationToken) =>
        session.ExecuteAsync(
            $"INSERT INTO {Quote(schema)}.[schema_snapshots] ([fingerprint], [dump], [captured_at], [head_chain_id]) VALUES (@fingerprint, @dump, @captured_at, @head_chain_id);",
            new Dictionary<string, object?>
            {
                ["fingerprint"] = snapshot.Fingerprint,
                ["dump"] = snapshot.Dump,
                ["captured_at"] = snapshot.CapturedAtUtc,
                ["head_chain_id"] = snapshot.HeadChainId,
            },
            cancellationToken);

    public async Task<SchemaSnapshot?> GetLatestSchemaSnapshotAsync(IDatabaseSession session, string schema, CancellationToken cancellationToken)
    {
        var rows = await session.QueryAsync($"SELECT TOP (1) * FROM {Quote(schema)}.[schema_snapshots] ORDER BY [id] DESC;", null, cancellationToken).ConfigureAwait(false);
        if (rows.Count == 0)
        {
            return null;
        }

        var r = rows[0];
        return new SchemaSnapshot((string)r["fingerprint"]!, (string)r["dump"]!, (DateTimeOffset)r["captured_at"]!, (string?)r["head_chain_id"]);
    }

    internal static string Quote(string schema)
    {
        if (!SchemaRegex().IsMatch(schema))
        {
            throw new MigrationException($"Invalid state schema name '{schema}'. Use letters, digits and underscores.");
        }

        return $"[{schema}]";
    }

    [GeneratedRegex(@"^[A-Za-z_][A-Za-z0-9_]{0,127}$")]
    private static partial Regex SchemaRegex();
}
