using System.Globalization;
using System.Text.RegularExpressions;
using Badger.SqlMigrate.Core;
using Badger.SqlMigrate.Core.Providers;

namespace Badger.SqlMigrate.Providers.Sqlite;

/// <summary>
/// The bsm journal in SQLite. SQLite has no schemas, so the state schema name becomes a table prefix:
/// bsm_meta, bsm_changes, bsm_repeatables, bsm_events and bsm_schema_snapshots. Timestamps are ISO-8601 UTC text.
/// </summary>
public sealed partial class SqliteStateStore : IStateStore
{
    public const int StateVersion = 1;

    public static readonly string[] Tables = ["meta", "changes", "repeatables", "events", "schema_snapshots"];

    public async Task<bool> ExistsAsync(IDatabaseSession session, string schema, CancellationToken cancellationToken)
    {
        var rows = await session.QueryAsync("SELECT count(*) AS n FROM sqlite_master WHERE type = 'table' AND name = @name;", new Dictionary<string, object?> { ["name"] = Prefix(schema) + "changes" }, cancellationToken).ConfigureAwait(false);
        return Convert.ToInt64(rows[0]["n"], CultureInfo.InvariantCulture) > 0;
    }

    public async Task EnsureCreatedAsync(IDatabaseSession session, string schema, CancellationToken cancellationToken)
    {
        var p = Prefix(schema);
        await session.ExecuteAsync($"""
            CREATE TABLE IF NOT EXISTS {p}meta (
                key TEXT NOT NULL PRIMARY KEY,
                value TEXT NOT NULL);
            CREATE TABLE IF NOT EXISTS {p}changes (
                sequence INTEGER PRIMARY KEY AUTOINCREMENT,
                name TEXT NOT NULL UNIQUE,
                script_hash TEXT NOT NULL,
                chain_id TEXT NOT NULL,
                deploy_script TEXT NOT NULL,
                revert_script TEXT,
                verify_script TEXT,
                flags INTEGER NOT NULL,
                deployed_at TEXT NOT NULL,
                deployed_by TEXT NOT NULL,
                host TEXT NOT NULL,
                tool_version TEXT NOT NULL,
                duration_ms INTEGER NOT NULL,
                applied_out_of_order INTEGER NOT NULL,
                baselined INTEGER NOT NULL,
                rendered_hash TEXT,
                rendered_revert_hash TEXT);
            CREATE TABLE IF NOT EXISTS {p}repeatables (
                name TEXT NOT NULL PRIMARY KEY,
                hash TEXT NOT NULL,
                script TEXT NOT NULL,
                previous_hash TEXT,
                previous_script TEXT,
                applied_at TEXT NOT NULL,
                applied_by TEXT NOT NULL);
            CREATE TABLE IF NOT EXISTS {p}events (
                id INTEGER PRIMARY KEY AUTOINCREMENT,
                kind TEXT NOT NULL,
                name TEXT NOT NULL,
                script_hash TEXT,
                chain_id TEXT,
                message TEXT,
                at TEXT NOT NULL,
                actor TEXT NOT NULL,
                host TEXT NOT NULL,
                tool_version TEXT NOT NULL,
                environment TEXT);
            CREATE TABLE IF NOT EXISTS {p}schema_snapshots (
                id INTEGER PRIMARY KEY AUTOINCREMENT,
                fingerprint TEXT NOT NULL,
                dump TEXT NOT NULL,
                captured_at TEXT NOT NULL,
                head_chain_id TEXT);
            """, null, cancellationToken).ConfigureAwait(false);

        var version = await GetMetaAsync(session, schema, "state_version", cancellationToken).ConfigureAwait(false);
        if (version is null)
        {
            await SetMetaAsync(session, schema, "state_version", StateVersion.ToString(CultureInfo.InvariantCulture), cancellationToken).ConfigureAwait(false);
        }
        else if (int.Parse(version, CultureInfo.InvariantCulture) > StateVersion)
        {
            throw new MigrationException($"The bsm state tables ({p}*) were created by a newer version of Badger SQL Migrate (state version {version}). Upgrade the tool.");
        }
    }

    public async Task<string?> GetMetaAsync(IDatabaseSession session, string schema, string key, CancellationToken cancellationToken)
    {
        var rows = await session.QueryAsync($"SELECT value FROM {Prefix(schema)}meta WHERE key = @key;", new Dictionary<string, object?> { ["key"] = key }, cancellationToken).ConfigureAwait(false);
        return rows.Count == 0 ? null : (string?)rows[0]["value"];
    }

    public Task SetMetaAsync(IDatabaseSession session, string schema, string key, string value, CancellationToken cancellationToken) =>
        session.ExecuteAsync(
            $"INSERT INTO {Prefix(schema)}meta (key, value) VALUES (@key, @value) ON CONFLICT (key) DO UPDATE SET value = excluded.value;",
            new Dictionary<string, object?> { ["key"] = key, ["value"] = value },
            cancellationToken);

    public async Task<IReadOnlyList<AppliedChange>> GetAppliedChangesAsync(IDatabaseSession session, string schema, CancellationToken cancellationToken)
    {
        var rows = await session.QueryAsync($"SELECT * FROM {Prefix(schema)}changes ORDER BY sequence;", null, cancellationToken).ConfigureAwait(false);
        return rows.Select(r => new AppliedChange
        {
            Sequence = Long(r["sequence"]),
            Name = (string)r["name"]!,
            ScriptHash = (string)r["script_hash"]!,
            ChainId = (string)r["chain_id"]!,
            DeployScript = (string)r["deploy_script"]!,
            RevertScript = (string?)r["revert_script"],
            VerifyScript = (string?)r["verify_script"],
            Flags = (int)Long(r["flags"]),
            DeployedAtUtc = Utc(r["deployed_at"]),
            DeployedBy = (string)r["deployed_by"]!,
            Host = (string)r["host"]!,
            ToolVersion = (string)r["tool_version"]!,
            DurationMs = Long(r["duration_ms"]),
            AppliedOutOfOrder = Long(r["applied_out_of_order"]) != 0,
            Baselined = Long(r["baselined"]) != 0,
            RenderedHash = (string?)r["rendered_hash"],
            RenderedRevertHash = (string?)r["rendered_revert_hash"],
        }).ToList();
    }

    public Task InsertChangeAsync(IDatabaseSession session, string schema, AppliedChange change, CancellationToken cancellationToken) =>
        session.ExecuteAsync($"""
            INSERT INTO {Prefix(schema)}changes
                (name, script_hash, chain_id, deploy_script, revert_script, verify_script, flags, deployed_at, deployed_by, host,
                 tool_version, duration_ms, applied_out_of_order, baselined, rendered_hash, rendered_revert_hash)
            VALUES
                (@name, @script_hash, @chain_id, @deploy_script, @revert_script, @verify_script, @flags, @deployed_at, @deployed_by, @host,
                 @tool_version, @duration_ms, @applied_out_of_order, @baselined, @rendered_hash, @rendered_revert_hash);
            """, ChangeParameters(change), cancellationToken);

    public Task UpdateChangeAsync(IDatabaseSession session, string schema, AppliedChange change, CancellationToken cancellationToken) =>
        session.ExecuteAsync($"""
            UPDATE {Prefix(schema)}changes
               SET script_hash = @script_hash, chain_id = @chain_id, deploy_script = @deploy_script, revert_script = @revert_script,
                   verify_script = @verify_script, flags = @flags, rendered_hash = @rendered_hash, rendered_revert_hash = @rendered_revert_hash
             WHERE name = @name;
            """, ChangeParameters(change), cancellationToken);

    public Task DeleteChangeAsync(IDatabaseSession session, string schema, string name, CancellationToken cancellationToken) =>
        session.ExecuteAsync($"DELETE FROM {Prefix(schema)}changes WHERE name = @name;", new Dictionary<string, object?> { ["name"] = name }, cancellationToken);

    public async Task<IReadOnlyList<AppliedRepeatable>> GetRepeatablesAsync(IDatabaseSession session, string schema, CancellationToken cancellationToken)
    {
        var rows = await session.QueryAsync($"SELECT * FROM {Prefix(schema)}repeatables ORDER BY name;", null, cancellationToken).ConfigureAwait(false);
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
            INSERT INTO {Prefix(schema)}repeatables (name, hash, script, previous_hash, previous_script, applied_at, applied_by)
            VALUES (@name, @hash, @script, @previous_hash, @previous_script, @applied_at, @applied_by)
            ON CONFLICT (name) DO UPDATE SET hash = excluded.hash, script = excluded.script, previous_hash = excluded.previous_hash,
                previous_script = excluded.previous_script, applied_at = excluded.applied_at, applied_by = excluded.applied_by;
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
            INSERT INTO {Prefix(schema)}events (kind, name, script_hash, chain_id, message, at, actor, host, tool_version, environment)
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
        var rows = await session.QueryAsync($"SELECT * FROM {Prefix(schema)}events ORDER BY id DESC LIMIT @limit;", new Dictionary<string, object?> { ["limit"] = limit }, cancellationToken).ConfigureAwait(false);
        return rows.Select(r => new MigrationEvent
        {
            Id = Long(r["id"]),
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
            $"INSERT INTO {Prefix(schema)}schema_snapshots (fingerprint, dump, captured_at, head_chain_id) VALUES (@fingerprint, @dump, @captured_at, @head_chain_id);",
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
        var rows = await session.QueryAsync($"SELECT * FROM {Prefix(schema)}schema_snapshots ORDER BY id DESC LIMIT 1;", null, cancellationToken).ConfigureAwait(false);
        if (rows.Count == 0)
        {
            return null;
        }

        var r = rows[0];
        return new SchemaSnapshot((string)r["fingerprint"]!, (string)r["dump"]!, Utc(r["captured_at"]), (string?)r["head_chain_id"]);
    }

    /// <summary>"bsm_" for state schema "bsm".</summary>
    public static string Prefix(string schema) =>
        SchemaRegex().IsMatch(schema) ? schema + "_" : throw new MigrationException($"Invalid state schema name '{schema}'. Use letters, digits and underscores.");

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

    private static long Long(object? value) => Convert.ToInt64(value, CultureInfo.InvariantCulture);

    private static DateTimeOffset Utc(object? value) => value switch
    {
        string s => DateTimeOffset.Parse(s, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal),
        DateTimeOffset d => d,
        DateTime d => new DateTimeOffset(DateTime.SpecifyKind(d, DateTimeKind.Utc)),
        _ => throw new InvalidCastException($"Unexpected timestamp value {value}."),
    };

    [GeneratedRegex(@"^[A-Za-z_][A-Za-z0-9_]{0,62}$")]
    private static partial Regex SchemaRegex();
}
