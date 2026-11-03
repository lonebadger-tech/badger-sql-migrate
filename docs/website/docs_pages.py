"""The documentation section: /badger-sql-migrate/docs/..."""
from build import BASE, VERSION, Page, code, sh, sql, yaml

SIDEBAR = [
    ("Get started", [
        ("Overview", "docs/"),
        ("Install", "docs/install/"),
        ("Quick start", "docs/quick-start/"),
        ("Project layout", "docs/project/"),
    ]),
    ("Concepts", [
        ("The plan", "docs/plan/"),
        ("Changes and scripts", "docs/changes/"),
        ("Hash certification", "docs/certification/"),
        ("Revert (down)", "docs/revert/"),
        ("Teams and ordering", "docs/teamwork/"),
        ("Repeatables", "docs/repeatables/"),
    ]),
    ("Configure", [
        ("Environments", "docs/environments/"),
        ("Authentication", "docs/authentication/"),
        ("Connection settings", "docs/connections/"),
        ("Variables", "docs/variables/"),
        ("Always Encrypted", "docs/always-encrypted/"),
    ]),
    ("Operate", [
        ("Command reference", "docs/commands/"),
        ("CI/CD", "docs/ci-cd/"),
        ("Existing databases", "docs/baseline/"),
        ("Journal tables", "docs/journal/"),
        ("Troubleshooting", "docs/troubleshooting/"),
    ]),
    ("Extend", [
        ("Library and providers", "docs/extending/"),
    ]),
]


def doc(path, title, lede, body, description=None):
    return Page(path, f"{title} · Badger SQL Migrate", description or lede, body, sidebar=SIDEBAR, heading=title, lede=lede)


def link(path, label):
    return f'<a href="{BASE}/{path}">{label}</a>'


def pages():
    return [overview(), install(), quick_start(), project(), plan(), changes(), certification(), revert(), teamwork(),
            repeatables(), environments(), authentication(), connections(), variables(), always_encrypted(), commands(),
            ci_cd(), baseline(), journal(), troubleshooting(), extending()]


# ---------------------------------------------------------------- get started

def overview():
    return doc("docs/", "Documentation", "Everything about Badger SQL Migrate, from the first change to production pipelines.", f"""
<p>Badger SQL Migrate deploys hand-written SQL changes to SQL Server and Azure SQL, in an order you control, and proves afterwards that every database received exactly what you intended. It is a single command-line tool, <code>badger</code>, for Windows, macOS and Linux.</p>

<h2>Five ideas</h2>
<ol>
  <li><strong>Changes have names.</strong> A change is a folder (<code>changes/add-customer-email/</code>) with a <code>deploy.sql</code>, a <code>revert.sql</code> and an optional <code>verify.sql</code>. No version numbers to collide.</li>
  <li><strong>The plan decides the order.</strong> <code>badger.plan</code> lists changes one per line. Git merges it without conflicts. {link("docs/plan/", "The plan")}</li>
  <li><strong>Everything is hashed.</strong> Each change has a SHA-256 hash, and each position in the plan a chain hash over everything before it. Identical head hash, identical history. {link("docs/certification/", "Hash certification")}</li>
  <li><strong>Down is real.</strong> The database stores the revert script at deploy time and uses it to roll back. {link("docs/revert/", "Revert")}</li>
  <li><strong>Environments hold the differences.</strong> Servers, identities, variables and encryption keys differ per environment; change scripts do not. {link("docs/environments/", "Environments")}</li>
</ol>

<h2>Where to start</h2>
<ul>
  <li>New project: {link("docs/install/", "Install")}, then the {link("docs/quick-start/", "Quick start")}.</li>
  <li>Database already in production: {link("docs/baseline/", "Existing databases")}.</li>
  <li>Setting up a pipeline: {link("docs/ci-cd/", "CI/CD")} and {link("docs/authentication/", "Authentication")}.</li>
  <li>Looking for a script pattern: {link("examples/", "Examples")}.</li>
</ul>

<h2>Supported databases</h2>
<p>SQL Server 2016 SP1 and later (on Windows, Linux and containers), Azure SQL Database and Azure SQL Managed Instance. The engine is vendor-neutral, so other databases can be added as providers ({link("docs/extending/", "Library and providers")}); SQL Server is the only provider today.</p>
""")


def install():
    return doc("docs/install/", "Install", "A .NET tool, or a single executable for your platform.", f"""
<h2>.NET tool</h2>
{sh('''dotnet tool install --global Badger.SqlMigrate
badger --version''')}
<p>Requires the .NET 10 SDK. Pin a version per repository with a local tool manifest (<code>dotnet new tool-manifest</code>, then <code>dotnet tool install Badger.SqlMigrate</code>) and run it as <code>dotnet badger</code>.</p>

<h2>Standalone executable</h2>
<p>{link("download/", "Download")} the archive for your platform and put <code>badger</code> on your <code>PATH</code>. The executables are self-contained: no .NET runtime needed. Builds exist for Windows (x64, Arm64), macOS (Apple silicon, Intel) and Linux (x64, Arm64, Alpine x64), plus a portable build for any OS with the .NET 10 runtime.</p>

<h2>Check it works</h2>
{sh('''badger --help
badger doctor -C path/to/project -e dev     # configuration, sign-in, connection, journal''')}

<h2>Platform notes</h2>
<ul>
  <li><strong>macOS:</strong> binaries are signed ad hoc but not notarized; clear the quarantine flag once with <code>xattr -d com.apple.quarantine badger</code>.</li>
  <li><strong>Linux:</strong> Kerberos (integrated) authentication needs the system GSSAPI library. Entra interactive sign-in caches tokens with libsecret; without it, use device code or set <code>entra.allowUnencryptedTokenCache</code>.</li>
  <li><strong>Windows:</strong> Windows authentication uses SSPI; the Windows certificate store, CNG and CSP key stores for Always Encrypted are available.</li>
</ul>
""")


def quick_start():
    return doc("docs/quick-start/", "Quick start", "From an empty folder to a certified database in ten minutes.", f"""
<p>You need a SQL Server you can reach. A local container works:</p>
{sh('''docker run -d --name sql -e ACCEPT_EULA=Y -e MSSQL_SA_PASSWORD="Your_str0ng_password" -p 1433:1433 mcr.microsoft.com/mssql/server:2022-latest
export BADGER_LOCAL_PASSWORD="Your_str0ng_password"''')}

<h2>1. Create a project</h2>
{sh('''badger init -C db
cd db''')}
<p>This writes <code>badger.yaml</code>, an empty <code>badger.plan</code>, the <code>changes/</code>, <code>repeatable/</code> and <code>environments/</code> folders, and a <code>.gitattributes</code> rule that lets git merge the plan. Edit the <code>local</code> environment in <code>badger.yaml</code> so <code>database:</code> names a database that exists.</p>

<h2>2. Add a change</h2>
{sh('''badger add create-customer''')}
{sql('''CREATE TABLE dbo.Customer (
    Id int IDENTITY NOT NULL CONSTRAINT PK_Customer PRIMARY KEY,
    Name nvarchar(200) NOT NULL
);''', "changes/create-customer/deploy.sql")}
{sql('''DROP TABLE dbo.Customer;''', "changes/create-customer/revert.sql")}
{sql('''IF OBJECT_ID(N'dbo.Customer', N'U') IS NULL THROW 50000, N'Customer missing', 1;''', "changes/create-customer/verify.sql")}

<h2>3. Check, then deploy</h2>
{sh('''badger plan check          # no database needed: plan, folders, lint, variables
badger doctor              # can we sign in and connect?
badger deploy --dry-run
badger deploy''')}

<h2>4. Look at the result</h2>
{sh('''badger status''')}
{code('''environment    local
target         sqlserver://localhost:1433/App
  + create-customer                          2026-11-03 18:03Z you

database head  0e21ef9c72f3
plan head      0e21ef9c72f3
up to date and certified: database history is identical to the plan.''')}

<h2>5. Change your mind</h2>
{sh('''badger add add-customer-email --requires create-customer
# write deploy.sql / revert.sql
badger deploy
badger revert --last 1        # runs the revert.sql stored at deploy time
badger deploy                 # and forward again
badger log                    # who did what, when, from where''')}

<h2>Next</h2>
<ul>
  <li>Add a test and a production environment: {link("docs/environments/", "Environments")}.</li>
  <li>Sign in with Entra ID: {link("docs/authentication/", "Authentication")}.</li>
  <li>Commit <code>db/</code> to git and add <code>badger plan check</code> to CI: {link("docs/ci-cd/", "CI/CD")}.</li>
</ul>
""")


def project():
    return doc("docs/project/", "Project layout", "Files badger reads, and what each is for.", f"""
{code('''db/
  badger.yaml                   provider, environments, variables, preprocessor settings
  badger.plan                   ordered list of changes and tags
  .gitattributes                badger.plan merge=union, *.sql text eol=lf
  changes/
    <change-name>/
      deploy.sql                required
      revert.sql                required unless the change is !irreversible
      verify.sql                optional
  repeatable/                   re-applied when their hash changes (any folder depth)
    views/vActiveCustomer.sql
    procedures/usp_FindCustomer.sql
  environments/
    <environment>/              repeatable scripts for one environment only
      seed.sql''')}
<p>Pass the project folder with <code>-C</code> / <code>--project</code> (default: the current folder).</p>

<h2>Rules badger checks</h2>
<ul>
  <li>Every change in the plan has a folder with <code>deploy.sql</code>, and every folder in <code>changes/</code> is in the plan.</li>
  <li>Change names use letters, digits, <code>-</code>, <code>_</code> and <code>.</code>, starting with a letter or digit. Names are case-sensitive.</li>
  <li>A missing <code>revert.sql</code> is an error unless the plan marks the change <code>!irreversible</code>.</li>
  <li>Scripts are read as UTF-8. A byte-order mark and Windows line endings are ignored for hashing.</li>
</ul>
<p><code>badger plan check</code> reports all of these without a database. Put it in a pre-commit hook and in CI.</p>
""")


# ---------------------------------------------------------------- concepts

def plan():
    return doc("docs/plan/", "The plan", "badger.plan is the single source of deploy order.", f"""
{code('''# comments start with #
create-customer                                 @2026-11-03T14:10Z brock  # a note
add-customer-email [create-customer]            @2026-11-04T09:02Z alice
rebuild-fulltext [create-product] !notx         @2026-11-04T09:15Z bob
drop-legacy !irreversible                       @2026-11-05T08:00Z alice
@release-1.4''', "", "badger.plan")}

<h2>Line syntax</h2>
<table>
<tr><th>Part</th><th>Meaning</th></tr>
<tr><td><code>name</code></td><td>The change; matches <code>changes/name/</code>.</td></tr>
<tr><td><code>[a b]</code></td><td>Changes this one requires. They must appear earlier in the plan.</td></tr>
<tr><td><code>!notx</code></td><td>Run outside a transaction (see {link("docs/changes/", "Changes")}).</td></tr>
<tr><td><code>!irreversible</code></td><td>No revert script; revert stops here with an explicit error.</td></tr>
<tr><td><code>@timestamp author</code></td><td>When and by whom it was planned. Informational, not hashed.</td></tr>
<tr><td><code># note</code></td><td>A note. Not hashed.</td></tr>
<tr><td><code>@tag</code> (own line)</td><td>Tags the change above it. Use as <code>--to @tag</code>.</td></tr>
</table>
<p><code>badger add</code> and <code>badger tag</code> write these lines for you. The name, dependencies and flags are part of the change's hash; notes, authors and timestamps are not.</p>

<h2>Why one change per line</h2>
<p>When two developers each append a change on their branches, git's <code>union</code> merge driver keeps both lines instead of reporting a conflict. <code>badger init</code> adds the rule to <code>.gitattributes</code>:</p>
{code('''badger.plan merge=union''', "", ".gitattributes")}
<p>Run <code>badger plan check</code> after merging. It catches the cases a union merge cannot judge: the same name added twice, a dependency on a change that was removed, or a folder without a plan line. See {link("docs/teamwork/", "Teams and ordering")}.</p>

<h2>Targets</h2>
<p><code>--to</code> accepts a change name or a tag: <code>badger deploy --to @release-1.4</code> deploys up to and including the tagged change.</p>
""")


def changes():
    return doc("docs/changes/", "Changes and scripts", "deploy.sql, revert.sql, verify.sql, batches, transactions and the linter.", f"""
<h2>The three scripts</h2>
<table>
<tr><th>Script</th><th>Runs</th><th>Purpose</th></tr>
<tr><td><code>deploy.sql</code></td><td><code>badger deploy</code></td><td>Make the change.</td></tr>
<tr><td><code>revert.sql</code></td><td><code>badger revert</code>, <code>rebase</code></td><td>Undo exactly what deploy did.</td></tr>
<tr><td><code>verify.sql</code></td><td>After deploy, and in <code>badger verify</code></td><td>Raise an error if the change is not in effect.</td></tr>
</table>

<h2>Batches</h2>
<p>Scripts are split into batches on lines that contain only <code>GO</code> (optionally <code>GO 5</code> to repeat, and a trailing <code>-- comment</code>), the way SSMS and sqlcmd do. <code>GO</code> inside comments (including nested <code>/* */</code>), strings and bracketed names is ignored.</p>

<h2>Transactions</h2>
<p>By default a change runs in one transaction: every batch of <code>deploy.sql</code>, then <code>verify.sql</code>, then the journal row. Any error rolls back all of it and records a <code>fail</code> event; the database is as it was.</p>
<p>Some statements cannot run in a transaction. Mark those changes <code>!notx</code>: batches then run one after another without a transaction, and the journal row is written only after the last succeeds. Write such scripts so they can be re-run (<code>IF NOT EXISTS</code>), because a failure can leave partial work.</p>

<h2>Linter</h2>
<p>Scripts are parsed with Microsoft's T-SQL parser before anything runs. <code>plan check</code> and <code>deploy</code> report:</p>
<table>
<tr><th>Code</th><th>Severity</th><th>Finding</th></tr>
<tr><td><code>BSM001</code></td><td>Error</td><td>Statement cannot run in a transaction (ALTER/CREATE/DROP DATABASE, BACKUP, RESTORE, full-text catalog and index DDL, RECONFIGURE, resumable online index) and the change is not <code>!notx</code>.</td></tr>
<tr><td><code>BSM002</code></td><td>Error</td><td><code>USE</code>: badger deploys to the environment's database; switching databases inside a script breaks that.</td></tr>
<tr><td><code>BSM003</code></td><td>Warning</td><td>Explicit <code>BEGIN TRANSACTION</code> inside a transactional change. A <code>ROLLBACK</code> there would abort badger's transaction.</td></tr>
<tr><td><code>BSM000</code></td><td>Warning</td><td>The script did not parse; lint skipped (SQL Server may still accept it).</td></tr>
</table>

<h2>Immutability</h2>
<p>Once a change is deployed anywhere, treat its scripts as frozen. If its hash in your working copy differs from the hash a database recorded, <code>status</code> marks it <em>edited after deploy</em> and <code>deploy</code> refuses to continue. To change behaviour, add a new change. Whitespace counts; only BOMs and line endings are normalized.</p>

<h2>Output</h2>
<p>Messages from <code>PRINT</code> and low-severity <code>RAISERROR</code> appear with <code>-v</code>.</p>
""")


def certification():
    return doc("docs/certification/", "Hash certification", "How badger proves two databases got identical changes in the same order.", f"""
<h2>Script hash</h2>
<p>Each change's script hash is SHA-256 over its name, dependencies, flags, and the text of deploy, revert and verify, each part length-prefixed so text cannot move between scripts without changing the hash. Before hashing, a UTF-8 byte-order mark is removed and CRLF/CR become LF: a Windows checkout and a macOS checkout of the same commit hash identically. Every other difference, including whitespace, changes the hash.</p>

<h2>Chain id</h2>
<p>Each change also gets a chain id: SHA-256 of the provider, the change name, its script hash and the chain id of the change before it. Like a git commit id, it depends on everything earlier. The chain id of the last change is the <strong>plan head</strong> (<code>badger plan show</code>).</p>
<p>When a database deploys a change, it records a chain id computed from <em>its own</em> deploy order. If the database received a prefix of the plan, in plan order, with identical scripts, its head equals the plan's chain id at that position, and <code>badger status</code> reports it as <strong>certified</strong>. Two databases with the same head received byte-identical history.</p>

<h2>Rendered hash</h2>
<p>Scripts can contain {link("docs/variables/", "variables")} that differ per environment, so the SQL that runs in test and prod is not the same text. Badger therefore stores a second hash per change: SHA-256 of deploy and verify as rendered for that environment, plus one for the rendered revert. That separates "same change" (chain id, equal across environments) from "same SQL executed here" (rendered hash). <code>badger verify</code> reports changes whose variables now render differently; <code>revert</code> refuses to run a revert script that no longer matches.</p>

<h2>Schema fingerprint</h2>
<p>After each run badger records a fingerprint: a hash of a canonical, sorted description of the schema (tables, columns, types, defaults, indexes, keys, constraints, module definitions, types, sequences, synonyms). System-generated constraint names, which differ from server to server, are masked; badger's own schema is excluded. <code>badger verify</code> compares the live schema with the last fingerprint, so a hotfix made outside badger shows up as drift.</p>

<h2>What status tells you</h2>
<table>
<tr><th>Status</th><th>Meaning</th></tr>
<tr><td>up to date and certified</td><td>Every planned change applied, in plan order, with identical scripts.</td></tr>
<tr><td>applied, not in plan order</td><td>Same changes, different order (out-of-order deploys on a shared database).</td></tr>
<tr><td>edited after deploy</td><td>The working copy's scripts differ from what this database ran.</td></tr>
<tr><td>deployed but not in plan</td><td>An orphan: removed from the plan, or from another branch. It can still be reverted.</td></tr>
</table>
<p>Releases of badger itself publish SHA-256 sums for every download, so the tool can be verified the same way.</p>
""")


def revert():
    return doc("docs/revert/", "Revert (down)", "Rolling back with the scripts the database stored at deploy time.", f"""
{sh('''badger revert --last 1               # the most recently deployed change
badger revert --to create-customer    # everything deployed after it (it stays)
badger revert --to @release-1.4
badger revert --all
badger revert --to x --dry-run        # list, change nothing
badger rebase --to x                  # revert to x, then deploy to the head again''')}
<p>Changes are reverted in reverse deploy order, each in its own transaction (unless <code>!notx</code>), with a <code>revert</code> event in the log. Revert asks for confirmation; pass <code>--yes</code> in scripts.</p>

<h2>Stored scripts</h2>
<p>When a change is deployed, its deploy, revert and verify scripts are saved in the journal. Revert runs the <em>saved</em> revert script. You can switch branches, delete the change, or roll back from a machine that never had the code, and the database still knows how to undo itself. If your working copy's revert script differs from the saved one, badger says so and uses the saved one.</p>

<h2>Guards</h2>
<ul>
  <li><code>!irreversible</code> changes stop the revert before anything runs.</li>
  <li>Environments with <code>allowRevert: false</code> refuse revert and rebase. Use it for production and fix forward.</li>
  <li>If a variable used by the revert script changed since deploy, revert refuses (it would undo something other than what was done). Check <code>badger vars</code>; override with <code>--allow-variable-drift</code>.</li>
  <li>Protected environments ask you to type the environment name.</li>
</ul>

<h2>Writing good revert scripts</h2>
<ul>
  <li>Undo in reverse order of the deploy.</li>
  <li>Drop what you created; restore what you changed. For data changes, keep enough information in the deploy (for example a backup table) to restore it.</li>
  <li>Test the pair: <code>badger rebase --to &lt;previous change&gt;</code> on a dev database runs revert and deploy back to back. <code>badger verify</code> afterwards should match the original fingerprint.</li>
</ul>
""")


def teamwork():
    return doc("docs/teamwork/", "Teams and ordering", "Concurrent development without version collisions, and deploys that cannot interleave.", f"""
<h2>Two developers, same day</h2>
<p>Alice adds <code>add-customer-email</code> and Bob adds <code>add-order-index</code> on separate branches. Each appends a line to <code>badger.plan</code>. With version numbers they would both have picked <code>V12</code>; with names there is nothing to collide on, and git's union merge keeps both lines. <code>badger plan check</code> in CI verifies the merged plan.</p>

<h2>Shared databases and out-of-order changes</h2>
<p>Bob's change reached the shared dev database first. After the merge, Alice's line sits above Bob's in the plan, but Bob's change is already deployed. What happens next is the environment's <code>ordering</code>:</p>
<table>
<tr><th><code>ordering</code></th><th>Behaviour</th><th>Use for</th></tr>
<tr><td><code>dependency</code> (default)</td><td>Deploy Alice's change now (its dependencies are satisfied), flagged out of order. The database is up to date but not certified.</td><td>Shared dev and test databases</td></tr>
<tr><td><code>strict</code></td><td>Refuse. The database must receive changes in exact plan order.</td><td>Production</td></tr>
</table>
<p>Production databases only ever see the merged plan, in order, so they certify. Bring a dev database back to plan order with <code>badger rebase --to &lt;last common change&gt;</code>. Override per run with <code>--ordering strict|dependency</code>.</p>

<h2>Concurrent deploys</h2>
<p>Deploy, revert, baseline and provisioning hold an exclusive lock on the database for the whole run (<code>sp_getapplock</code>, session-owned, works on Azure SQL). A second pipeline waits up to <code>lockTimeoutSeconds</code> (default 300) and then fails with a message naming the lock. Read-only commands (<code>status</code>, <code>verify</code>, <code>log</code>) do not take the lock.</p>

<h2>Recommended workflow</h2>
<ol>
  <li>Each developer works against their own database (<code>local</code> environment).</li>
  <li><code>badger plan check</code> runs on every pull request.</li>
  <li>Merges deploy to a shared test environment (<code>ordering: dependency</code>).</li>
  <li>Releases deploy to production (<code>ordering: strict</code>, <code>protected: true</code>, <code>allowRevert: false</code>) and run <code>badger verify</code>.</li>
</ol>
""")


def repeatables():
    return doc("docs/repeatables/", "Repeatables", "Views, procedures, functions and environment-only scripts that re-run when they change.", f"""
<p>Objects that are defined rather than migrated, such as views, stored procedures, functions and triggers, belong in <code>repeatable/</code>. Write them with <code>CREATE OR ALTER</code>. After the changes in a deploy, badger applies every repeatable whose hash differs from the one it last applied, each in its own transaction.</p>

<h2>Order</h2>
<p>Repeatables run in path order (ordinal), with declared dependencies first:</p>
{sql('''-- badger:requires views/vActiveCustomer.sql functions/fnFullName.sql
CREATE OR ALTER PROCEDURE dbo.usp_FindCustomer ...''', "repeatable/procedures/usp_FindCustomer.sql")}
<p>Names are paths relative to <code>repeatable/</code>, with forward slashes. Cycles and unknown names are reported by <code>plan check</code>.</p>

<h2>Environment-only scripts</h2>
<p>Scripts under <code>environments/&lt;name&gt;/</code> behave like repeatables but run only in that environment, after the shared ones. They are named <code>@&lt;name&gt;/path</code> and may require shared repeatables. Use them for seed data, test users and environment-specific grants. Write them idempotently (<code>MERGE</code>, <code>IF NOT EXISTS</code>).</p>

<h2>What is recorded</h2>
<p>For each repeatable, the journal keeps the current and previous script and hash, and a <code>repeatable</code> event. Repeatables are not part of the plan certificate: they are re-applied by content, not by position.</p>

<h2>Changes or repeatables?</h2>
<ul>
  <li>Tables, columns, indexes, data: <strong>changes</strong> (they need explicit migrations and reverts).</li>
  <li>Views, procedures, functions, triggers: <strong>repeatables</strong> (the latest definition wins).</li>
  <li>Something a change depends on during deploy (for example a function used in a computed column): a <strong>change</strong>, because repeatables run after changes.</li>
</ul>
""")


# ---------------------------------------------------------------- configure

def environments():
    return doc("docs/environments/", "Environments", "badger.yaml: dev, test, prod and everything that differs between them.", f"""
{yaml('''provider: sqlserver
defaultEnvironment: dev                # also: BADGER_ENV, or --env / -e

variables: {}                         # see Variables

environments:
  _azure:                              # '_' prefix: template, used only through extends
    database: App
    tls: { mode: strict }
    authentication: entraInteractive
    entra: { tenantId: 7f3c1e2a-0000-0000-0000-000000000000 }

  dev:
    extends: _azure
    host: myapp-dev.database.windows.net

  prod:
    extends: _azure
    description: Production
    host: myapp-prod.database.windows.net
    protected: true
    allowRevert: false
    ordering: strict''', "badger.yaml")}

<h2>Inheritance</h2>
<p><code>extends</code> names another environment (or template) to start from. Maps merge key by key, so <code>entra:</code> in a child adds to the template's <code>entra:</code>; values and lists are replaced. Chains are allowed, cycles are errors. <code>badger env show -e prod</code> prints the merged result with secrets masked; <code>badger env list</code> shows every environment.</p>

<h2>Settings</h2>
<table>
<tr><th>Key</th><th>Default</th><th>Meaning</th></tr>
<tr><td><code>description</code></td><td></td><td>Shown in <code>env list</code>; <code>{{env.description}}</code></td></tr>
<tr><td><code>host</code>, <code>port</code>, <code>database</code></td><td>port 1433</td><td>Where to connect</td></tr>
<tr><td><code>connectionString</code></td><td></td><td>Raw SqlClient connection string; the fields above override it</td></tr>
<tr><td><code>authentication</code>, <code>user</code>, <code>password</code>, <code>entra</code></td><td></td><td>{link("docs/authentication/", "Authentication")}</td></tr>
<tr><td><code>tls</code>, <code>connectionProperties</code>, <code>ssh</code></td><td></td><td>{link("docs/connections/", "Connection settings")}</td></tr>
<tr><td><code>protected</code></td><td><code>false</code></td><td>Deploy, revert, rebase, baseline and destructive provisioning require typing the environment name, or <code>--yes</code></td></tr>
<tr><td><code>allowRevert</code></td><td><code>true</code></td><td><code>false</code> refuses revert and rebase</td></tr>
<tr><td><code>ordering</code></td><td><code>dependency</code></td><td><code>dependency</code> or <code>strict</code> ({link("docs/teamwork/", "Teams")})</td></tr>
<tr><td><code>stateSchema</code></td><td><code>badger</code></td><td>Schema for the {link("docs/journal/", "journal tables")}</td></tr>
<tr><td><code>commandTimeoutSeconds</code></td><td>driver default (30)</td><td>Per-batch timeout</td></tr>
<tr><td><code>lockTimeoutSeconds</code></td><td><code>300</code></td><td>Wait for another deployer</td></tr>
<tr><td><code>variables</code></td><td></td><td>Environment-specific {link("docs/variables/", "variables")}</td></tr>
<tr><td><code>alwaysEncrypted</code></td><td></td><td>{link("docs/always-encrypted/", "Always Encrypted keys")}</td></tr>
</table>
<p>Unknown keys inside known blocks (for example a misspelled <code>entra.tennantId</code>) are errors, so typos never silently fall back to defaults.</p>

<h2>Secrets</h2>
<p>Any value can reference <code>${{env:NAME}}</code> (an environment variable) or <code>${{file:path}}</code> (a file, such as a mounted secret). They are resolved only for the environment being used, so a missing production secret never breaks a dev run. Never commit passwords; <code>badger.yaml</code> belongs in source control.</p>

<h2>Database ownership</h2>
<p>The first deploy records the environment name in the database. If you later run as a different environment against that database (for example prod settings pointing at a test server after a copy-paste mistake), badger refuses:</p>
{code('''error: This database belongs to environment 'test', but you are running as 'prod'. Check --env and the
connection settings. If the database was intentionally copied (e.g. prod restored into test), pass --claim-environment.''')}
<p><code>--claim-environment</code> re-records the database under the new name. Every journal event also records the environment that wrote it.</p>

<h2>Environment-only scripts</h2>
<p>See {link("docs/repeatables/", "Repeatables")}: <code>environments/&lt;name&gt;/**/*.sql</code> runs only there.</p>
""")


def authentication():
    rows = [
        ("password", "SQL login", "<code>user</code>, <code>password</code>", ""),
        ("integrated", "Windows SSPI, or Kerberos on macOS/Linux", "", "Run <code>kinit user@REALM</code> first on macOS/Linux"),
        ("entraInteractive", "Browser sign-in", "<code>tenantId</code>, <code>loginHint</code>, <code>clientId</code>", "<strong>MFA and Conditional Access.</strong> Token cached between runs"),
        ("entraDeviceCode", "Code in the terminal, sign in on any device", "<code>tenantId</code>", "<strong>MFA.</strong> Works over SSH; cached between runs"),
        ("entraDefault", "DefaultAzureCredential chain", "<code>tenantId</code>, <code>clientId</code> (managed identity)", "Environment, workload identity, managed identity, Visual Studio, Azure CLI, PowerShell, azd"),
        ("entraAzureCli", "The <code>az login</code> account", "<code>tenantId</code>", "MFA happens in the CLI"),
        ("entraAzurePowerShell", "The <code>Connect-AzAccount</code> account", "<code>tenantId</code>", ""),
        ("entraAzureDeveloperCli", "The <code>azd auth login</code> account", "<code>tenantId</code>", ""),
        ("entraManagedIdentity", "Managed identity", "user-assigned: <code>clientId</code>, <code>managedIdentityObjectId</code> or <code>managedIdentityResourceId</code>", "VMs, App Service, Functions, Container Apps, AKS"),
        ("entraServicePrincipal", "App registration secret", "<code>tenantId</code>, <code>clientId</code>, <code>clientSecret</code>", ""),
        ("entraServicePrincipalCertificate", "App registration certificate", "<code>tenantId</code>, <code>clientId</code>, <code>certificatePath</code> (+ <code>certificatePassword</code>) or <code>certificateThumbprint</code>", "PFX or PEM; thumbprint = Windows certificate store; <code>sendCertificateChain</code> for SN+I"),
        ("entraWorkloadIdentity", "Federated workload identity", "<code>tenantId</code>, <code>clientId</code>", "AKS and other OIDC issuers (<code>AZURE_FEDERATED_TOKEN_FILE</code>)"),
        ("entraAzurePipelines", "Azure DevOps service connection", "<code>tenantId</code>, <code>clientId</code>, <code>serviceConnectionId</code>, <code>systemAccessToken</code>", "Workload identity federation, no secret"),
        ("entraEnvironment", "Environment variables", "", "<code>AZURE_TENANT_ID</code>, <code>AZURE_CLIENT_ID</code>, <code>AZURE_CLIENT_SECRET</code> or <code>AZURE_CLIENT_CERTIFICATE_PATH</code>"),
        ("entraAccessToken", "A token you already have", "<code>accessToken</code>", "e.g. <code>${env:SQL_TOKEN}</code> from <code>az account get-access-token</code>"),
        ("entraIntegrated", "Entra Integrated (federated domain)", "<code>user</code> (optional)", "Native SqlClient"),
        ("entraPassword", "Entra user name and password", "<code>user</code>, <code>password</code>", "No MFA; deprecated by Microsoft; prints a warning"),
    ]
    table = "<table><tr><th><code>authentication</code></th><th>Signs in with</th><th>Settings</th><th>Notes</th></tr>" + "".join(
        f"<tr><td><code>{a}</code></td><td>{b}</td><td>{c}</td><td>{d}</td></tr>" for a, b, c, d in rows) + "</table>"
    return doc("docs/authentication/", "Authentication", "SQL logins, Windows and Kerberos, and every Microsoft Entra ID flow, including MFA.", f"""
{table}
<p>Settings marked in the table go under <code>entra:</code> (except <code>user</code> and <code>password</code>).</p>

<h2>MFA without signing in every time</h2>
{yaml('''environments:
  dev:
    host: myapp-dev.database.windows.net
    database: App
    authentication: entraInteractive
    entra:
      tenantId: 7f3c1e2a-0000-0000-0000-000000000000
      loginHint: you@contoso.com''', "badger.yaml")}
<p>The first run opens a browser for sign-in, MFA included. The token is kept in the operating system's encrypted store (Keychain on macOS, DPAPI on Windows, libsecret on Linux) and a small sign-in record (account and tenant ids, no tokens) is saved in <code>~/.badger/auth/</code>, so later runs are silent until the refresh token expires. <code>badger auth clear</code> forgets saved sign-ins. Set <code>entra.tokenCache: memory</code> to sign in on every run. Without a browser (SSH sessions), use <code>entraDeviceCode</code>.</p>

<h2>More <code>entra</code> settings</h2>
<table>
<tr><th>Key</th><th>Meaning</th></tr>
<tr><td><code>authorityHost</code></td><td><code>azurePublicCloud</code> (default), <code>azureGovernment</code>, <code>azureChina</code>, or an authority URL</td></tr>
<tr><td><code>additionallyAllowedTenants</code></td><td>Other tenants the credential may get tokens for</td></tr>
<tr><td><code>tokenCache</code></td><td><code>persistent</code> (default) or <code>memory</code></td></tr>
<tr><td><code>allowUnencryptedTokenCache</code></td><td>Linux without libsecret: allow an unencrypted cache file (off by default)</td></tr>
<tr><td><code>scope</code></td><td>Override the token audience (default: what the server asks for, normally <code>https://database.windows.net/.default</code>)</td></tr>
<tr><td><code>timeoutSeconds</code></td><td>Limit for interactive sign-in and CLI credential processes</td></tr>
</table>

<h2>Examples</h2>
{yaml('''  ci:
    authentication: entraServicePrincipalCertificate
    entra:
      tenantId: 7f3c1e2a-0000-0000-0000-000000000000
      clientId: 0a1b2c3d-0000-0000-0000-000000000000
      certificatePath: ${env:DEPLOY_CERT_PATH}
      certificatePassword: ${env:DEPLOY_CERT_PASSWORD}

  appservice:
    authentication: entraManagedIdentity
    entra: { clientId: 9d8c7b6a-0000-0000-0000-000000000000 }   # user-assigned; omit for system-assigned

  gov:
    host: myapp.database.usgovcloudapi.net
    authentication: entraDefault
    entra: { authorityHost: azureGovernment }

  corp:
    host: sql01.corp.contoso.com
    authentication: integrated''')}

<h2>Raw connection strings</h2>
<p>A <code>connectionString</code> with <code>Authentication=Active Directory Interactive</code> (or any other SqlClient Entra mode) also works; badger registers SqlClient's own Entra provider. Setting <code>authentication:</code> takes precedence and removes conflicting keywords from the string.</p>

<h2>Checking sign-in</h2>
<p><code>badger doctor</code> gets a token before connecting, so you can tell an identity problem from a network or permission problem:</p>
{code('''provider       SQL Server / Azure SQL
environment    prod (protected)
auth           EntraInteractive
entra token    ok, expires 2026-11-03 19:22Z
server         Azure SQL Database 12.0.2000.8 (SQL Azure)
login          you@contoso.com
database       App
encrypted      TRUE
recorded env   prod
journal        14 change(s) deployed
connection ok.''')}
<p>The Entra identity needs a database user (<code>CREATE USER [you@contoso.com] FROM EXTERNAL PROVIDER</code>) with rights to run your changes and to create the <code>badger</code> schema on first deploy.</p>
""")


def connections():
    return doc("docs/connections/", "Connection settings", "TLS, any SqlClient keyword, and SSH tunnels.", f"""
<h2>TLS</h2>
{yaml('''tls:
  mode: strict                       # default | optional | mandatory | strict (TDS 8.0)
  hostNameInCertificate: myapp.database.windows.net
  serverCertificate: certs/sql01.cer # pin the server certificate
  trustServerCertificate: false      # true only for local development''')}

<h2>Any SqlClient setting</h2>
<p><code>connectionProperties</code> accepts every keyword SqlClient understands and applies them last, over everything else. Invalid keywords or values are reported by <code>badger doctor</code> and before connecting.</p>
{yaml('''connectionProperties:
  Connect Retry Count: 3
  Connect Retry Interval: 10
  Connect Timeout: 30
  Command Timeout: 120
  ApplicationIntent: ReadWrite
  Multi Subnet Failover: true
  Failover Partner: sql02
  Server SPN: MSSQLSvc/sql01.corp.contoso.com:1433
  IP Address Preference: IPv4First
  Max Pool Size: 10
  Application Name: release-pipeline''')}
<p>Common Azure SQL choices: <code>Connect Retry Count</code> and <code>Connect Retry Interval</code> for transient faults, <code>Multi Subnet Failover</code> for failover groups, <code>tls.mode: strict</code> where supported.</p>

<h2>SSH tunnels</h2>
<p>Reach a database behind a bastion host. Badger opens a local port forward and connects through it; TLS is still validated against the real server name.</p>
{yaml('''ssh:
  host: bastion.example.com
  port: 22
  user: deploy
  privateKeyFile: ~/.ssh/id_ed25519
  privateKeyPassphrase: ${env:SSH_KEY_PASSPHRASE}
  # password: ${env:SSH_PASSWORD}
  # knownHostsFile: ~/.ssh/known_hosts          (default)
  # hostKeyFingerprint: SHA256:abc...           (pin instead of known_hosts)''')}
<p>The bastion's host key must be in <code>known_hosts</code> (<code>ssh-keyscan bastion.example.com &gt;&gt; ~/.ssh/known_hosts</code>) or pinned with <code>hostKeyFingerprint</code>. <code>insecureSkipHostKeyCheck: true</code> exists for throwaway environments and prints a warning every time.</p>

<h2>Overriding for one run</h2>
<p><code>--connection-string "..."</code> connects exactly there, replacing the environment's host, port and database; authentication and other settings still apply.</p>
""")


def variables():
    builtins = [
        ("{env}", "{env.name}", "Environment name"), ("{env.description}", "", "Environment description"),
        ("{db}", "{db.name}", "Database name"), ("{db.host}", "", "Server host"), ("{db.port}", "", "Server port"),
        ("{change}", "{change.name}", "Current change or repeatable"), ("{change.kind}", "", "deploy, revert, verify or repeatable"),
        ("{change.hash}", "", "The change's script hash"), ("{badger.provider}", "", "Provider id (sqlserver)"),
        ("{badger.version}", "", "Tool version"), ("{badger.schema}", "", "Journal schema"), ("{plan.head}", "", "Plan head chain id"),
        ("{deploy.user}", "", "OS user running badger *"), ("{deploy.host}", "", "Machine running badger *"),
        ("{deploy.utc}", "", "Run start, ISO 8601 UTC *"), ("{deploy.date}", "", "Run start date, UTC *"), ("{deploy.id}", "", "Unique id of the run *"),
    ]
    table = "<table><tr><th>Variable</th><th>Also</th><th>Value</th></tr>" + "".join(
        f"<tr><td><code>{a}</code></td><td>{f'<code>{b}</code>' if b else ''}</td><td>{c}</td></tr>" for a, b, c in builtins) + "</table>"
    return doc("docs/variables/", "Variables", "Scripts are preprocessed: {env}, {db}, your own variables, and $(sqlcmd) syntax.", f"""
<p>Before a script reaches the database, badger replaces variables in it:</p>
{sql('''SELECT * FROM [calc_{env}].[table1];       -- test:  SELECT * FROM [calc_test].[table1];
SELECT * FROM [calc_$(env)].[table1];      -- the same, sqlcmd syntax''')}

<h2>Syntax</h2>
<table>
<tr><th>Write</th><th>Gets</th></tr>
<tr><td><code>{{name}}</code>, <code>{{group.name}}</code></td><td>The variable's value</td></tr>
<tr><td><code>{{var.name}}</code></td><td>A user variable, explicitly</td></tr>
<tr><td><code>$(name)</code></td><td>The same variables, sqlcmd style</td></tr>
<tr><td><code>{{{{name}}}}</code></td><td>A literal <code>{{name}}</code></td></tr>
<tr><td><code>-- badger:preprocess off</code> as the first line</td><td>The script untouched</td></tr>
</table>
<p>Only an identifier directly inside the braces is a variable. ODBC escapes (<code>{{fn NOW()}}</code>, <code>{{d '2026-01-01'}}</code>), JSON (<code>'{{"a":1}}'</code>) and format strings (<code>'{{0}}'</code>) are left alone. Names are case-insensitive.</p>

<h2>Built-in variables</h2>
{table}
<p>* Per-run values: a script that uses them renders differently on every run, so it is not tracked by rendered hash. The names <code>env</code>, <code>db</code>, <code>change</code>, <code>badger</code>, <code>plan</code> and <code>deploy</code> (and anything under them) are reserved.</p>

<h2>Your variables</h2>
{yaml('''variables:                       # every environment
  AppRole: app_reader
  schemas:                       # groups become {schemas.calc}, {schemas.stage}
    calc: calc_{env}             # values may use other variables and built-ins
    stage: stage_{env}
  calcTable: "[{schemas.calc}].[table1]"

environments:
  prod:
    variables:                   # this environment only; wins over the shared ones
      AppRole: app_reader_prod
      Region: ${env:DEPLOY_REGION} # secrets and env vars work here too''', "badger.yaml")}
<p>Precedence, lowest to highest: shared <code>variables</code>, the environment's <code>variables</code> (including what it inherits through <code>extends</code>), then <code>--var name=value</code> on the command line. Circular references are errors.</p>
<p class="callout"><strong>YAML tip:</strong> in an inline map (<code>{{ a: b }}</code>), braces are YAML syntax, so quote values that contain variables: <code>{{ calc: "calc_{{env}}" }}</code>. In block style (one key per line) no quotes are needed.</p>

<h2>Unknown variables</h2>
<p>An unknown <code>{{name}}</code> or <code>$(name)</code> is an error that names the script and line, so a typo never reaches the database. If your scripts contain many literal braces, set <code>preprocessor.unknown: keep</code> to leave unknown <code>{{name}}</code> tokens as they are (unknown built-ins such as <code>{{env.nmae}}</code> are still errors).</p>
{yaml('''preprocessor:
  unknown: error        # error (default) | keep
  sqlcmdSyntax: true    # expand $(name) too (default)''', "badger.yaml")}
<p><code>badger plan check</code> also fails when a script uses a variable that some environment does not define, and names those environments.</p>

<h2>Seeing the result</h2>
{sh('''badger vars -e prod                        # every variable: value, source, description
badger render create-calc-schema -e prod   # the exact SQL prod will receive
badger render create-calc-schema -e prod --kind revert
badger deploy -e prod --script review.sql  # all pending changes, rendered, for review''')}

<h2>Variables and certification</h2>
<p>Raw scripts are hashed and certified, so the same change certifies identically in every environment. The rendered SQL is hashed separately per environment (see {link("docs/certification/", "Hash certification")}). If a variable's value changes after a change was deployed, <code>verify</code> reports it and <code>revert</code> refuses unless you pass <code>--allow-variable-drift</code>.</p>
""")


def always_encrypted():
    return doc("docs/always-encrypted/", "Always Encrypted", "Column master keys and column encryption keys per environment, provisioned and rotated by badger.", f"""
<p>Always Encrypted keys are a classic per-environment difference: production's column master key lives in production's Key Vault, test's somewhere else. Change scripts should not contain key paths or encrypted key values. Badger splits it cleanly:</p>
<ul>
  <li>Scripts reference a <strong>column encryption key name</strong> (the same in every environment).</li>
  <li>Each environment maps that name to <strong>its own column master key</strong> in <code>badger.yaml</code>.</li>
  <li>Before changes run, badger <strong>creates</strong> missing keys and <strong>rotates</strong> keys whose master key changed.</li>
</ul>

{sql('''ALTER TABLE dbo.Customer ADD Ssn char(11) COLLATE Latin1_General_BIN2
    ENCRYPTED WITH (COLUMN_ENCRYPTION_KEY = CEK_Customer, ENCRYPTION_TYPE = Deterministic,
                    ALGORITHM = 'AEAD_AES_256_CBC_HMAC_SHA_256') NULL;''', "changes/add-customer-ssn/deploy.sql")}
{yaml('''environments:
  test:
    alwaysEncrypted:
      columnMasterKeys:
        CMK_Test:
          keyStore: AZURE_KEY_VAULT
          keyPath: https://myapp-test-kv.vault.azure.net/keys/cmk/1a2b3c4d
      columnEncryptionKeys:
        CEK_Customer: { columnMasterKey: CMK_Test }

  prod:
    alwaysEncrypted:
      attestation: { protocol: AAS, url: "https://myapp.weu.attest.azure.net/attest/SgxEnclave" }
      keyStores:
        azureKeyVault:
          authentication: entraManagedIdentity     # optional: a different identity for Key Vault
          entra: { clientId: 9d8c7b6a-0000-0000-0000-000000000000 }
          trustedEndpoints: [vault.azure.net, managedhsm.azure.net]
      columnMasterKeys:
        CMK_Prod_2026:
          keyStore: AZURE_KEY_VAULT
          keyPath: https://myapp-prod-kv.vault.azure.net/keys/cmk/9f8e7d6c
          enclaveComputations: true
      columnEncryptionKeys:
        CEK_Customer: { columnMasterKey: CMK_Prod_2026 }''', "badger.yaml")}

<h2>Settings</h2>
<table>
<tr><th>Key</th><th>Meaning</th></tr>
<tr><td><code>enabled</code></td><td>Default <code>true</code>: the connection uses <code>Column Encryption Setting=Enabled</code></td></tr>
<tr><td><code>attestation</code></td><td><code>protocol</code> (<code>AAS</code>, <code>HGS</code>, <code>None</code>) and <code>url</code> for secure enclaves</td></tr>
<tr><td><code>keyStores.azureKeyVault</code></td><td>Optional <code>authentication</code> and <code>entra</code> for Key Vault (default: the environment's Entra identity, otherwise <code>entraDefault</code>), <code>trustedEndpoints</code> for sovereign clouds and Managed HSM</td></tr>
<tr><td><code>keyStores.custom</code></td><td>List of <code>name</code>, <code>assembly</code> (path relative to the project) and <code>type</code>: your own <code>SqlColumnEncryptionKeyStoreProvider</code>, e.g. an HSM</td></tr>
<tr><td><code>columnMasterKeys.&lt;name&gt;</code></td><td><code>keyStore</code>, <code>keyPath</code>, <code>enclaveComputations</code></td></tr>
<tr><td><code>columnEncryptionKeys.&lt;name&gt;</code></td><td><code>columnMasterKey</code>: which CMK protects it in this environment</td></tr>
</table>
<p>Key stores: <code>AZURE_KEY_VAULT</code> (Key Vault and Managed HSM), <code>MSSQL_CERTIFICATE_STORE</code>, <code>MSSQL_CNG_STORE</code>, <code>MSSQL_CSP_PROVIDER</code> (these three on Windows only), or a custom name. Short aliases: <code>azureKeyVault</code>, <code>certificateStore</code>, <code>cng</code>, <code>csp</code>.</p>

<h2>What deploy does</h2>
<ol>
  <li>Compares the configuration with <code>sys.column_master_keys</code> and <code>sys.column_encryption_keys</code>.</li>
  <li>Creates missing column master keys. Enclave-enabled keys are signed with the key itself.</li>
  <li>Creates missing column encryption keys: a new random 256-bit key, encrypted on your machine with the column master key (RSA-OAEP). The plaintext key never leaves the process and is never logged.</li>
  <li>Runs your changes.</li>
</ol>
<p>Your identity needs <em>wrap/unwrap/sign</em> rights on the key (Key Vault crypto user) and <code>ALTER ANY COLUMN MASTER KEY</code> / <code>ALTER ANY COLUMN ENCRYPTION KEY</code> in the database.</p>

<h2>Rotating a column master key</h2>
<ol>
  <li>Create the new key in Key Vault and add it under <code>columnMasterKeys</code> with a new name.</li>
  <li>Point the column encryption key at it: <code>CEK_Customer: {{ columnMasterKey: CMK_Prod_2027 }}</code>.</li>
  <li><code>badger deploy</code> (or <code>badger env provision</code>) adds a second value to the CEK: the same data key, decrypted with the old CMK and re-encrypted with the new one. Data does not change; both keys work.</li>
  <li>When every application can reach the new key, run <code>badger env provision --allow-destructive</code> to drop the value protected by the old CMK. This asks for confirmation.</li>
</ol>
{sh('''badger env status -e prod''')}
{code('''  = column master key CMK_Prod_2026: AZURE_KEY_VAULT key path matches
  + column master key CMK_Prod_2027: create column master key in AZURE_KEY_VAULT
  + column encryption key CEK_Customer: add value protected by CMK_Prod_2027 (rotation from CMK_Prod_2026)
  - column encryption key CEK_Customer: drop value protected by old column master key CMK_Prod_2026 (rotation complete)''')}

<h2>Conflicts</h2>
<p>SQL Server cannot change a column master key's key path. If the database has <code>CMK_Prod_2026</code> pointing somewhere other than your configuration, badger stops with a conflict instead of guessing. Define a new CMK name and rotate. Keys that exist in the database but not in your configuration are reported for information and never dropped.</p>

<h2>Fingerprints across environments</h2>
<p>The schema fingerprint records which CEK name protects each encrypted column, never key paths or values, so environments with different keys still produce the same fingerprint for the same schema.</p>
""")


# ---------------------------------------------------------------- operate

def commands():
    cmds = [
        ("init", "[--provider sqlserver]", "Create badger.yaml, badger.plan, folders and .gitattributes."),
        ("add &lt;name&gt;", "[-r|--requires a b] [-n|--note text] [--notx] [--irreversible]", "Scaffold changes/&lt;name&gt;/ and append it to the plan."),
        ("tag &lt;name&gt;", "", "Tag the last change in the plan (use as @name)."),
        ("plan check", "", "Validate plan, folders, lint and variable definitions. No database."),
        ("plan show", "", "Changes with script hashes and chain ids; the plan head. No database."),
        ("status", "[--exit-code]", "Plan versus database: pending, applied, edited, orphans, certified. --exit-code returns 3 when not up to date."),
        ("deploy", "[--to change|@tag] [--dry-run] [--script file] [--ordering dependency|strict] [-y]", "Provision environment resources, deploy pending changes, apply changed repeatables, record a schema fingerprint. --script writes a reviewable SQL file instead."),
        ("revert", "--to x | --last n | --all [--dry-run] [-y] [--allow-variable-drift]", "Revert with the scripts stored at deploy time."),
        ("rebase", "--to x [-y] [--allow-variable-drift]", "Revert to x, then deploy to the head."),
        ("verify", "", "Hashes versus working copy, rendered-SQL drift, verify scripts (rolled back), schema drift. Exit 1 on problems."),
        ("log", "[-n|--limit 50]", "Deploy, revert, fail, repeatable and resource events, newest first."),
        ("baseline", "--to x [-y]", "Record changes up to x as applied without running them."),
        ("vars", "", "Every variable for the environment: value, source, description."),
        ("render &lt;name&gt;", "[-k|--kind deploy|revert|verify]", "Print a change or repeatable exactly as it would run."),
        ("env list", "", "Environments and templates."),
        ("env show", "", "The selected environment, merged, secrets masked."),
        ("env status", "", "Environment resources (Always Encrypted keys) versus the database."),
        ("env provision", "[--dry-run] [--allow-destructive] [-y]", "Create or rotate environment resources without deploying changes."),
        ("auth clear", "", "Forget saved Entra sign-ins."),
        ("doctor", "", "Configuration, Entra token, connection, server facts, journal, resources."),
    ]
    table = "<table><tr><th>Command</th><th>Options</th><th>Does</th></tr>" + "".join(
        f"<tr><td><code>badger {c}</code></td><td>{f'<code>{o}</code>' if o else ''}</td><td>{d}</td></tr>" for c, o, d in cmds) + "</table>"
    return doc("docs/commands/", "Command reference", f"Every badger command and option (version {VERSION}).", f"""
{table}

<h2>Global options</h2>
<table>
<tr><th>Option</th><th>Meaning</th></tr>
<tr><td><code>-C, --project &lt;dir&gt;</code></td><td>Project folder (default: current folder)</td></tr>
<tr><td><code>-e, --env &lt;name&gt;</code></td><td>Environment (default: <code>BADGER_ENV</code>, then <code>defaultEnvironment</code>, then the only one)</td></tr>
<tr><td><code>--var name=value</code></td><td>Script variable; repeatable; overrides badger.yaml</td></tr>
<tr><td><code>--connection-string "..."</code></td><td>Connect exactly here for this run</td></tr>
<tr><td><code>--claim-environment</code></td><td>Accept a database recorded under another environment and re-record it</td></tr>
<tr><td><code>--json</code></td><td>Machine-readable output on stdout (warnings go to stderr)</td></tr>
<tr><td><code>-v, --verbose</code></td><td>Server messages (PRINT), fingerprints, tunnel details</td></tr>
<tr><td><code>--no-color</code></td><td>No ANSI colors; <code>NO_COLOR</code> and redirected output also turn them off</td></tr>
<tr><td><code>--version</code>, <code>-h</code></td><td>Version; help for any command</td></tr>
</table>

<h2>Exit codes</h2>
<table>
<tr><th>Code</th><th>Meaning</th></tr>
<tr><td>0</td><td>Success</td></tr>
<tr><td>1</td><td>Failure: a script error, a problem found by <code>plan check</code> or <code>verify</code>, a refused action, bad configuration, or a command-line usage error</td></tr>
<tr><td>3</td><td><code>status --exit-code</code>: not up to date</td></tr>
</table>

<h2>Environment variables</h2>
<table>
<tr><th>Variable</th><th>Meaning</th></tr>
<tr><td><code>BADGER_ENV</code></td><td>Default environment</td></tr>
<tr><td><code>BADGER_AUTHOR</code></td><td>Author written by <code>badger add</code> (default: OS user)</td></tr>
<tr><td><code>BADGER_HOME</code></td><td>Where saved sign-ins live (default <code>~/.badger</code>)</td></tr>
<tr><td><code>NO_COLOR</code></td><td>Disable colors</td></tr>
</table>

<h2>Confirmation</h2>
<p>Revert, rebase, baseline and destructive provisioning ask for confirmation. In a protected environment, so does deploy, and you must type the environment name. Non-interactive runs (redirected input, <code>--json</code>) fail instead of hanging unless you pass <code>--yes</code>.</p>

<h2>JSON output</h2>
{sh('''badger status -e prod --json | jq '.certified, .databaseHead'
badger log --json -n 20''')}
""")


def ci_cd():
    return doc("docs/ci-cd/", "CI/CD", "Checks on every pull request, gated deploys, no stored secrets.", f"""
<h2>On every pull request</h2>
{sh('''badger plan check -C db''')}
<p>Needs no database. Fails on plan problems, missing folders or reverts, lint errors, and variables missing in any environment.</p>

<h2>On merge and release</h2>
{sh('''badger deploy -C db -e test --json
badger deploy -C db -e prod --yes --json     # --yes: protected environment, no prompt in CI
badger verify -C db -e prod''')}
<p>Two pipelines cannot interleave: the second waits for the deploy lock. Use <code>--script review.sql</code> to attach the exact SQL to an approval step, then <code>deploy</code> after approval.</p>

<h2>Identity without secrets</h2>
<table>
<tr><th>Runner</th><th>Use</th></tr>
<tr><td>GitHub Actions</td><td><code>azure/login</code> with OIDC, then <code>entraAzureCli</code>; or <code>entraWorkloadIdentity</code></td></tr>
<tr><td>Azure Pipelines</td><td><code>entraAzurePipelines</code> with a workload-identity service connection, or <code>entraAzureCli</code> inside an AzureCLI task</td></tr>
<tr><td>Azure-hosted agents, VMs, Container Apps</td><td><code>entraManagedIdentity</code></td></tr>
<tr><td>Anything else</td><td><code>entraServicePrincipalCertificate</code> or <code>entraEnvironment</code> with secrets from your secret store</td></tr>
</table>
<p>Complete GitHub Actions and Azure Pipelines files are on the {link("examples/#github-actions", "Examples")} page.</p>

<h2>Pin the tool version</h2>
{sh(f'''dotnet tool install --global Badger.SqlMigrate --version {VERSION}''')}

<h2>Containers</h2>
{code(f'''FROM mcr.microsoft.com/dotnet/runtime-deps:10.0
ADD https://lonebadgerdownloads.blob.core.windows.net/badger-sql-migrate/v{VERSION}/badger-sql-migrate-{VERSION}-linux-x64.tar.gz /tmp/badger.tgz
RUN tar -xzf /tmp/badger.tgz -C /usr/local/bin badger && rm /tmp/badger.tgz
COPY db /db
ENTRYPOINT ["badger", "-C", "/db"]''', "", "Dockerfile")}

<h2>Applications that migrate themselves</h2>
<p>The engine is also a .NET library; see {link("docs/extending/", "Library and providers")}.</p>
""")


def baseline():
    return doc("docs/baseline/", "Existing databases", "Adopting a database that is already in production.", f"""
<p>When a database already has its schema, you do not want to run your initial changes against it. Baseline records them as applied without executing them.</p>
<ol>
  <li>Script the current schema as your first change(s), e.g. with SSMS, SqlPackage or Badger SQL. Keep them as the real history for new environments.</li>
  <li>Tag that point: <code>badger tag baseline</code>.</li>
  <li>For each existing database: <code>badger baseline --to @baseline -e prod</code>.</li>
  <li>From here on, add changes normally. New, empty databases run the full plan, including the initial changes.</li>
</ol>
{sh('''badger add initial-schema --irreversible     # paste the scripted schema into deploy.sql
badger tag baseline
badger baseline --to @baseline -e prod
badger status -e prod                          # initial-schema shown as (baseline)''')}
<p>Baseline only works on a database with no deployed changes, takes the deploy lock, records the environment, and writes a schema fingerprint so later drift is detected.</p>

<h2>Coming from Flyway, DbUp or grate</h2>
<ul>
  <li>Convert your versioned scripts into changes (one folder each) in their existing order, or collapse history into one <code>initial-schema</code> change.</li>
  <li>Write revert scripts for the changes you might want to undo; mark the rest <code>!irreversible</code>.</li>
  <li>Move repeatable/"anytime" scripts into <code>repeatable/</code>.</li>
  <li>Replace placeholders such as <code>${{name}}</code> (Flyway) with <code>{{name}}</code> or keep <code>$(name)</code>.</li>
  <li>Baseline each existing database. The old tool's history table can stay; badger uses its own <code>badger</code> schema.</li>
</ul>
""")


def journal():
    return doc("docs/journal/", "Journal tables", "What badger stores in each database (schema badger, configurable).", f"""
<table>
<tr><th>Table</th><th>Contents</th></tr>
<tr><td><code>badger.changes</code></td><td>One row per applied change: sequence, name, script hash, chain id, deploy/revert/verify scripts, flags, deployed at/by/host, tool version, duration, out-of-order and baseline flags, rendered hashes</td></tr>
<tr><td><code>badger.repeatables</code></td><td>Current and previous script and hash of each repeatable, applied at/by</td></tr>
<tr><td><code>badger.events</code></td><td>Append-only log: deploy, revert, fail (with the error), repeatable, baseline, resource; actor, host, tool version, environment</td></tr>
<tr><td><code>badger.schema_snapshots</code></td><td>Schema fingerprint and canonical dump after each run, with the head chain id</td></tr>
<tr><td><code>badger.meta</code></td><td><code>state_version</code> (journal layout) and <code>environment</code> (owner)</td></tr>
</table>
{sql('''-- what is deployed, in order
SELECT sequence, name, deployed_at, deployed_by, applied_out_of_order FROM badger.changes ORDER BY sequence;

-- recent failures
SELECT TOP (20) at, name, message, actor, host FROM badger.events WHERE kind = 'fail' ORDER BY id DESC;''')}
<p>Badger creates the schema and tables on the first deploy and upgrades them itself; a newer journal than the tool understands stops the tool with a message to upgrade. Change the schema name with <code>stateSchema</code>. Do not edit these tables by hand: they are the certificate.</p>
""")


def troubleshooting():
    items = [
        ("“These changes were edited after they were deployed”", "A deployed change's scripts changed (even whitespace). Restore the deployed version (<code>git log changes/&lt;name&gt;</code>) and add a new change for the new behaviour."),
        ("“This database belongs to environment 'test'…”", "You are pointing one environment's settings at another environment's database. Check <code>--env</code> and the host/database. If the database was copied on purpose, pass <code>--claim-environment</code> once."),
        ("“Timed out … waiting for the deploy lock”", "Another deploy is running against the database. Wait, or raise <code>lockTimeoutSeconds</code>."),
        ("“… cannot run inside a transaction”", "Lint BSM001. Mark the change <code>!notx</code> in the plan and make the script re-runnable."),
        ("“unknown variable {x}”", "Define it under <code>variables:</code>, pass <code>--var x=…</code>, write <code>{{x}}</code> for a literal, or set <code>preprocessor.unknown: keep</code>."),
        ("“renders differently than when it was deployed”", "A variable used by the change changed value. Run <code>badger vars</code>; restore the value, or use <code>--allow-variable-drift</code> if the change is intended."),
        ("“Changes … come before changes already deployed” (strict ordering)", "A change merged above changes already in this database. In dev, use <code>--ordering dependency</code> or <code>rebase</code>; production should never see this if it only receives released plans."),
        ("“schema drift”", "Something changed the schema outside badger. Find it (compare with a fresh database built from the plan), then either revert it or capture it as a new change."),
        ("Entra: “credential unavailable” or browser does not open", "On SSH or headless machines use <code>entraDeviceCode</code>. For CLI credentials, sign in with <code>az login</code> first. <code>badger auth clear</code> resets saved sign-ins."),
        ("Linux: token cache error", "Install libsecret (and a keyring), use <code>entra.tokenCache: memory</code>, or set <code>allowUnencryptedTokenCache: true</code>."),
        ("Kerberos on macOS/Linux fails", "Run <code>kinit user@REALM</code>, check <code>klist</code>, and set <code>connectionProperties: { Server SPN: MSSQLSvc/host:1433 }</code> if the SPN differs."),
        ("“column master key … cannot be changed”", "The database's CMK points to a different key path. Add a new CMK name and rotate; badger never rewrites CMK metadata."),
        ("macOS: “badger cannot be opened”", "Run <code>xattr -d com.apple.quarantine badger</code> once, or install the .NET tool."),
    ]
    rows = "".join(f"<h3>{q}</h3><p>{a}</p>" for q, a in items)
    return doc("docs/troubleshooting/", "Troubleshooting", "Common messages and what to do about them.", f"""
<p>Start with:</p>
{sh('''badger doctor -e <environment> -v''')}
<p>It checks configuration, Entra sign-in, the connection, server facts, the journal and environment resources, and reports each separately.</p>
{rows}
<h2>Support</h2>
<p>Email <a href="mailto:support@lonebadger.tech">support@lonebadger.tech</a> or open an issue on <a href="https://github.com/lonebadger-tech/badger-sql-migrate/issues">GitHub</a>. Include <code>badger --version</code>, the command, and the output of <code>badger doctor</code>. Never send passwords or tokens.</p>
""")


def extending():
    return doc("docs/extending/", "Library and providers", "Use the engine from .NET code, or add another database.", f"""
<h2>Use the engine in an application</h2>
<p>The CLI is a thin layer over a .NET library. An application can migrate its own database at startup with the same plan, hashes and journal:</p>
{code('''using Badger.SqlMigrate.Core.Configuration;
using Badger.SqlMigrate.Core.Engine;
using Badger.SqlMigrate.Core.Project;
using Badger.SqlMigrate.Core.Providers;
using Badger.SqlMigrate.Providers.SqlServer;

var provider = new SqlServerProvider();
var environment = new DeploymentEnvironment
{
    Name = "prod",
    ConnectionString = configuration.GetConnectionString("App"),
    Ordering = OrderingPolicy.Strict,
};
var project = MigrationProject.Load(Path.Combine(AppContext.BaseDirectory, "db"), provider.Id, environment.Name);

await using var session = await provider.OpenSessionAsync(environment, SessionContext.Empty, cancellationToken);
var engine = new MigrationEngine(project, provider, environment, session, Actor.Current("my-app"));
var result = await engine.DeployAsync(new DeployOptions(), cancellationToken);''', "", "C#")}
<p>Pass a <code>SessionContext</code> with an <code>IAccessTokenProvider</code> for Entra ID (the CLI uses <code>EntraTokenProvider</code> from <code>Badger.SqlMigrate.Connectivity</code>). Register custom Always Encrypted key stores with <code>SqlServerProvider.RegisterKeyStoreProvider</code>.</p>

<h2>Architecture</h2>
{code('''Badger.SqlMigrate.Cli  ──►  Badger.SqlMigrate.Core  ◄──  Badger.SqlMigrate.Providers.SqlServer
                                    ▲
Badger.SqlMigrate.Connectivity ─────┘   Entra ID tokens, SSH tunnels, secrets (vendor-neutral)''')}
<p>The core holds the plan, hashing, the preprocessor and the engine, and references no database driver; a test fails the build if the engine, connectivity or CLI reference a vendor driver directly.</p>

<h2>Adding a database</h2>
<p>A provider implements <code>IDatabaseProvider</code>: sessions, batch splitting, linting, journal tables, a deploy lock, a schema fingerprint, and optionally environment resources. It must pass the shared conformance test suite. The engine, plan, certification, variables, Entra ID tokens and SSH tunnels work unchanged. <a href="https://github.com/lonebadger-tech/badger-sql-migrate/blob/dev/docs/writing-a-provider.md">Writing a provider</a> walks through it, and <a href="https://github.com/lonebadger-tech/badger-sql-migrate/blob/dev/CONTRIBUTING.md">CONTRIBUTING.md</a> covers sending it upstream. PostgreSQL, MySQL and SQLite are natural next providers.</p>

<h2>Source</h2>
<p><a href="https://github.com/lonebadger-tech/badger-sql-migrate">github.com/lonebadger-tech/badger-sql-migrate</a> · <a href="{BASE}/license/">MIT license</a>. Contributions welcome.</p>
""")
