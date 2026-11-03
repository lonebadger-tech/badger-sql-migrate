using System.CommandLine;
using Badger.SqlMigrate.Cli;
using Badger.SqlMigrate.Core.Configuration;

var project = new Option<string>("--project", "-C") { Description = "Project directory containing badger.yaml and badger.plan.", Recursive = true, DefaultValueFactory = _ => "." };
var environment = new Option<string?>("--env", "-e") { Description = "Environment from badger.yaml (or BADGER_ENV; default: defaultEnvironment).", Recursive = true };
var connection = new Option<string?>("--connection-string") { Description = "Override the environment's connection string.", Recursive = true };
var claim = new Option<bool>("--claim-environment") { Description = "Allow a database recorded under another environment name (e.g. restored from prod) and record this one.", Recursive = true };
var vars = new Option<string[]>("--var") { Description = "Script variable, name=value. Repeatable.", Recursive = true };
var json = new Option<bool>("--json") { Description = "Machine-readable output on stdout.", Recursive = true };
var verbose = new Option<bool>("--verbose", "-v") { Description = "Show server messages and details.", Recursive = true };
var noColor = new Option<bool>("--no-color") { Description = "Disable ANSI colors (also honors NO_COLOR).", Recursive = true };

GlobalOptions Globals(ParseResult r) => new(
    r.GetValue(project)!,
    r.GetValue(environment),
    r.GetValue(connection),
    r.GetValue(vars) ?? [],
    r.GetValue(claim),
    new ConsoleOutput(r.GetValue(json), r.GetValue(verbose), r.GetValue(noColor)));

var root = new RootCommand("Badger SQL Migrate - hash-certified, plan-based database migrations with real up and down.");
root.Options.Add(project);
root.Options.Add(environment);
root.Options.Add(connection);
root.Options.Add(claim);
root.Options.Add(vars);
root.Options.Add(json);
root.Options.Add(verbose);
root.Options.Add(noColor);

// init
var initProvider = new Option<string>("--provider") { Description = "Database provider.", DefaultValueFactory = _ => "sqlserver" };
var init = new Command("init", "Create badger.yaml, badger.plan and folders in the project directory.") { initProvider };
init.SetAction((r, ct) => Commands.Run(Globals(r), g => Commands.Init(g, r.GetValue(initProvider)!)));
root.Subcommands.Add(init);

// add
var addName = new Argument<string>("name") { Description = "Change name (letters, digits, '-', '_', '.')." };
var addRequires = new Option<string[]>("--requires", "-r") { Description = "Changes this one depends on.", AllowMultipleArgumentsPerToken = true };
var addNote = new Option<string?>("--note", "-n") { Description = "Note stored in the plan." };
var addNoTx = new Option<bool>("--notx") { Description = "Run outside a transaction." };
var addIrreversible = new Option<bool>("--irreversible") { Description = "No revert script." };
var add = new Command("add", "Add a change: creates changes/<name>/ scripts and appends it to badger.plan.") { addName, addRequires, addNote, addNoTx, addIrreversible };
add.SetAction((r, ct) => Commands.Run(Globals(r), g => Commands.Add(g, r.GetValue(addName)!, r.GetValue(addRequires) ?? [], r.GetValue(addNote), r.GetValue(addNoTx), r.GetValue(addIrreversible))));
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
var revert = new Command("revert", "Revert changes using the revert scripts stored in the database at deploy time.") { revertTo, revertLast, revertAll, dryRun, yes, allowDrift };
revert.SetAction((r, ct) => Commands.Run(Globals(r), g => Commands.Revert(g, r.GetValue(revertTo), r.GetValue(revertLast), r.GetValue(revertAll), r.GetValue(dryRun), r.GetValue(yes), r.GetValue(allowDrift), ct)));
root.Subcommands.Add(revert);

// rebase
var rebaseTo = new Option<string>("--to") { Description = "Revert to this change or @tag, then deploy to the plan head.", Required = true };
var rebase = new Command("rebase", "Revert to a change, then redeploy to head (handy while iterating on a change).") { rebaseTo, yes, allowDrift };
rebase.SetAction((r, ct) => Commands.Run(Globals(r), g => Commands.Rebase(g, r.GetValue(rebaseTo)!, r.GetValue(yes), r.GetValue(allowDrift), ct)));
root.Subcommands.Add(rebase);

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
var baselineTo = new Option<string>("--to") { Description = "Mark changes up to this one as already applied.", Required = true };
var baseline = new Command("baseline", "Adopt an existing database: record changes as applied without running them.") { baselineTo, yes };
baseline.SetAction((r, ct) => Commands.Run(Globals(r), g => Commands.Baseline(g, r.GetValue(baselineTo)!, r.GetValue(yes), ct)));
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

// "@tag" is a plan target, not a response file.
return await root.Parse(args, new ParserConfiguration { ResponseFileTokenReplacer = null }).InvokeAsync();
