using System.Globalization;
using System.Text.RegularExpressions;
using Badger.Trek.Core;
using Badger.Trek.Core.Providers;

namespace Badger.Trek.Providers.MySql;

/// <summary>
/// The trek journal in MySQL. In MySQL a schema is a database, and trek deploys to one: the state schema name becomes
/// a table prefix in that database (trek_meta, trek_changes, trek_repeatables, trek_events, trek_schema_snapshots), so
/// trek needs no CREATE DATABASE right and no access to another database. InnoDB, utf8mb4 with binary collation (names
/// and hashes compare exactly), timestamps as DATETIME(6) in UTC.
/// </summary>
public sealed partial class MySqlStateStore : IStateStore
{
    public const int StateVersion = 1;

    private const string TableOptions = "ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_bin";

    public string MoveScript(string from, string to)
    {
        var (f, t) = (Prefix(from), Prefix(to));
        return "RENAME TABLE " + string.Join(", ", StateTables.Names.Select(n => $"`{f}{n}` TO `{t}{n}`")) + ";";
    }

    public async Task<bool> ExistsAsync(IDatabaseSession session, string schema, CancellationToken cancellationToken)
    {
        var rows = await session.QueryAsync(
            "SELECT COUNT(*) AS n FROM information_schema.tables WHERE table_schema = DATABASE() AND table_name = @name;",
            new Dictionary<string, object?> { ["name"] = Prefix(schema) + "changes" },
            cancellationToken).ConfigureAwait(false);
        return Convert.ToInt64(rows[0]["n"], CultureInfo.InvariantCulture) > 0;
    }

    public async Task EnsureCreatedAsync(IDatabaseSession session, string schema, CancellationToken cancellationToken)
    {
        var p = Prefix(schema);
        string[] tables =
        [
            $"""
            CREATE TABLE IF NOT EXISTS `{p}meta` (
                `key` VARCHAR(128) NOT NULL PRIMARY KEY,
                `value` TEXT NOT NULL) {TableOptions}
            """,
            $"""
            CREATE TABLE IF NOT EXISTS `{p}changes` (
                sequence BIGINT NOT NULL AUTO_INCREMENT PRIMARY KEY,
                name VARCHAR(255) NOT NULL UNIQUE,
                script_hash VARCHAR(128) NOT NULL,
                chain_id VARCHAR(128) NOT NULL,
                deploy_script LONGTEXT NOT NULL,
                revert_script LONGTEXT NULL,
                verify_script LONGTEXT NULL,
                flags INT NOT NULL,
                deployed_at DATETIME(6) NOT NULL,
                deployed_by VARCHAR(255) NOT NULL,
                host VARCHAR(255) NOT NULL,
                tool_version VARCHAR(64) NOT NULL,
                duration_ms BIGINT NOT NULL,
                applied_out_of_order TINYINT(1) NOT NULL,
                baselined TINYINT(1) NOT NULL,
                rendered_hash VARCHAR(128) NULL,
                rendered_revert_hash VARCHAR(128) NULL) {TableOptions}
            """,
            $"""
            CREATE TABLE IF NOT EXISTS `{p}repeatables` (
                name VARCHAR(512) NOT NULL PRIMARY KEY,
                hash VARCHAR(128) NOT NULL,
                script LONGTEXT NOT NULL,
                previous_hash VARCHAR(128) NULL,
                previous_script LONGTEXT NULL,
                applied_at DATETIME(6) NOT NULL,
                applied_by VARCHAR(255) NOT NULL) {TableOptions}
            """,
            $"""
            CREATE TABLE IF NOT EXISTS `{p}events` (
                id BIGINT NOT NULL AUTO_INCREMENT PRIMARY KEY,
                kind VARCHAR(32) NOT NULL,
                name VARCHAR(512) NOT NULL,
                script_hash VARCHAR(128) NULL,
                chain_id VARCHAR(128) NULL,
                message LONGTEXT NULL,
                at DATETIME(6) NOT NULL,
                actor VARCHAR(255) NOT NULL,
                host VARCHAR(255) NOT NULL,
                tool_version VARCHAR(64) NOT NULL,
                environment VARCHAR(255) NULL) {TableOptions}
            """,
            $"""
            CREATE TABLE IF NOT EXISTS `{p}schema_snapshots` (
                id BIGINT NOT NULL AUTO_INCREMENT PRIMARY KEY,
                fingerprint VARCHAR(128) NOT NULL,
                dump LONGTEXT NOT NULL,
                captured_at DATETIME(6) NOT NULL,
                head_chain_id VARCHAR(128) NULL) {TableOptions}
            """,
        ];
        foreach (var table in tables)
        {
            await session.ExecuteAsync(table, null, cancellationToken).ConfigureAwait(false);
        }

        var version = await GetMetaAsync(session, schema, "state_version", cancellationToken).ConfigureAwait(false);
        if (version is null)
        {
            await SetMetaAsync(session, schema, "state_version", StateVersion.ToString(CultureInfo.InvariantCulture), cancellationToken).ConfigureAwait(false);
        }
        else if (int.Parse(version, CultureInfo.InvariantCulture) > StateVersion)
        {
            throw new MigrationException($"The trek state tables ({p}*) were created by a newer version of Badger Trek (state version {version}). Upgrade the tool.");
        }
    }

    public async Task<string?> GetMetaAsync(IDatabaseSession session, string schema, string key, CancellationToken cancellationToken)
    {
        var rows = await session.QueryAsync($"SELECT `value` FROM `{Prefix(schema)}meta` WHERE `key` = @key;", new Dictionary<string, object?> { ["key"] = key }, cancellationToken).ConfigureAwait(false);
        return rows.Count == 0 ? null : (string?)rows[0]["value"];
    }

    public Task SetMetaAsync(IDatabaseSession session, string schema, string key, string value, CancellationToken cancellationToken) =>
        session.ExecuteAsync(
            $"INSERT INTO `{Prefix(schema)}meta` (`key`, `value`) VALUES (@key, @value) AS new ON DUPLICATE KEY UPDATE `value` = new.`value`;",
            new Dictionary<string, object?> { ["key"] = key, ["value"] = value },
            cancellationToken);

    public async Task<IReadOnlyList<AppliedChange>> GetAppliedChangesAsync(IDatabaseSession session, string schema, CancellationToken cancellationToken)
    {
        var rows = await session.QueryAsync($"SELECT * FROM `{Prefix(schema)}changes` ORDER BY sequence;", null, cancellationToken).ConfigureAwait(false);
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
            INSERT INTO `{Prefix(schema)}changes`
                (name, script_hash, chain_id, deploy_script, revert_script, verify_script, flags, deployed_at, deployed_by, host,
                 tool_version, duration_ms, applied_out_of_order, baselined, rendered_hash, rendered_revert_hash)
            VALUES
                (@name, @script_hash, @chain_id, @deploy_script, @revert_script, @verify_script, @flags, @deployed_at, @deployed_by, @host,
                 @tool_version, @duration_ms, @applied_out_of_order, @baselined, @rendered_hash, @rendered_revert_hash);
            """, ChangeParameters(change), cancellationToken);

    public Task UpdateChangeAsync(IDatabaseSession session, string schema, AppliedChange change, CancellationToken cancellationToken) =>
        session.ExecuteAsync($"""
            UPDATE `{Prefix(schema)}changes`
               SET script_hash = @script_hash, chain_id = @chain_id, deploy_script = @deploy_script, revert_script = @revert_script,
                   verify_script = @verify_script, flags = @flags, rendered_hash = @rendered_hash, rendered_revert_hash = @rendered_revert_hash
             WHERE name = @name;
            """, ChangeParameters(change), cancellationToken);

    public Task DeleteChangeAsync(IDatabaseSession session, string schema, string name, CancellationToken cancellationToken) =>
        session.ExecuteAsync($"DELETE FROM `{Prefix(schema)}changes` WHERE name = @name;", new Dictionary<string, object?> { ["name"] = name }, cancellationToken);

    public async Task<IReadOnlyList<AppliedRepeatable>> GetRepeatablesAsync(IDatabaseSession session, string schema, CancellationToken cancellationToken)
    {
        var rows = await session.QueryAsync($"SELECT * FROM `{Prefix(schema)}repeatables` ORDER BY name;", null, cancellationToken).ConfigureAwait(false);
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
            INSERT INTO `{Prefix(schema)}repeatables` (name, hash, script, previous_hash, previous_script, applied_at, applied_by)
            VALUES (@name, @hash, @script, @previous_hash, @previous_script, @applied_at, @applied_by) AS new
            ON DUPLICATE KEY UPDATE hash = new.hash, script = new.script, previous_hash = new.previous_hash,
                previous_script = new.previous_script, applied_at = new.applied_at, applied_by = new.applied_by;
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
            INSERT INTO `{Prefix(schema)}events` (kind, name, script_hash, chain_id, message, at, actor, host, tool_version, environment)
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
        var rows = await session.QueryAsync($"SELECT * FROM `{Prefix(schema)}events` ORDER BY id DESC LIMIT @limit;", new Dictionary<string, object?> { ["limit"] = limit }, cancellationToken).ConfigureAwait(false);
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
            $"INSERT INTO `{Prefix(schema)}schema_snapshots` (fingerprint, dump, captured_at, head_chain_id) VALUES (@fingerprint, @dump, @captured_at, @head_chain_id);",
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
        var rows = await session.QueryAsync($"SELECT * FROM `{Prefix(schema)}schema_snapshots` ORDER BY id DESC LIMIT 1;", null, cancellationToken).ConfigureAwait(false);
        if (rows.Count == 0)
        {
            return null;
        }

        var r = rows[0];
        return new SchemaSnapshot((string)r["fingerprint"]!, (string)r["dump"]!, Utc(r["captured_at"]), (string?)r["head_chain_id"]);
    }

    /// <summary>"trek_" for state schema "trek".</summary>
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
        DateTime d => new DateTimeOffset(DateTime.SpecifyKind(d, DateTimeKind.Utc)),
        DateTimeOffset d => d.ToUniversalTime(),
        string s => DateTimeOffset.Parse(s, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal),
        _ => throw new InvalidCastException($"Unexpected timestamp value {value}."),
    };

    // MySQL table names are at most 64 characters; the longest suffix is "schema_snapshots".
    [GeneratedRegex(@"^[A-Za-z_][A-Za-z0-9_]{0,46}$")]
    private static partial Regex SchemaRegex();
}
