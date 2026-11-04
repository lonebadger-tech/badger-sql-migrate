using System.CommandLine;
using Badger.SqlMigrate.Cli;
using Badger.SqlMigrate.Core.Configuration;

var project = new Option<string>("--project", "-C") { Description = "Project directory containing bsm.yaml and bsm.plan.", Recursive = true, DefaultValueFactory = _ => "." };
var environment = new Option<string?>("--env", "-e") { Description = "Environment from bsm.yaml (or BSM_ENV; default: defaultEnvironment).", Recursive = true };
var connection = new Option<string?>("--connection-string") { Description = "Override the environment's connection string.", Recursive = true };
var claim = new Option<bool>("--claim-environment") { Description = "Allow a database recorded under another environment name (e.g. restored from prod) and record this one.", Recursive = true };
var vars = new Option<string[]>("--var") { Description = "Script variable, name=value. Repeatable.", Recursive = true };
var json = new Option<bool>("--json") { Description = "Machine-readable output on stdout.", Recursive = true };
var verbose = new Option<bool>("--verbose", "-v") { Description = "Show server messages and details.", Recursive = true };
var noColor = new Option<bool>("--no-color") { Description = "Disable ANSI colors (also honors NO_COLOR).", Recursive = true };
var noAgentsFile = new Option<bool>("--no-agents-file") { Description = "Do not write AGENTS.md (instructions for AI assistants) into the project.", Recursive = true };
var bundleFile = new Option<string?>("--bundle") { Description = "Run from a verified bundle (bsm bundle create) instead of the project folder's scripts.", Recursive = true };

GlobalOptions Globals(ParseResult r) => new(
    r.GetValue(project)!,
    r.GetValue(environment),
    r.GetValue(connection),
    r.GetValue(vars) ?? [],
    r.GetValue(claim),
    new ConsoleOutput(r.GetValue(json), r.GetValue(verbose), r.GetValue(noColor)),
    r.GetValue(bundleFile))
{
    AgentsFile = !r.GetValue(noAgentsFile),
};

var root = new RootCommand("Badger SQL Migrate - hash-certified, plan-based database migrations with real up and down.");
root.Options.Add(project);
root.Options.Add(environment);
root.Options.Add(connection);
root.Options.Add(claim);
root.Options.Add(vars);
root.Options.Add(json);
root.Options.Add(verbose);
root.Options.Add(noColor);
root.Options.Add(bundleFile);

// init
var initProvider = new Option<string>("--provider") { Description = "Database provider: sqlserver or postgresql.", DefaultValueFactory = _ => "sqlserver" };
var init = new Command("init", "Create bsm.yaml, bsm.plan, folders and AGENTS.md in the project directory.") { initProvider, noAgentsFile };
init.SetAction((r, ct) => Commands.Run(Globals(r), g => Commands.Init(g, r.GetValue(initProvider)!)));
root.Subcommands.Add(init);

// add
var addName = new Argument<string>("name") { Description = "Change name (letters, digits, '-', '_', '.')." };
var addRequires = new Option<string[]>("--requires", "-r") { Description = "Changes this one depends on.", AllowMultipleArgumentsPerToken = true };
var addNote = new Option<string?>("--note", "-n") { Description = "Note stored in the plan." };
var addNoTx = new Option<bool>("--notx") { Description = "Run outside a transaction." };
var addIrreversible = new Option<bool>("--irreversible") { Description = "No revert script." };
var addFromDiff = new Option<bool>("--from-diff") { Description = "Generate deploy.sql and revert.sql from schema edits made in the environment's database (compared with a shadow database built from the plan)." };
var addShadowEnv = new Option<string?>("--shadow-env") { Description = "Create the shadow database on this environment's server instead (it needs CREATE DATABASE rights)." };
var addKeepShadow = new Option<bool>("--keep-shadow") { Description = "Do not drop the shadow database afterwards." };
var addExclude = new Option<string[]>("--exclude") { Description = "Objects to leave out, as schema.name globs (e.g. audit.*). Repeatable." };
var addDryRun = new Option<bool>("--dry-run") { Description = "With --from-diff: print the generated deploy script; write nothing." };
var addNoRecord = new Option<bool>("--no-record") { Description = "With --from-diff: do not record the new change as applied in the database it came from." };
var add = new Command("add", "Add a change: creates changes/<name>/ scripts and appends it to bsm.plan.") { addName, addRequires, addNote, addNoTx, addIrreversible, addFromDiff, addShadowEnv, addKeepShadow, addExclude, addDryRun, addNoRecord };
add.SetAction((r, ct) => r.GetValue(addFromDiff)
    ? Commands.Run(Globals(r), g => Commands.AddFromDiff(g, r.GetValue(addName)!, r.GetValue(addRequires) ?? [], r.GetValue(addNote), r.GetValue(addShadowEnv), r.GetValue(addKeepShadow), r.GetValue(addExclude) ?? [], r.GetValue(addDryRun), !r.GetValue(addNoRecord), ct))
    : Commands.Run(Globals(r), g => Commands.Add(g, r.GetValue(addName)!, r.GetValue(addRequires) ?? [], r.GetValue(addNote), r.GetValue(addNoTx), r.GetValue(addIrreversible))));
root.Subcommands.Add(add);

// tag
var tagName = new Argument<string>("name") { Description = "Tag name, e.g. release-1.4." };
var tag = new Command("tag", "Tag the last change in the plan (use as @name in --to).") { tagName };
tag.SetAction((r, ct) => Commands.Run(Globals(r), g => Commands.Tag(g, r.GetValue(tagName)!)));
root.Subcommands.Add(tag);

// plan check
var plan = new Command("plan", "Plan file commands.");
var check = new Command("check", "Validate the plan, folders and scripts (no database needed). Use in CI and pre-commit hooks.");
check.SetAction((r, ct) => Commands.Run(Globals(r), g => Commands.PlanCheck(g)));
plan.Subcommands.Add(check);
var show = new Command("show", "List planned changes with script hashes and chain ids (no database needed).");
show.SetAction((r, ct) => Commands.Run(Globals(r), g => Commands.PlanShow(g)));
plan.Subcommands.Add(show);
root.Subcommands.Add(plan);

// status
var statusExit = new Option<bool>("--exit-code") { Description = "Exit with 3 when the database is not up to date." };
var status = new Command("status", "Compare the plan with the database.") { statusExit };
status.SetAction((r, ct) => Commands.Run(Globals(r), g => Commands.Status(g, r.GetValue(statusExit), ct)));
root.Subcommands.Add(status);

// deploy
var deployTo = new Option<string?>("--to") { Description = "Deploy up to and including this change or @tag." };
var dryRun = new Option<bool>("--dry-run") { Description = "Show what would run without changing anything." };
var deployScript = new Option<string?>("--script") { Description = "Write a reviewable SQL script of pending changes to this file instead of deploying." };
var ordering = new Option<OrderingPolicy?>("--ordering") { Description = "Override the environment's ordering policy (dependency|strict)." };
var yes = new Option<bool>("--yes", "-y") { Description = "Do not ask for confirmation (required in non-interactive runs against protected environments)." };
var deploy = new Command("deploy", "Provision environment resources, then deploy pending changes and repeatables.") { deployTo, dryRun, deployScript, ordering, yes };
deploy.SetAction((r, ct) => Commands.Run(Globals(r), g => Commands.Deploy(g, r.GetValue(deployTo), r.GetValue(dryRun), r.GetValue(deployScript), r.GetValue(ordering), r.GetValue(yes), ct)));
root.Subcommands.Add(deploy);

// revert
var revertTo = new Option<string?>("--to") { Description = "Revert everything deployed after this change or @tag." };
var revertLast = new Option<int?>("--last") { Description = "Revert the last N deployed changes." };
var revertAll = new Option<bool>("--all") { Description = "Revert every deployed change." };
var allowDrift = new Option<bool>("--allow-variable-drift") { Description = "Revert even if the revert script renders differently than at deploy time." };
var revertRepeatable = new Option<string[]>("--repeatable") { Description = "Restore a repeatable (e.g. views/vCustomer.sql) to its definition before the last apply. Repeatable." };
var revert = new Command("revert", "Revert changes using the revert scripts stored in the database at deploy time.") { revertTo, revertLast, revertAll, revertRepeatable, dryRun, yes, allowDrift };
revert.SetAction((r, ct) => Commands.Run(Globals(r), g => Commands.Revert(g, r.GetValue(revertTo), r.GetValue(revertLast), r.GetValue(revertAll), r.GetValue(revertRepeatable) ?? [], r.GetValue(dryRun), r.GetValue(yes), r.GetValue(allowDrift), ct)));
root.Subcommands.Add(revert);

// rebase
var rebaseTo = new Option<string>("--to") { Description = "Revert to this change or @tag, then deploy to the plan head.", Required = true };
var rebase = new Command("rebase", "Revert to a change, then redeploy to head (handy while iterating on a change).") { rebaseTo, yes, allowDrift };
rebase.SetAction((r, ct) => Commands.Run(Globals(r), g => Commands.Rebase(g, r.GetValue(rebaseTo)!, r.GetValue(yes), r.GetValue(allowDrift), ct)));
root.Subcommands.Add(rebase);

// repair
var repairNames = new Argument<string[]>("changes") { Description = "Deployed changes whose edited scripts should be accepted.", Arity = ArgumentArity.ZeroOrMore };
var repairAll = new Option<bool>("--all") { Description = "Repair every deployed change whose hash no longer matches." };
var repairReason = new Option<string?>("--reason") { Description = "Why the scripts were edited; recorded in the log." };
var repair = new Command("repair", "Accept edits to deployed changes (e.g. a fixed revert.sql) without running anything; re-certifies the journal.") { repairNames, repairAll, repairReason, dryRun, yes };
repair.SetAction((r, ct) => Commands.Run(Globals(r), g => Commands.Repair(g, r.GetValue(repairNames) ?? [], r.GetValue(repairAll), r.GetValue(repairReason), r.GetValue(dryRun), r.GetValue(yes), ct)));
root.Subcommands.Add(repair);

// rework
var reworkName = new Argument<string>("change") { Description = "The deployed change to supersede." };
var reworkAs = new Option<string?>("--as") { Description = "Name of the new change (default: <change>-v2, -v3, ...)." };
var rework = new Command("rework", "Add a change that supersedes a deployed one: copies its deploy script to edit, with a revert that restores it.") { reworkName, reworkAs, addNote };
rework.SetAction((r, ct) => Commands.Run(Globals(r), g => Commands.Rework(g, r.GetValue(reworkName)!, r.GetValue(reworkAs), r.GetValue(addNote))));
root.Subcommands.Add(rework);

// bundle
var bundle = new Command("bundle", "Package the project into a hash-verified file to deploy with --bundle.");
var bundleOutput = new Option<string?>("--output", "-o") { Description = "Bundle file to write (default: bsm-bundle-<plan head>.zip)." };
var bundleNoConfig = new Option<bool>("--no-config") { Description = "Leave bsm.yaml out; deploys then read it from --project." };
var bundleCreate = new Command("create", "Write a bundle of the plan, changes, repeatables, environment scripts and bsm.yaml.") { bundleOutput, bundleNoConfig };
bundleCreate.SetAction((r, ct) => Commands.Run(Globals(r), g => Commands.BundleCreate(g, r.GetValue(bundleOutput), r.GetValue(bundleNoConfig))));
bundle.Subcommands.Add(bundleCreate);
var bundlePath = new Argument<string>("file") { Description = "Bundle file." };
var bundleVerify = new Command("verify", "Check every file in a bundle against its manifest hashes.") { bundlePath };
bundleVerify.SetAction((r, ct) => Commands.Run(Globals(r), g => Commands.BundleVerify(g, r.GetValue(bundlePath)!)));
bundle.Subcommands.Add(bundleVerify);
root.Subcommands.Add(bundle);

// diff-schema
var diffAgainst = new Option<string?>("--against") { Description = "Compare with this environment's live database (default: this database's schema as recorded after its last deploy)." };
var diffExit = new Option<bool>("--exit-code") { Description = "Exit with 3 when the schemas differ." };
var diffScript = new Option<string?>("--script") { Description = "With --against: write SQL that turns this database into that one, to this file." };
var diffSchema = new Command("diff-schema", "Show schema differences: live vs. last recorded (what drifted), or between two environments.") { diffAgainst, diffExit, diffScript };
diffSchema.SetAction((r, ct) => Commands.Run(Globals(r), g => Commands.DiffSchema(g, r.GetValue(diffAgainst), r.GetValue(diffExit), r.GetValue(diffScript), ct)));
root.Subcommands.Add(diffSchema);

// verify
var verify = new Command("verify", "Check deployed hashes against the working copy, run verify scripts, and detect schema drift.");
verify.SetAction((r, ct) => Commands.Run(Globals(r), g => Commands.Verify(g, ct)));
root.Subcommands.Add(verify);

// log
var logLimit = new Option<int>("--limit", "-n") { Description = "Number of events.", DefaultValueFactory = _ => 50 };
var log = new Command("log", "Show the deploy/revert/failure history recorded in the database.") { logLimit };
log.SetAction((r, ct) => Commands.Run(Globals(r), g => Commands.Log(g, r.GetValue(logLimit), ct)));
root.Subcommands.Add(log);

// baseline
var baselineTo = new Option<string?>("--to") { Description = "Mark changes up to this one as already applied (with --from sqlproj: further than the imported changes)." };
var baselineFrom = new Option<string?>("--from") { Description = "flyway | dbup | grate | roundhouse | sqitch: read what the database already has from that tool's journal; sqlproj: the database was published from the SQL project (for projects made with 'bsm import')." };
baselineFrom.AcceptOnlyFromAmong("flyway", "dbup", "grate", "roundhouse", "sqitch", "sqlproj");
var baselineJournal = new Option<string?>("--journal") { Description = "The tool's journal table, [schema.]table (default: flyway_schema_history or SchemaVersions in dbo/public, grate.ScriptsRun, RoundhousE.ScriptsRun, sqitch.changes)." };
var baselineSkipChecksums = new Option<bool>("--skip-checksums") { Description = "Do not compare Flyway's checksums, grate's hashes or Sqitch's script hashes with the imported scripts (sqlproj: do not check that the project's tables exist)." };
var baseline = new Command("baseline", "Adopt an existing database: record changes as applied without running them.") { baselineTo, baselineFrom, baselineJournal, baselineSkipChecksums, yes };
baseline.SetAction((r, ct) => Commands.Run(Globals(r), g => Commands.Baseline(g, r.GetValue(baselineTo), r.GetValue(baselineFrom), r.GetValue(baselineJournal), r.GetValue(baselineSkipChecksums), r.GetValue(yes), ct)));

// import
var import = new Command("import", "Convert another tool's migrations (Flyway, DbUp, grate, Sqitch, SQL projects) into a new bsm project (written to --project).") { noAgentsFile };
var importPath = new Argument<string>("path") { Description = "The folder to import from." };
var importDryRun = new Option<bool>("--dry-run") { Description = "Show what would be written without writing anything." };
var importProvider = new Option<string?>("--provider") { Description = "The database the project targets: sqlserver (default) or postgresql. Sqitch projects for pg default to postgresql." };
var flywayConfig = new Option<string?>("--config") { Description = "flyway.conf or flyway.toml (default: found in the folder or its conf/ folder)." };
var flywayLocation = new Option<string[]>("--location") { Description = "Migration folder; overrides flyway.locations. Repeatable." };
var importFlyway = new Command("flyway", "Import a Flyway project: V/U/R scripts, placeholders, script config and environments.") { importPath, flywayConfig, flywayLocation, importProvider, importDryRun };
importFlyway.SetAction((r, ct) => Commands.Run(Globals(r), g => Commands.ImportFlyway(g, r.GetValue(importPath)!, r.GetValue(flywayConfig), r.GetValue(flywayLocation) ?? [], r.GetValue(importDryRun), r.GetValue(importProvider) ?? "sqlserver")));
import.Subcommands.Add(importFlyway);
var dbupFileNames = new Option<bool>("--file-names") { Description = "DbUp script names are file names only (WithScriptsFromFileSystem); default: folder.path.names like embedded resources." };
var dbupRunAlways = new Option<string[]>("--run-always") { Description = "Glob of scripts DbUp runs every time (imported as repeatables). Repeatable." };
var dbupNoVariables = new Option<bool>("--no-variables") { Description = "The project disabled DbUp variables: leave $name$ text alone." };
var importDbUp = new Command("dbup", "Import DbUp scripts: one change per script in DbUp's order; $variables$ become {variables} (values via --var).") { importPath, dbupFileNames, dbupRunAlways, dbupNoVariables, importProvider, importDryRun };
importDbUp.SetAction((r, ct) => Commands.Run(Globals(r), g => Commands.ImportDbUp(g, r.GetValue(importPath)!, r.GetValue(dbupFileNames), r.GetValue(dbupRunAlways) ?? [], r.GetValue(dbupNoVariables), r.GetValue(importDryRun), r.GetValue(importProvider) ?? "sqlserver")));
import.Subcommands.Add(importDbUp);
var grateFolder = new Option<string[]>("--folder") { Description = "A renamed grate folder, key=name (e.g. up=ddl). Repeatable." };
var grateEnvironments = new Option<string[]>("--environment") { Description = "Environment names used in *.ENV.* file names (default: the upper-case parts of those names). Repeatable or comma-separated." };
var grateIgnoreDirs = new Option<bool>("--ignore-directory-names") { Description = "grate ran with --ignoredirectorynames: script names are file names only." };
var grateNoTokens = new Option<bool>("--no-tokens") { Description = "grate ran with --disabletokenreplacement: leave {{Token}} text alone." };
var importGrate = new Command("grate", "Import a grate or RoundhousE scripts folder: up/ as changes, anytime and everytime folders as repeatables, *.ENV.* scripts, {{Tokens}} (values via --var).") { importPath, grateFolder, grateEnvironments, grateIgnoreDirs, grateNoTokens, importProvider, importDryRun };
importGrate.SetAction((r, ct) => Commands.Run(Globals(r), g => Commands.ImportGrate(g, r.GetValue(importPath)!, r.GetValue(grateFolder) ?? [], r.GetValue(grateEnvironments) ?? [], r.GetValue(grateIgnoreDirs), r.GetValue(grateNoTokens), r.GetValue(importDryRun), r.GetValue(importProvider) ?? "sqlserver")));
import.Subcommands.Add(importGrate);
var sqitchEngine = new Option<string?>("--engine") { Description = "Which engine's settings to use from sqitch.conf (default: core.engine)." };
var importSqitch = new Command("sqitch", "Import a Sqitch project: plan order, dependencies, tags, reworks and deploy/revert/verify scripts.") { importPath, sqitchEngine, importProvider, importDryRun };
importSqitch.SetAction((r, ct) => Commands.Run(Globals(r), g => Commands.ImportSqitch(g, r.GetValue(importPath)!, r.GetValue(sqitchEngine), r.GetValue(importDryRun), r.GetValue(importProvider))));
import.Subcommands.Add(importSqitch);
var importSqlProjPath = new Argument<string>("path") { Description = "The .sqlproj file, or the folder holding it." };
var importSqlProj = new Command("sqlproj", "Import an SSDT / SQL Database Project (.sqlproj): one change per table, type, schema ... in dependency order; views, procedures, functions and triggers as repeatables; the post-deployment script; SqlCmdVariables.") { importSqlProjPath, importDryRun };
importSqlProj.SetAction((r, ct) => Commands.Run(Globals(r), g => Commands.ImportSqlProj(g, r.GetValue(importSqlProjPath)!, r.GetValue(importDryRun))));
import.Subcommands.Add(importSqlProj);
root.Subcommands.Add(import);
root.Subcommands.Add(baseline);

// vars / render
var vars2 = new Command("vars", "List built-in and user variables for an environment, resolved ({name} and $(name) in scripts).");
vars2.SetAction((r, ct) => Commands.Run(Globals(r), g => Commands.Vars(g)));
root.Subcommands.Add(vars2);
var renderName = new Argument<string>("name") { Description = "Change name, or repeatable path (views/v.sql, @dev/seed.sql)." };
var renderKind = new Option<string>("--kind", "-k") { Description = "deploy | revert | verify (changes only).", DefaultValueFactory = _ => "deploy" };
var render = new Command("render", "Print a script exactly as it would be sent to the database for an environment.") { renderName, renderKind };
render.SetAction((r, ct) => Commands.Run(Globals(r), g => Commands.Render(g, r.GetValue(renderName)!, r.GetValue(renderKind)!)));
root.Subcommands.Add(render);

// env
var env = new Command("env", "Environment commands.");
var envList = new Command("list", "List environments (templates start with '_').");
envList.SetAction((r, ct) => Commands.Run(Globals(r), g => Commands.EnvList(g)));
env.Subcommands.Add(envList);
var envShow = new Command("show", "Show the fully resolved environment (extends merged, secrets masked).");
envShow.SetAction((r, ct) => Commands.Run(Globals(r), g => Commands.EnvShow(g)));
env.Subcommands.Add(envShow);
var envStatus = new Command("status", "Compare per-environment resources (e.g. Always Encrypted keys) with the database.");
envStatus.SetAction((r, ct) => Commands.Run(Globals(r), g => Commands.EnvStatus(g, ct)));
env.Subcommands.Add(envStatus);
var allowDestructive = new Option<bool>("--allow-destructive") { Description = "Also apply destructive steps, e.g. drop a column encryption key value protected by a retired master key." };
var envProvision = new Command("provision", "Create or rotate per-environment resources without deploying changes.") { dryRun, allowDestructive, yes };
envProvision.SetAction((r, ct) => Commands.Run(Globals(r), g => Commands.EnvProvision(g, r.GetValue(dryRun), r.GetValue(allowDestructive), r.GetValue(yes), ct)));
env.Subcommands.Add(envProvision);
root.Subcommands.Add(env);

// auth
var auth = new Command("auth", "Entra ID sign-in commands.");
var authClear = new Command("clear", "Forget saved interactive/device-code sign-ins so the next run signs in again.");
authClear.SetAction((r, ct) => Commands.Run(Globals(r), g => Commands.AuthClear(g)));
auth.Subcommands.Add(authClear);
root.Subcommands.Add(auth);

// doctor
var doctor = new Command("doctor", "Check configuration, sign-in and connectivity for an environment and explain problems.");
doctor.SetAction((r, ct) => Commands.Run(Globals(r), g => Commands.Doctor(g, ct)));
root.Subcommands.Add(doctor);

// AI tools
var aiGuide = new Command("ai-guide", "Print the guide for AI assistants writing changes in a bsm project (Markdown).");
aiGuide.SetAction(r => Commands.AiGuideCommand());
root.Subcommands.Add(aiGuide);
var mcp = new Command("mcp", "Run a Model Context Protocol server over stdio for AI assistants: tools that read and write this project's files. It never connects to a database.");
mcp.SetAction((r, ct) => Commands.Run(Globals(r), g => Badger.SqlMigrate.Cli.Mcp.BsmMcpServer.RunAsync(g.ProjectDir, ct)));
root.Subcommands.Add(mcp);

// "@tag" is a plan target, not a response file.
return await root.Parse(args, new ParserConfiguration { ResponseFileTokenReplacer = null }).InvokeAsync();
