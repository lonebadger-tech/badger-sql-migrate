using System.Globalization;
using System.Text.RegularExpressions;
using Badger.SqlMigrate.Core;
using Badger.SqlMigrate.Core.Providers;

namespace Badger.SqlMigrate.Providers.PostgreSql;

/// <summary>The bsm journal in PostgreSQL: the same tables as on SQL Server, in schema bsm (configurable).</summary>
public sealed partial class PostgreSqlStateStore : IStateStore
{
    public const int StateVersion = 1;

    public async Task<bool> ExistsAsync(IDatabaseSession session, string schema, CancellationToken cancellationToken)
    {
        var rows = await session.QueryAsync("SELECT to_regclass(@name) IS NOT NULL AS present;", new Dictionary<string, object?> { ["name"] = $"{Quote(schema)}.changes" }, cancellationToken).ConfigureAwait(false);
        return rows[0]["present"] is true;
    }

    public async Task EnsureCreatedAsync(IDatabaseSession session, string schema, CancellationToken cancellationToken)
    {
        var s = Quote(schema);
        await session.ExecuteAsync($"""
            CREATE SCHEMA IF NOT EXISTS {s};
            CREATE TABLE IF NOT EXISTS {s}.meta (
                key text PRIMARY KEY,
                value text NOT NULL);
            CREATE TABLE IF NOT EXISTS {s}.changes (
                sequence bigint GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
                name text NOT NULL UNIQUE,
                script_hash text NOT NULL,
                chain_id text NOT NULL,
                deploy_script text NOT NULL,
                revert_script text,
                verify_script text,
                flags integer NOT NULL,
                deployed_at timestamptz NOT NULL,
                deployed_by text NOT NULL,
                host text NOT NULL,
                tool_version text NOT NULL,
                duration_ms bigint NOT NULL,
                applied_out_of_order boolean NOT NULL,
                baselined boolean NOT NULL,
                rendered_hash text,
                rendered_revert_hash text);
            CREATE TABLE IF NOT EXISTS {s}.repeatables (
                name text PRIMARY KEY,
                hash text NOT NULL,
                script text NOT NULL,
                previous_hash text,
                previous_script text,
                applied_at timestamptz NOT NULL,
                applied_by text NOT NULL);
            CREATE TABLE IF NOT EXISTS {s}.events (
                id bigint GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
                kind text NOT NULL,
                name text NOT NULL,
                script_hash text,
                chain_id text,
                message text,
                at timestamptz NOT NULL,
                actor text NOT NULL,
                host text NOT NULL,
                tool_version text NOT NULL,
                environment text);
            CREATE TABLE IF NOT EXISTS {s}.schema_snapshots (
                id bigint GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
                fingerprint text NOT NULL,
                dump text NOT NULL,
                captured_at timestamptz NOT NULL,
                head_chain_id text);
            """, null, cancellationToken).ConfigureAwait(false);

        var version = await GetMetaAsync(session, schema, "state_version", cancellationToken).ConfigureAwait(false);
        if (version is null)
        {
            await SetMetaAsync(session, schema, "state_version", StateVersion.ToString(CultureInfo.InvariantCulture), cancellationToken).ConfigureAwait(false);
        }
        else if (int.Parse(version, CultureInfo.InvariantCulture) > StateVersion)
        {
            throw new MigrationException($"The bsm state tables in {s} were created by a newer version of Badger SQL Migrate (state version {version}). Upgrade the tool.");
        }
    }

    public async Task<string?> GetMetaAsync(IDatabaseSession session, string schema, string key, CancellationToken cancellationToken)
    {
        var rows = await session.QueryAsync($"SELECT value FROM {Quote(schema)}.meta WHERE key = @key;", new Dictionary<string, object?> { ["key"] = key }, cancellationToken).ConfigureAwait(false);
        return rows.Count == 0 ? null : (string?)rows[0]["value"];
    }

    public Task SetMetaAsync(IDatabaseSession session, string schema, string key, string value, CancellationToken cancellationToken) =>
        session.ExecuteAsync(
            $"INSERT INTO {Quote(schema)}.meta (key, value) VALUES (@key, @value) ON CONFLICT (key) DO UPDATE SET value = EXCLUDED.value;",
            new Dictionary<string, object?> { ["key"] = key, ["value"] = value },
            cancellationToken);

    public async Task<IReadOnlyList<AppliedChange>> GetAppliedChangesAsync(IDatabaseSession session, string schema, CancellationToken cancellationToken)
    {
        var rows = await session.QueryAsync($"SELECT * FROM {Quote(schema)}.changes ORDER BY sequence;", null, cancellationToken).ConfigureAwait(false);
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
            DeployedAtUtc = Utc(r["deployed_at"]),
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
            INSERT INTO {Quote(schema)}.changes
                (name, script_hash, chain_id, deploy_script, revert_script, verify_script, flags, deployed_at, deployed_by, host,
                 tool_version, duration_ms, applied_out_of_order, baselined, rendered_hash, rendered_revert_hash)
            VALUES
                (@name, @script_hash, @chain_id, @deploy_script, @revert_script, @verify_script, @flags, @deployed_at, @deployed_by, @host,
                 @tool_version, @duration_ms, @applied_out_of_order, @baselined, @rendered_hash, @rendered_revert_hash);
            """, ChangeParameters(change), cancellationToken);

    public Task UpdateChangeAsync(IDatabaseSession session, string schema, AppliedChange change, CancellationToken cancellationToken) =>
        session.ExecuteAsync($"""
            UPDATE {Quote(schema)}.changes
               SET script_hash = @script_hash, chain_id = @chain_id, deploy_script = @deploy_script, revert_script = @revert_script,
                   verify_script = @verify_script, flags = @flags, rendered_hash = @rendered_hash, rendered_revert_hash = @rendered_revert_hash
             WHERE name = @name;
            """, ChangeParameters(change), cancellationToken);

    public Task DeleteChangeAsync(IDatabaseSession session, string schema, string name, CancellationToken cancellationToken) =>
        session.ExecuteAsync($"DELETE FROM {Quote(schema)}.changes WHERE name = @name;", new Dictionary<string, object?> { ["name"] = name }, cancellationToken);

    public async Task<IReadOnlyList<AppliedRepeatable>> GetRepeatablesAsync(IDatabaseSession session, string schema, CancellationToken cancellationToken)
    {
        var rows = await session.QueryAsync($"SELECT * FROM {Quote(schema)}.repeatables ORDER BY name;", null, cancellationToken).ConfigureAwait(false);
        return rows.Select(r => new AppliedRepeatable
        {
            Name = (string)r["name"]!,
            Hash = (string)r["hash"]!,
            Script = (string)r["script"]!,
            PreviousHash = (string?)r["previous_hash"],
            PreviousScript = (string?)r["previous_script"],
            AppliedAtUtc = Utc(r["applied_at"]),
            AppliedBy = (string)r["applied_by"]!,
        }).ToList();
    }

    public Task UpsertRepeatableAsync(IDatabaseSession session, string schema, AppliedRepeatable repeatable, CancellationToken cancellationToken) =>
        session.ExecuteAsync($"""
            INSERT INTO {Quote(schema)}.repeatables (name, hash, script, previous_hash, previous_script, applied_at, applied_by)
            VALUES (@name, @hash, @script, @previous_hash, @previous_script, @applied_at, @applied_by)
            ON CONFLICT (name) DO UPDATE SET hash = EXCLUDED.hash, script = EXCLUDED.script, previous_hash = EXCLUDED.previous_hash,
                previous_script = EXCLUDED.previous_script, applied_at = EXCLUDED.applied_at, applied_by = EXCLUDED.applied_by;
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
            INSERT INTO {Quote(schema)}.events (kind, name, script_hash, chain_id, message, at, actor, host, tool_version, environment)
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
        var rows = await session.QueryAsync($"SELECT * FROM {Quote(schema)}.events ORDER BY id DESC LIMIT @limit;", new Dictionary<string, object?> { ["limit"] = limit }, cancellationToken).ConfigureAwait(false);
        return rows.Select(r => new MigrationEvent
        {
            Id = (long)r["id"]!,
            Kind = Enum.Parse<MigrationEventKind>((string)r["kind"]!, ignoreCase: true),
            Name = (string)r["name"]!,
            ScriptHash = (string?)r["script_hash"],
            ChainId = (string?)r["chain_id"],
            Message = (string?)r["message"],
            AtUtc = Utc(r["at"]),
            Actor = (string)r["actor"]!,
            Host = (string)r["host"]!,
            ToolVersion = (string)r["tool_version"]!,
            Environment = (string?)r["environment"],
        }).ToList();
    }

    public Task InsertSchemaSnapshotAsync(IDatabaseSession session, string schema, SchemaSnapshot snapshot, CancellationToken cancellationToken) =>
        session.ExecuteAsync(
            $"INSERT INTO {Quote(schema)}.schema_snapshots (fingerprint, dump, captured_at, head_chain_id) VALUES (@fingerprint, @dump, @captured_at, @head_chain_id);",
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
        var rows = await session.QueryAsync($"SELECT * FROM {Quote(schema)}.schema_snapshots ORDER BY id DESC LIMIT 1;", null, cancellationToken).ConfigureAwait(false);
        if (rows.Count == 0)
        {
            return null;
        }

        var r = rows[0];
        return new SchemaSnapshot((string)r["fingerprint"]!, (string)r["dump"]!, Utc(r["captured_at"]), (string?)r["head_chain_id"]);
    }

    private static Dictionary<string, object?> ChangeParameters(AppliedChange change) => new()
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
    };

    /// <summary>timestamptz reads as a UTC DateTime.</summary>
    private static DateTimeOffset Utc(object? value) => value switch
    {
        DateTimeOffset d => d,
        DateTime d => new DateTimeOffset(DateTime.SpecifyKind(d, DateTimeKind.Utc)),
        _ => throw new InvalidCastException($"Unexpected timestamp value {value}."),
    };

    internal static string Quote(string schema) =>
        SchemaRegex().IsMatch(schema) ? $"\"{schema}\"" : throw new MigrationException($"Invalid state schema name '{schema}'. Use letters, digits and underscores.");

    [GeneratedRegex(@"^[A-Za-z_][A-Za-z0-9_]{0,62}$")]
    private static partial Regex SchemaRegex();
}
