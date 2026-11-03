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
        ("Generating changes", "docs/generating/"),
        ("Hash certification", "docs/certification/"),
        ("Revert (down)", "docs/revert/"),
        ("Fixing deployed changes", "docs/fixing-changes/"),
        ("Teams and ordering", "docs/teamwork/"),
        ("Repeatables", "docs/repeatables/"),
        ("Temporal tables and partitions", "docs/temporal-partitions/"),
    ]),
    ("Configure", [
        ("Environments", "docs/environments/"),
        ("Authentication", "docs/authentication/"),
        ("Connection settings", "docs/connections/"),
        ("Variables", "docs/variables/"),
        ("Always Encrypted", "docs/always-encrypted/"),
        ("PostgreSQL", "docs/postgresql/"),
    ]),
    ("Operate", [
        ("Command reference", "docs/commands/"),
        ("CI/CD", "docs/ci-cd/"),
        ("Bundles", "docs/bundles/"),
        ("Schema drift", "docs/drift/"),
        ("Existing databases", "docs/baseline/"),
        ("Moving from other tools", "docs/importing/"),
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
    return [overview(), install(), quick_start(), project(), plan(), changes(), certification(), revert(), fixing_changes(), generating(),
            teamwork(), repeatables(), temporal_partitions(), environments(), authentication(), connections(), variables(), always_encrypted(), postgresql(),
            commands(), ci_cd(), bundles(), drift(), baseline(), importing(), journal(), troubleshooting(), extending()]


# ---------------------------------------------------------------- get started

def overview():
    return doc("docs/", "Documentation", "Everything about Badger SQL Migrate, from the first change to production pipelines.", f"""
<p>Badger SQL Migrate deploys hand-written SQL changes to SQL Server, Azure SQL and PostgreSQL, in an order you control, and proves afterwards that every database received exactly what you intended. It is a single command-line tool, <code>bsm</code>, for Windows, macOS and Linux.</p>

<h2>Five ideas</h2>
<ol>
  <li><strong>Changes have names.</strong> A change is a folder (<code>changes/add-customer-email/</code>) with a <code>deploy.sql</code>, a <code>revert.sql</code> and an optional <code>verify.sql</code>. No version numbers to collide.</li>
  <li><strong>The plan decides the order.</strong> <code>bsm.plan</code> lists changes one per line. Git merges it without conflicts. {link("docs/plan/", "The plan")}</li>
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
bsm --version''')}
<p>Requires the .NET 10 SDK. Pin a version per repository with a local tool manifest (<code>dotnet new tool-manifest</code>, then <code>dotnet tool install Badger.SqlMigrate</code>) and run it as <code>dotnet bsm</code>.</p>

<h2>Standalone executable</h2>
<p>{link("download/", "Download")} the archive for your platform and put <code>bsm</code> on your <code>PATH</code>. The executables are self-contained: no .NET runtime needed. Builds exist for Windows (x64, Arm64), macOS (Apple silicon, Intel) and Linux (x64, Arm64, Alpine x64), plus a portable build for any OS with the .NET 10 runtime.</p>

<h2>Check it works</h2>
{sh('''bsm --help
bsm doctor -C path/to/project -e dev     # configuration, sign-in, connection, journal''')}

<h2>Platform notes</h2>
<ul>
  <li><strong>macOS:</strong> binaries are signed ad hoc but not notarized; clear the quarantine flag once with <code>xattr -d com.apple.quarantine bsm</code>.</li>
  <li><strong>Linux:</strong> Kerberos (integrated) authentication needs the system GSSAPI library. Entra interactive sign-in caches tokens with libsecret; without it, use device code or set <code>entra.allowUnencryptedTokenCache</code>.</li>
  <li><strong>Windows:</strong> Windows authentication uses SSPI; the Windows certificate store, CNG and CSP key stores for Always Encrypted are available.</li>
</ul>
""")


def quick_start():
    return doc("docs/quick-start/", "Quick start", "From an empty folder to a certified database in ten minutes.", f"""
<p>You need a SQL Server you can reach. A local container works:</p>
{sh('''docker run -d --name sql -e ACCEPT_EULA=Y -e MSSQL_SA_PASSWORD="Your_str0ng_password" -p 1433:1433 mcr.microsoft.com/mssql/server:2022-latest
export BSM_LOCAL_PASSWORD="Your_str0ng_password"''')}

<h2>1. Create a project</h2>
{sh('''bsm init -C db
cd db''')}
<p>This writes <code>bsm.yaml</code>, an empty <code>bsm.plan</code>, the <code>changes/</code>, <code>repeatable/</code> and <code>environments/</code> folders, and a <code>.gitattributes</code> rule that lets git merge the plan. Edit the <code>local</code> environment in <code>bsm.yaml</code> so <code>database:</code> names a database that exists.</p>

<h2>2. Add a change</h2>
{sh('''bsm add create-customer''')}
{sql('''CREATE TABLE dbo.Customer (
    Id int IDENTITY NOT NULL CONSTRAINT PK_Customer PRIMARY KEY,
    Name nvarchar(200) NOT NULL
);''', "changes/create-customer/deploy.sql")}
{sql('''DROP TABLE dbo.Customer;''', "changes/create-customer/revert.sql")}
{sql('''IF OBJECT_ID(N'dbo.Customer', N'U') IS NULL THROW 50000, N'Customer missing', 1;''', "changes/create-customer/verify.sql")}

<h2>3. Check, then deploy</h2>
{sh('''bsm plan check          # no database needed: plan, folders, lint, variables
bsm doctor              # can we sign in and connect?
bsm deploy --dry-run
bsm deploy''')}

<h2>4. Look at the result</h2>
{sh('''bsm status''')}
{code('''environment    local
target         sqlserver://localhost:1433/App
  + create-customer                          2026-11-03 18:03Z you

database head  0e21ef9c72f3
plan head      0e21ef9c72f3
up to date and certified: database history is identical to the plan.''')}

<h2>5. Change your mind</h2>
{sh('''bsm add add-customer-email --requires create-customer
# write deploy.sql / revert.sql
bsm deploy
bsm revert --last 1        # runs the revert.sql stored at deploy time
bsm deploy                 # and forward again
bsm log                    # who did what, when, from where''')}

<h2>Next</h2>
<ul>
  <li>Add a test and a production environment: {link("docs/environments/", "Environments")}.</li>
  <li>Sign in with Entra ID: {link("docs/authentication/", "Authentication")}.</li>
  <li>Commit <code>db/</code> to git and add <code>bsm plan check</code> to CI: {link("docs/ci-cd/", "CI/CD")}.</li>
</ul>
""")


def project():
    return doc("docs/project/", "Project layout", "Files bsm reads, and what each is for.", f"""
{code('''db/
  bsm.yaml                   provider, environments, variables, preprocessor settings
  bsm.plan                   ordered list of changes and tags
  .gitattributes                bsm.plan merge=union, *.sql text eol=lf
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

<h2>Rules bsm checks</h2>
<ul>
  <li>Every change in the plan has a folder with <code>deploy.sql</code>, and every folder in <code>changes/</code> is in the plan.</li>
  <li>Change names use letters, digits, <code>-</code>, <code>_</code> and <code>.</code>, starting with a letter or digit. Names are case-sensitive.</li>
  <li>A missing <code>revert.sql</code> is an error unless the plan marks the change <code>!irreversible</code>.</li>
  <li>Scripts are read as UTF-8. A byte-order mark and Windows line endings are ignored for hashing.</li>
</ul>
<p><code>bsm plan check</code> reports all of these without a database. Put it in a pre-commit hook and in CI.</p>
""")


# ---------------------------------------------------------------- concepts

def plan():
    return doc("docs/plan/", "The plan", "bsm.plan is the single source of deploy order.", f"""
{code('''# comments start with #
create-customer                                 @2026-11-03T14:10Z brock  # a note
add-customer-email [create-customer]            @2026-11-04T09:02Z alice
rebuild-fulltext [create-product] !notx         @2026-11-04T09:15Z bob
drop-legacy !irreversible                       @2026-11-05T08:00Z alice
@release-1.4''', "", "bsm.plan")}

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
<p><code>bsm add</code> and <code>bsm tag</code> write these lines for you. The name, dependencies and flags are part of the change's hash; notes, authors and timestamps are not.</p>

<h2>Why one change per line</h2>
<p>When two developers each append a change on their branches, git's <code>union</code> merge driver keeps both lines instead of reporting a conflict. <code>bsm init</code> adds the rule to <code>.gitattributes</code>:</p>
{code('''bsm.plan merge=union''', "", ".gitattributes")}
<p>Run <code>bsm plan check</code> after merging. It catches the cases a union merge cannot judge: the same name added twice, a dependency on a change that was removed, or a folder without a plan line. See {link("docs/teamwork/", "Teams and ordering")}.</p>

<h2>Targets</h2>
<p><code>--to</code> accepts a change name or a tag: <code>bsm deploy --to @release-1.4</code> deploys up to and including the tagged change.</p>
""")


def changes():
    return doc("docs/changes/", "Changes and scripts", "deploy.sql, revert.sql, verify.sql, batches, transactions and the linter.", f"""
<h2>The three scripts</h2>
<table>
<tr><th>Script</th><th>Runs</th><th>Purpose</th></tr>
<tr><td><code>deploy.sql</code></td><td><code>bsm deploy</code></td><td>Make the change.</td></tr>
<tr><td><code>revert.sql</code></td><td><code>bsm revert</code>, <code>rebase</code></td><td>Undo exactly what deploy did.</td></tr>
<tr><td><code>verify.sql</code></td><td>After deploy, and in <code>bsm verify</code></td><td>Raise an error if the change is not in effect.</td></tr>
</table>

<h2>Batches</h2>
<p>Scripts are split into batches on lines that contain only <code>GO</code> (optionally <code>GO 5</code> to repeat, and a trailing <code>-- comment</code>), the way SSMS and sqlcmd do. <code>GO</code> inside comments (including nested <code>/* */</code>), strings and bracketed names is ignored.</p>

<h2>Transactions</h2>
<p>By default a change runs in one transaction: every batch of <code>deploy.sql</code>, then <code>verify.sql</code>, then the journal row. Any error rolls back all of it and records a <code>fail</code> event; the database is as it was.</p>
<p>Some statements cannot run in a transaction. Mark those changes <code>!notx</code>: batches then run one after another without a transaction, and the journal row is written only after the last succeeds. Write such scripts so they can be re-run (<code>IF NOT EXISTS</code>), because a failure can leave partial work.</p>

<h2>Linter</h2>
<p>On SQL Server, scripts are parsed with Microsoft's T-SQL parser before anything runs ({link("docs/postgresql/#linter", "PostgreSQL's checks")} are on its own page). <code>plan check</code> and <code>deploy</code> report:</p>
<table>
<tr><th>Code</th><th>Severity</th><th>Finding</th></tr>
<tr><td><code>BSM001</code></td><td>Error</td><td>Statement cannot run in a transaction (ALTER/CREATE/DROP DATABASE, BACKUP, RESTORE, full-text catalog and index DDL, RECONFIGURE, resumable online index) and the change is not <code>!notx</code>.</td></tr>
<tr><td><code>BSM002</code></td><td>Error</td><td><code>USE</code>: bsm deploys to the environment's database; switching databases inside a script breaks that.</td></tr>
<tr><td><code>BSM003</code></td><td>Warning</td><td>Explicit <code>BEGIN TRANSACTION</code> inside a transactional change. A <code>ROLLBACK</code> there would abort bsm's transaction.</td></tr>
<tr><td><code>BSM004</code></td><td>Warning</td><td><code>SYSTEM_VERSIONING = OFF</code> for a table that the same script neither turns back on nor drops, so it would stop recording history.</td></tr>
<tr><td><code>BSM000</code></td><td>Warning</td><td>The script did not parse; lint skipped (SQL Server may still accept it).</td></tr>
</table>

<h2>Immutability</h2>
<p>Once a change is deployed anywhere, treat its scripts as frozen. If its hash in your working copy differs from the hash a database recorded, <code>status</code> marks it <em>edited after deploy</em> and <code>deploy</code> refuses to continue. To change behaviour, add a new change. Whitespace counts; only BOMs and line endings are normalized.</p>

<h2>Output</h2>
<p>Messages from <code>PRINT</code> and low-severity <code>RAISERROR</code> appear with <code>-v</code>.</p>
""")


def certification():
    return doc("docs/certification/", "Hash certification", "How bsm proves two databases got identical changes in the same order.", f"""
<h2>Script hash</h2>
<p>Each change's script hash is SHA-256 over its name, dependencies, flags, and the text of deploy, revert and verify, each part length-prefixed so text cannot move between scripts without changing the hash. Before hashing, a UTF-8 byte-order mark is removed and CRLF/CR become LF: a Windows checkout and a macOS checkout of the same commit hash identically. Every other difference, including whitespace, changes the hash.</p>

<h2>Chain id</h2>
<p>Each change also gets a chain id: SHA-256 of the provider, the change name, its script hash and the chain id of the change before it. Like a git commit id, it depends on everything earlier. The chain id of the last change is the <strong>plan head</strong> (<code>bsm plan show</code>).</p>
<p>When a database deploys a change, it records a chain id computed from <em>its own</em> deploy order. If the database received a prefix of the plan, in plan order, with identical scripts, its head equals the plan's chain id at that position, and <code>bsm status</code> reports it as <strong>certified</strong>. Two databases with the same head received byte-identical history.</p>

<h2>Rendered hash</h2>
<p>Scripts can contain {link("docs/variables/", "variables")} that differ per environment, so the SQL that runs in test and prod is not the same text. Badger SQL Migrate therefore stores a second hash per change: SHA-256 of deploy and verify as rendered for that environment, plus one for the rendered revert. That separates "same change" (chain id, equal across environments) from "same SQL executed here" (rendered hash). <code>bsm verify</code> reports changes whose variables now render differently; <code>revert</code> refuses to run a revert script that no longer matches.</p>

<h2>Schema fingerprint</h2>
<p>After each run bsm records a fingerprint: a hash of a canonical, sorted description of the schema (tables, columns, types, defaults, indexes, keys, constraints, module definitions, types, sequences, synonyms). System-generated constraint names, which differ from server to server, are masked; the <code>bsm</code> journal schema is excluded. <code>bsm verify</code> compares the live schema with the last fingerprint, so a hotfix made outside bsm shows up as drift.</p>

<h2>What status tells you</h2>
<table>
<tr><th>Status</th><th>Meaning</th></tr>
<tr><td>up to date and certified</td><td>Every planned change applied, in plan order, with identical scripts.</td></tr>
<tr><td>applied, not in plan order</td><td>Same changes, different order (out-of-order deploys on a shared database).</td></tr>
<tr><td>edited after deploy</td><td>The working copy's scripts differ from what this database ran.</td></tr>
<tr><td>deployed but not in plan</td><td>An orphan: removed from the plan, or from another branch. It can still be reverted.</td></tr>
</table>
<p>Releases of bsm itself publish SHA-256 sums for every download, so the tool can be verified the same way.</p>
""")


def revert():
    return doc("docs/revert/", "Revert (down)", "Rolling back with the scripts the database stored at deploy time.", f"""
{sh('''bsm revert --last 1               # the most recently deployed change
bsm revert --to create-customer    # everything deployed after it (it stays)
bsm revert --to @release-1.4
bsm revert --all
bsm revert --to x --dry-run        # list, change nothing
bsm rebase --to x                  # revert to x, then deploy to the head again
bsm revert --repeatable views/vCustomer.sql   # put a view back to its previous definition''')}
<p>Changes are reverted in reverse deploy order, each in its own transaction (unless <code>!notx</code>), with a <code>revert</code> event in the log. Revert asks for confirmation; pass <code>--yes</code> in scripts.</p>

<h2>Stored scripts</h2>
<p>When a change is deployed, its deploy, revert and verify scripts are saved in the journal. Revert runs the <em>saved</em> revert script. You can switch branches, delete the change, or roll back from a machine that never had the code, and the database still knows how to undo itself. If your working copy's revert script differs from the saved one, bsm says so and uses the saved one.</p>

<h2>Guards</h2>
<ul>
  <li><code>!irreversible</code> changes stop the revert before anything runs.</li>
  <li>Environments with <code>allowRevert: false</code> refuse revert and rebase. Use it for production and fix forward.</li>
  <li>If a variable used by the revert script changed since deploy, revert refuses (it would undo something other than what was done). Check <code>bsm vars</code>; override with <code>--allow-variable-drift</code>.</li>
  <li>Protected environments ask you to type the environment name.</li>
</ul>

<p>A revert script with a bug can be fixed after deploy without breaking certification; see {link("docs/fixing-changes/", "Fixing deployed changes")}. Repeatables are reverted one by one; see {link("docs/repeatables/#revert", "Repeatables")}.</p>

<h2>Writing good revert scripts</h2>
<ul>
  <li>Undo in reverse order of the deploy.</li>
  <li>Drop what you created; restore what you changed. For data changes, keep enough information in the deploy (for example a backup table) to restore it.</li>
  <li>Test the pair: <code>bsm rebase --to &lt;previous change&gt;</code> on a dev database runs revert and deploy back to back. <code>bsm verify</code> afterwards should match the original fingerprint.</li>
</ul>
""")


def fixing_changes():
    return doc("docs/fixing-changes/", "Fixing deployed changes", "Rework a change that needs to do something new; repair one whose scripts were edited harmlessly.", f"""
<p>A deployed change is immutable: if its scripts change, even by one character, its hash no longer matches the database and deploy stops. That protects every database from running something other than what the others ran. There are two ways forward, depending on what the edit is.</p>

<table>
<tr><th>The edit</th><th>Use</th><th>Runs SQL?</th></tr>
<tr><td>The change must do something different (a new procedure body, another column)</td><td><code>bsm rework</code>: a new change that supersedes it</td><td>Yes, as a normal deploy</td></tr>
<tr><td>A bug in <code>revert.sql</code> or <code>verify.sql</code>, a comment, formatting, a new <code>!notx</code> flag</td><td><code>bsm repair</code>: accept the edit in the journal</td><td>No</td></tr>
</table>

<h2 id="rework">Rework</h2>
{sh('''bsm rework get-customer            # adds get-customer-v2
bsm rework get-customer --as get-customer-by-email -n "search by email"''')}
<p>Rework adds a change named <code>&lt;change&gt;-v2</code> (then <code>-v3</code>, and so on) that requires the original:</p>
<ul>
  <li><code>deploy.sql</code> starts as a copy of the original deploy script. Edit it.</li>
  <li><code>revert.sql</code> is the original deploy script, so reverting the rework restores the previous version.</li>
  <li><code>verify.sql</code> is copied when the original has one; <code>!notx</code> carries over.</li>
</ul>
<p>Rework fits definitions that can be run again: <code>CREATE OR ALTER</code> procedures, views and functions kept in changes, permissions, and idempotent data fixes. For a table change, edit both scripts: an <code>ALTER TABLE</code> to deploy and its opposite to revert.</p>
{sql('''-- Revert get-customer-v2: restore what get-customer deployed.
-- Copied from changes/get-customer/deploy.sql; edit it if that script cannot simply be run again.

CREATE OR ALTER PROCEDURE dbo.GetCustomer @Id int AS
SELECT Id, Name FROM dbo.Customer WHERE Id = @Id;''', "changes/get-customer-v2/revert.sql")}
<p>Already edited the original by mistake? Move your edits into the new change and restore the original (<code>git checkout -- changes/get-customer</code>), so it matches what was deployed.</p>

<h2 id="repair">Repair</h2>
{sh('''bsm status -e test                      # ! create-order  EDITED AFTER DEPLOY
bsm repair create-order -e test --reason "revert dropped the wrong index"
bsm repair --all --dry-run -e test     # list every edited change and what differs''')}
<p>Repair records the working copy's scripts, hashes and flags for the named changes and recomputes the chain ids of every change deployed after them, so the database certifies against the plan again. It runs no SQL. Each repaired change gets a <code>repair</code> event in the log with what differed, the old hash and your reason.</p>
<ul>
  <li>Repair is per database: run it in every environment that has the change.</li>
  <li>A fixed <code>revert.sql</code> is what revert will run from then on, because revert uses the scripts stored in the journal.</li>
  <li>When <code>deploy.sql</code> itself differs, bsm warns and always asks for confirmation (type the environment name): databases that already ran the old script keep the old result. Only repair deploy edits that change nothing, and put real changes in a rework.</li>
  <li>Repair takes the deploy lock and respects the environment ownership guard.</li>
</ul>
""")


def generating():
    return doc("docs/generating/", "Generating changes", "Make schema edits in your own database with any tool, then let bsm write the change.", f"""
{sh('''bsm add customer-phone --from-diff                 # compare 'local' with the plan, write the change
bsm add customer-phone --from-diff --dry-run       # print the deploy script, write nothing
bsm add customer-phone --from-diff --shadow-env docker -n "phone numbers"''')}
<p>Change your development database however you like (SSMS, Azure Data Studio, a table designer, a script), then run <code>bsm add &lt;name&gt; --from-diff</code>. bsm:</p>
<ol>
  <li>checks that the database has exactly a prefix of the plan deployed, in order (otherwise the comparison would include unrelated differences);</li>
  <li>creates a temporary <strong>shadow database</strong> on the same server and deploys the plan to it, as far as your database has it, with your environment's variables and keys;</li>
  <li>compares the two catalogs and writes <code>changes/&lt;name&gt;/deploy.sql</code> (shadow → your database) and <code>revert.sql</code> (back again), with a summary and warnings at the top;</li>
  <li>appends the change to <code>bsm.plan</code>, marks it <code>!notx</code> if needed, and records it as applied in your database, which already has it (<code>--no-record</code> to skip);</li>
  <li>drops the shadow database (<code>--keep-shadow</code> to look at it).</li>
</ol>
<p>Everything is read from SQL Server's catalog views with the same driver bsm already uses: nothing else to install, on any OS.</p>
{sql('''-- Deploy customer-phone: generated by Badger SQL Migrate from 'local'. Review before committing.
--   + column dbo.Customer.Phone varchar(30)
--   + index dbo.Customer.IX_Customer_Phone

ALTER TABLE [dbo].[Customer] ADD [Phone] varchar(30) NULL;
GO

CREATE NONCLUSTERED INDEX [IX_Customer_Phone] ON [dbo].[Customer] ([Phone]) WHERE ([Phone] IS NOT NULL);
GO''', "changes/customer-phone/deploy.sql")}

<h2>What is scripted</h2>
<table>
<tr><th>Objects</th><th>Changes</th></tr>
<tr><td>Schemas, tables, columns</td><td>Create and drop; add, drop and alter columns (type, length, nullability, collation, masking); defaults; a column replaced by one with the same definition becomes a rename (<code>sp_rename</code>)</td></tr>
<tr><td>Keys, indexes, constraints</td><td>Primary keys, unique constraints, indexes (filtered, included columns, descending, columnstore, compression, partitioned), foreign keys (actions, NOCHECK), check constraints. Indexes and constraints on a column whose type changes are dropped and re-created around the change.</td></tr>
<tr><td>Views, procedures, functions, triggers</td><td><code>CREATE OR ALTER</code>, in dependency order; dropped in reverse order</td></tr>
<tr><td>Types, sequences, synonyms</td><td>Alias and table types, sequences (increment, limits, cycle, cache), synonyms</td></tr>
<tr><td>System-versioned tables</td><td>New temporal tables with their history table and retention; retention changes</td></tr>
<tr><td>Partitioning</td><td>Partition functions and schemes; boundaries added (<code>SPLIT</code>) and removed (<code>MERGE</code>); tables and indexes on a scheme</td></tr>
</table>
<p>Constraints SQL Server named itself (<code>DF__Order__Total__3B75D760</code>) have a different name on every server, so the script finds them at deploy time instead of hard-coding a name.</p>

<h2>Warnings instead of guesses</h2>
<p>Some edits cannot be scripted safely from two catalogs. They are listed at the top of <code>deploy.sql</code> and in the output, and left for you to write:</p>
<ul>
  <li><strong>Data loss</strong>: dropped tables and columns, narrowed types. The output says <code>WITH DATA LOSS</code>.</li>
  <li>Adding a <code>NOT NULL</code> column without a default to a table with rows; making a column <code>NOT NULL</code> while rows hold NULL.</li>
  <li>Changes that need a table rebuild: identity, sparse, ROWGUIDCOL or encryption settings, column order, moving to another filegroup or partition scheme.</li>
  <li>Turning system versioning on or off; changing a partition function's type or direction; XML, spatial and full-text indexes.</li>
  <li>A rename is only a guess: check that a column scripted with <code>sp_rename</code> really was renamed.</li>
  <li>Values of this environment's variables in the SQL, such as <code>reporting_local</code> for <code>{{schemas.reporting}}</code>: replace them with the variable if they differ per environment.</li>
</ul>

<h2>Repeatables and other tools' tables</h2>
<p>Views, procedures, functions and triggers that a repeatable script defines are maintained in that script, so they are never put in a change: the output tells you to edit the repeatable. The bsm journal, and other tools' journals (<code>flyway_schema_history</code>, <code>SchemaVersions</code>, grate's and RoundhousE's schemas, <code>__EFMigrationsHistory</code>) are left out; add your own with <code>--exclude audit.*</code>.</p>

<h2>The shadow database</h2>
<p>It is created next to your database with <code>CREATE DATABASE</code> and dropped afterwards, so the login needs that right. On Azure SQL Database a new database takes a minute and is billed while it exists: use <code>--shadow-env</code> to build it on another server, such as a local SQL Server container. The shadow uses your environment's variables and Always Encrypted keys, so it matches your database except for your edits.</p>

<h2>Comparing any two databases</h2>
{sh('''bsm diff-schema -e test --against prod                    # what differs
bsm diff-schema -e test --against prod --script sync.sql   # T-SQL that turns test into prod''')}
<p>The same scripter writes a one-off script between two live databases, for example to bring a drifted database back in line. It is not a change: review it, and run it yourself or turn it into one.</p>
""")


def teamwork():
    return doc("docs/teamwork/", "Teams and ordering", "Concurrent development without version collisions, and deploys that cannot interleave.", f"""
<h2>Two developers, same day</h2>
<p>Alice adds <code>add-customer-email</code> and Bob adds <code>add-order-index</code> on separate branches. Each appends a line to <code>bsm.plan</code>. With version numbers they would both have picked <code>V12</code>; with names there is nothing to collide on, and git's union merge keeps both lines. <code>bsm plan check</code> in CI verifies the merged plan.</p>

<h2>Shared databases and out-of-order changes</h2>
<p>Bob's change reached the shared dev database first. After the merge, Alice's line sits above Bob's in the plan, but Bob's change is already deployed. What happens next is the environment's <code>ordering</code>:</p>
<table>
<tr><th><code>ordering</code></th><th>Behaviour</th><th>Use for</th></tr>
<tr><td><code>dependency</code> (default)</td><td>Deploy Alice's change now (its dependencies are satisfied), flagged out of order. The database is up to date but not certified.</td><td>Shared dev and test databases</td></tr>
<tr><td><code>strict</code></td><td>Refuse. The database must receive changes in exact plan order.</td><td>Production</td></tr>
</table>
<p>Production databases only ever see the merged plan, in order, so they certify. Bring a dev database back to plan order with <code>bsm rebase --to &lt;last common change&gt;</code>. Override per run with <code>--ordering strict|dependency</code>.</p>

<h2>Concurrent deploys</h2>
<p>Deploy, revert, baseline and provisioning hold an exclusive lock on the database for the whole run (<code>sp_getapplock</code>, session-owned, works on Azure SQL). A second pipeline waits up to <code>lockTimeoutSeconds</code> (default 300) and then fails with a message naming the lock. Read-only commands (<code>status</code>, <code>verify</code>, <code>log</code>) do not take the lock.</p>

<h2>Recommended workflow</h2>
<ol>
  <li>Each developer works against their own database (<code>local</code> environment).</li>
  <li><code>bsm plan check</code> runs on every pull request.</li>
  <li>Merges deploy to a shared test environment (<code>ordering: dependency</code>).</li>
  <li>Releases deploy to production (<code>ordering: strict</code>, <code>protected: true</code>, <code>allowRevert: false</code>) and run <code>bsm verify</code>.</li>
</ol>
""")


def repeatables():
    return doc("docs/repeatables/", "Repeatables", "Views, procedures, functions and environment-only scripts that re-run when they change.", f"""
<p>Objects that are defined rather than migrated, such as views, stored procedures, functions and triggers, belong in <code>repeatable/</code>. Write them with <code>CREATE OR ALTER</code>. After the changes in a deploy, bsm applies every repeatable whose hash differs from the one it last applied, each in its own transaction.</p>

<h2>Order</h2>
<p>Repeatables run in path order (ordinal), with declared dependencies first:</p>
{sql('''-- bsm:requires views/vActiveCustomer.sql functions/fnFullName.sql
CREATE OR ALTER PROCEDURE dbo.usp_FindCustomer ...''', "repeatable/procedures/usp_FindCustomer.sql")}
<p>Names are paths relative to <code>repeatable/</code>, with forward slashes. Cycles and unknown names are reported by <code>plan check</code>.</p>

<h2>Environment-only scripts</h2>
<p>Scripts under <code>environments/&lt;name&gt;/</code> behave like repeatables but run only in that environment, after the shared ones. They are named <code>@&lt;name&gt;/path</code> and may require shared repeatables. Use them for seed data, test users and environment-specific grants. Write them idempotently (<code>MERGE</code>, <code>IF NOT EXISTS</code>).</p>

<h2>What is recorded</h2>
<p>For each repeatable, the journal keeps the current and previous script and hash, and a <code>repeatable</code> event. Repeatables are not part of the plan certificate: they are re-applied by content, not by position.</p>

<h2 id="revert">Reverting a repeatable</h2>
{sh('''bsm revert --repeatable views/vCustomer.sql -e test
bsm revert --repeatable views/a.sql --repeatable @test/seed.sql --dry-run''')}
<p>Runs the definition the repeatable had before its last apply (stored in the journal) and records it as current, with a <code>revert</code> event. Running it twice swaps back. Use it to back out a bad view or procedure quickly; the working copy still has the newer version, so the next deploy applies it again unless you fix it too. A repeatable applied only once has nothing to go back to: remove the object with a new change instead.</p>

<h2>Changes or repeatables?</h2>
<ul>
  <li>Tables, columns, indexes, data: <strong>changes</strong> (they need explicit migrations and reverts).</li>
  <li>Views, procedures, functions, triggers: <strong>repeatables</strong> (the latest definition wins).</li>
  <li>Something a change depends on during deploy (for example a function used in a computed column): a <strong>change</strong>, because repeatables run after changes.</li>
</ul>
""")


def temporal_partitions():
    return doc("docs/temporal-partitions/", "Temporal tables and partitions", "System-versioned tables and partitioned tables in changes, reverts and fingerprints.", f"""
<p>Both are ordinary schema, so they belong in {link("docs/changes/", "changes")}: deploy creates or alters them, revert undoes it, and both run in the change's transaction. Badger SQL Migrate adds three things: a fingerprint that understands them, a lint rule, and a setting for partitions that a job maintains.</p>

<h2 id="temporal">System-versioned (temporal) tables</h2>
{sql('''CREATE TABLE dbo.Price (
    Id int NOT NULL CONSTRAINT PK_Price PRIMARY KEY,
    Amount money NOT NULL,
    ValidFrom datetime2 GENERATED ALWAYS AS ROW START HIDDEN NOT NULL,
    ValidTo datetime2 GENERATED ALWAYS AS ROW END HIDDEN NOT NULL,
    PERIOD FOR SYSTEM_TIME (ValidFrom, ValidTo))
WITH (SYSTEM_VERSIONING = ON (HISTORY_TABLE = dbo.PriceHistory, HISTORY_RETENTION_PERIOD = 2 YEARS));''', "changes/create-price/deploy.sql")}
{sql('''ALTER TABLE dbo.Price SET (SYSTEM_VERSIONING = OFF);
DROP TABLE dbo.Price, dbo.PriceHistory;''', "changes/create-price/revert.sql")}
<ul>
  <li><strong>Name the history table.</strong> Without <code>HISTORY_TABLE</code>, SQL Server calls it <code>MSSQL_TemporalHistoryFor_&lt;object id&gt;</code>, a different name on every server, and a revert script has to look the name up before switching versioning off.</li>
  <li><strong>Changing a temporal table.</strong> Most schema changes go to the table and its history table together. Adding a nullable column or a column with a default works while versioning is on. For anything else, switch it off, change both tables, and switch it on again in the same script, so the change stays atomic:</li>
</ul>
{sql('''ALTER TABLE dbo.Price SET (SYSTEM_VERSIONING = OFF);
ALTER TABLE dbo.Price ALTER COLUMN Amount decimal(19, 4) NOT NULL;
ALTER TABLE dbo.PriceHistory ALTER COLUMN Amount decimal(19, 4) NOT NULL;
ALTER TABLE dbo.Price SET (SYSTEM_VERSIONING = ON (HISTORY_TABLE = dbo.PriceHistory, DATA_CONSISTENCY_CHECK = ON));''', "changes/price-amount-decimal/deploy.sql")}
<p>Lint rule <code>BSM004</code> warns when a script switches versioning off for a table and neither switches it back on nor drops the table. The fingerprint records each table's period columns, versioning state, history table and retention, and the <code>GENERATED ALWAYS</code> and <code>HIDDEN</code> flags of period columns. A history table SQL Server named itself is recorded under the name of its table, so databases built from the same plan fingerprint the same. Reverting a change also reverts the schema; the rows in the history table go with it, so take a copy first if you need them.</p>

<h2 id="partitions">Partitioned tables</h2>
{sql('''CREATE PARTITION FUNCTION pfOrderDate (date) AS RANGE RIGHT FOR VALUES ('2026-01-01', '2026-04-01', '2026-07-01', '2026-10-01');
CREATE PARTITION SCHEME psOrderDate AS PARTITION pfOrderDate ALL TO ({OrderFilegroup});
CREATE TABLE dbo.[Order] (
    OrderDate date NOT NULL,
    Id bigint NOT NULL,
    CONSTRAINT PK_Order PRIMARY KEY (OrderDate, Id)) ON psOrderDate (OrderDate);''', "changes/create-order/deploy.sql")}
{sql('''DROP TABLE dbo.[Order];
DROP PARTITION SCHEME psOrderDate;
DROP PARTITION FUNCTION pfOrderDate;''', "changes/create-order/revert.sql")}
<p>Filegroups often differ per environment (Azure SQL Database only has <code>PRIMARY</code>), so keep them in a {link("docs/variables/", "variable")}, for example <code>OrderFilegroup: "[PRIMARY]"</code> in Azure and <code>"[FG_Orders]"</code> on-premises. Adding a boundary in a change is a normal deploy, and its revert merges it back:</p>
{sql('''ALTER PARTITION SCHEME psOrderDate NEXT USED {OrderFilegroup};
ALTER PARTITION FUNCTION pfOrderDate() SPLIT RANGE ('2027-01-01');''', "changes/orders-2027/deploy.sql")}
{sql('''ALTER PARTITION FUNCTION pfOrderDate() MERGE RANGE ('2027-01-01');''', "changes/orders-2027/revert.sql")}
<p>Splitting or merging a partition that holds rows moves data and locks the table; split ahead of the data, while the partition is still empty. The fingerprint records each function's type, direction and boundary values, each scheme's filegroups, and the scheme and partitioning column of every partitioned table and index.</p>

<h2 id="sliding-window">Partitions maintained by a job</h2>
<p>With a sliding window, a scheduled job splits new partitions and switches out old ones outside bsm. Every split would show as schema drift. Tell the environment to leave boundaries out of the fingerprint:</p>
{yaml('''environments:
  prod:
    schemaFingerprint:
      partitionBoundaries: ignore   # include (default) | ignore''', "bsm.yaml")}
<p>The fingerprint then records each function's type and direction, and the set of filegroups each scheme uses, so moving a table to another scheme or function is still drift; adding a boundary is not. The setting applies to snapshots taken from then on: deploy once after changing it.</p>

<h2>Upgrading from 0.1</h2>
<p>Version 0.2 records these details for the first time, so a database with temporal or partitioned tables deployed by 0.1 has a fingerprint in the old format. <code>bsm verify</code> warns instead of reporting drift until the next deploy records a new fingerprint. Databases without them fingerprint exactly as before.</p>
""")


# ---------------------------------------------------------------- configure

def environments():
    return doc("docs/environments/", "Environments", "bsm.yaml: dev, test, prod and everything that differs between them.", f"""
{yaml('''provider: sqlserver
defaultEnvironment: dev                # also: BSM_ENV, or --env / -e

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
    ordering: strict''', "bsm.yaml")}

<h2>Inheritance</h2>
<p><code>extends</code> names another environment (or template) to start from. Maps merge key by key, so <code>entra:</code> in a child adds to the template's <code>entra:</code>; values and lists are replaced. Chains are allowed, cycles are errors. <code>bsm env show -e prod</code> prints the merged result with secrets masked; <code>bsm env list</code> shows every environment.</p>

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
<tr><td><code>stateSchema</code></td><td><code>bsm</code></td><td>Schema for the {link("docs/journal/", "journal tables")}</td></tr>
<tr><td><code>commandTimeoutSeconds</code></td><td>driver default (30)</td><td>Per-batch timeout</td></tr>
<tr><td><code>lockTimeoutSeconds</code></td><td><code>300</code></td><td>Wait for another deployer</td></tr>
<tr><td><code>variables</code></td><td></td><td>Environment-specific {link("docs/variables/", "variables")}</td></tr>
<tr><td><code>alwaysEncrypted</code></td><td></td><td>{link("docs/always-encrypted/", "Always Encrypted keys")}</td></tr>
<tr><td><code>schemaFingerprint</code></td><td></td><td><code>partitionBoundaries: ignore</code> for databases whose partitions a job splits and merges ({link("docs/temporal-partitions/#sliding-window", "Partitions")})</td></tr>
</table>
<p>Unknown keys inside known blocks (for example a misspelled <code>entra.tennantId</code>) are errors, so typos never silently fall back to defaults.</p>

<h2>Secrets</h2>
<p>Any value can reference <code>${{env:NAME}}</code> (an environment variable) or <code>${{file:path}}</code> (a file, such as a mounted secret). They are resolved only for the environment being used, so a missing production secret never breaks a dev run. Never commit passwords; <code>bsm.yaml</code> belongs in source control.</p>

<h2>Database ownership</h2>
<p>The first deploy records the environment name in the database. If you later run as a different environment against that database (for example prod settings pointing at a test server after a copy-paste mistake), bsm refuses:</p>
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
      loginHint: you@contoso.com''', "bsm.yaml")}
<p>The first run opens a browser for sign-in, MFA included. The token is kept in the operating system's encrypted store (Keychain on macOS, DPAPI on Windows, libsecret on Linux) and a small sign-in record (account and tenant ids, no tokens) is saved in <code>~/.bsm/auth/</code>, so later runs are silent until the refresh token expires. <code>bsm auth clear</code> forgets saved sign-ins. Set <code>entra.tokenCache: memory</code> to sign in on every run. Without a browser (SSH sessions), use <code>entraDeviceCode</code>.</p>

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
<p>A <code>connectionString</code> with <code>Authentication=Active Directory Interactive</code> (or any other SqlClient Entra mode) also works; bsm registers SqlClient's own Entra provider. Setting <code>authentication:</code> takes precedence and removes conflicting keywords from the string.</p>

<h2>Checking sign-in</h2>
<p><code>bsm doctor</code> gets a token before connecting, so you can tell an identity problem from a network or permission problem:</p>
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
<p>The Entra identity needs a database user (<code>CREATE USER [you@contoso.com] FROM EXTERNAL PROVIDER</code>) with rights to run your changes and to create the <code>bsm</code> schema on first deploy.</p>
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
<p><code>connectionProperties</code> accepts every keyword SqlClient understands and applies them last, over everything else. Invalid keywords or values are reported by <code>bsm doctor</code> and before connecting.</p>
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
<p>Reach a database behind a bastion host. Badger SQL Migrate opens a local port forward and connects through it; TLS is still validated against the real server name.</p>
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
        ("{change.hash}", "", "The change's script hash"), ("{bsm.provider}", "", "Provider id (sqlserver)"),
        ("{bsm.version}", "", "Tool version"), ("{bsm.schema}", "", "Journal schema"), ("{plan.head}", "", "Plan head chain id"),
        ("{deploy.user}", "", "OS user running bsm *"), ("{deploy.host}", "", "Machine running bsm *"),
        ("{deploy.utc}", "", "Run start, ISO 8601 UTC *"), ("{deploy.date}", "", "Run start date, UTC *"), ("{deploy.id}", "", "Unique id of the run *"),
    ]
    table = "<table><tr><th>Variable</th><th>Also</th><th>Value</th></tr>" + "".join(
        f"<tr><td><code>{a}</code></td><td>{f'<code>{b}</code>' if b else ''}</td><td>{c}</td></tr>" for a, b, c in builtins) + "</table>"
    return doc("docs/variables/", "Variables", "Scripts are preprocessed: {env}, {db}, your own variables, and $(sqlcmd) syntax.", f"""
<p>Before a script reaches the database, bsm replaces variables in it:</p>
{sql('''SELECT * FROM [calc_{env}].[table1];       -- test:  SELECT * FROM [calc_test].[table1];
SELECT * FROM [calc_$(env)].[table1];      -- the same, sqlcmd syntax''')}

<h2>Syntax</h2>
<table>
<tr><th>Write</th><th>Gets</th></tr>
<tr><td><code>{{name}}</code>, <code>{{group.name}}</code></td><td>The variable's value</td></tr>
<tr><td><code>{{var.name}}</code></td><td>A user variable, explicitly</td></tr>
<tr><td><code>$(name)</code></td><td>The same variables, sqlcmd style</td></tr>
<tr><td><code>{{{{name}}}}</code></td><td>A literal <code>{{name}}</code></td></tr>
<tr><td><code>-- bsm:preprocess off</code> as the first line</td><td>The script untouched</td></tr>
</table>
<p>Only an identifier directly inside the braces is a variable. ODBC escapes (<code>{{fn NOW()}}</code>, <code>{{d '2026-01-01'}}</code>), JSON (<code>'{{"a":1}}'</code>) and format strings (<code>'{{0}}'</code>) are left alone. Names are case-insensitive.</p>

<h2>Built-in variables</h2>
{table}
<p>* Per-run values: a script that uses them renders differently on every run, so it is not tracked by rendered hash. The names <code>env</code>, <code>db</code>, <code>change</code>, <code>bsm</code>, <code>plan</code> and <code>deploy</code> (and anything under them) are reserved.</p>

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
      Region: ${env:DEPLOY_REGION} # secrets and env vars work here too''', "bsm.yaml")}
<p>Precedence, lowest to highest: shared <code>variables</code>, the environment's <code>variables</code> (including what it inherits through <code>extends</code>), then <code>--var name=value</code> on the command line. Circular references are errors.</p>
<p class="callout"><strong>YAML tip:</strong> in an inline map (<code>{{ a: b }}</code>), braces are YAML syntax, so quote values that contain variables: <code>{{ calc: "calc_{{env}}" }}</code>. In block style (one key per line) no quotes are needed.</p>

<h2>Unknown variables</h2>
<p>An unknown <code>{{name}}</code> or <code>$(name)</code> is an error that names the script and line, so a typo never reaches the database. If your scripts contain many literal braces, set <code>preprocessor.unknown: keep</code> to leave unknown <code>{{name}}</code> tokens as they are (unknown built-ins such as <code>{{env.nmae}}</code> are still errors).</p>
{yaml('''preprocessor:
  unknown: error        # error (default) | keep
  sqlcmdSyntax: true    # expand $(name) too (default)''', "bsm.yaml")}
<p><code>bsm plan check</code> also fails when a script uses a variable that some environment does not define, and names those environments.</p>

<h2>Seeing the result</h2>
{sh('''bsm vars -e prod                        # every variable: value, source, description
bsm render create-calc-schema -e prod   # the exact SQL prod will receive
bsm render create-calc-schema -e prod --kind revert
bsm deploy -e prod --script review.sql  # all pending changes, rendered, for review''')}

<h2>Variables and certification</h2>
<p>Raw scripts are hashed and certified, so the same change certifies identically in every environment. The rendered SQL is hashed separately per environment (see {link("docs/certification/", "Hash certification")}). If a variable's value changes after a change was deployed, <code>verify</code> reports it and <code>revert</code> refuses unless you pass <code>--allow-variable-drift</code>.</p>
""")


def always_encrypted():
    return doc("docs/always-encrypted/", "Always Encrypted", "Column master keys and column encryption keys per environment, provisioned and rotated by bsm.", f"""
<p>Always Encrypted keys are a classic per-environment difference: production's column master key lives in production's Key Vault, test's somewhere else. Change scripts should not contain key paths or encrypted key values. Badger SQL Migrate splits it cleanly:</p>
<ul>
  <li>Scripts reference a <strong>column encryption key name</strong> (the same in every environment).</li>
  <li>Each environment maps that name to <strong>its own column master key</strong> in <code>bsm.yaml</code>.</li>
  <li>Before changes run, bsm <strong>creates</strong> missing keys and <strong>rotates</strong> keys whose master key changed.</li>
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
        CEK_Customer: { columnMasterKey: CMK_Prod_2026 }''', "bsm.yaml")}

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
  <li><code>bsm deploy</code> (or <code>bsm env provision</code>) adds a second value to the CEK: the same data key, decrypted with the old CMK and re-encrypted with the new one. Data does not change; both keys work.</li>
  <li>When every application can reach the new key, run <code>bsm env provision --allow-destructive</code> to drop the value protected by the old CMK. This asks for confirmation.</li>
</ol>
{sh('''bsm env status -e prod''')}
{code('''  = column master key CMK_Prod_2026: AZURE_KEY_VAULT key path matches
  + column master key CMK_Prod_2027: create column master key in AZURE_KEY_VAULT
  + column encryption key CEK_Customer: add value protected by CMK_Prod_2027 (rotation from CMK_Prod_2026)
  - column encryption key CEK_Customer: drop value protected by old column master key CMK_Prod_2026 (rotation complete)''')}

<h2>Conflicts</h2>
<p>SQL Server cannot change a column master key's key path. If the database has <code>CMK_Prod_2026</code> pointing somewhere other than your configuration, bsm stops with a conflict instead of guessing. Define a new CMK name and rotate. Keys that exist in the database but not in your configuration are reported for information and never dropped.</p>

<h2>Fingerprints across environments</h2>
<p>The schema fingerprint records which CEK name protects each encrypted column, never key paths or values, so environments with different keys still produce the same fingerprint for the same schema.</p>
""")


# ---------------------------------------------------------------- operate

def commands():
    cmds = [
        ("init", "[--provider sqlserver|postgresql]", "Create bsm.yaml, bsm.plan, folders and .gitattributes."),
        ("add &lt;name&gt;", "[-r|--requires a b] [-n|--note text] [--notx] [--irreversible]", "Scaffold changes/&lt;name&gt;/ and append it to the plan."),
        ("add &lt;name&gt; --from-diff", "[--shadow-env env] [--keep-shadow] [--exclude schema.name…] [--dry-run] [--no-record] [-r …] [-n …]", "Generate deploy.sql and revert.sql from schema edits made in the environment's database, compared with a shadow database built from the plan; records the change as applied there."),
        ("tag &lt;name&gt;", "", "Tag the last change in the plan (use as @name)."),
        ("plan check", "", "Validate plan, folders, lint and variable definitions. No database."),
        ("plan show", "", "Changes with script hashes and chain ids; the plan head. No database."),
        ("status", "[--exit-code]", "Plan versus database: pending, applied, edited, orphans, certified. --exit-code returns 3 when not up to date."),
        ("deploy", "[--to change|@tag] [--dry-run] [--script file] [--ordering dependency|strict] [-y]", "Provision environment resources, deploy pending changes, apply changed repeatables, record a schema fingerprint. --script writes a reviewable SQL file instead."),
        ("revert", "--to x | --last n | --all | --repeatable name… [--dry-run] [-y] [--allow-variable-drift]", "Revert with the scripts stored at deploy time; --repeatable restores a repeatable's previous definition."),
        ("rebase", "--to x [-y] [--allow-variable-drift]", "Revert to x, then deploy to the head."),
        ("rework &lt;change&gt;", "[--as name] [-n|--note text]", "Add a change that supersedes a deployed one: a copy of its deploy script to edit, with a revert that restores it."),
        ("repair", "[changes…] | --all [--reason text] [--dry-run] [-y]", "Accept edits to deployed changes without running SQL; re-chains and re-certifies the journal."),
        ("bundle create", "[-o file] [--no-config]", "Write a hash-verified bundle of the project. No database."),
        ("bundle verify &lt;file&gt;", "", "Check every file in a bundle against its manifest. No database."),
        ("diff-schema", "[--against env] [--exit-code] [--script file]", "Schema differences: live versus the schema recorded after the last deploy, or versus another environment. --exit-code returns 3 when they differ; --script writes T-SQL that turns this database into the other."),
        ("verify", "", "Hashes versus working copy, rendered-SQL drift, verify scripts (rolled back), schema drift. Exit 1 on problems."),
        ("log", "[-n|--limit 50]", "Deploy, revert, fail, repeatable and resource events, newest first."),
        ("baseline", "--to x | --from flyway|dbup|grate|roundhouse|sqitch [--journal schema.table] [--skip-checksums] [-y]", "Record changes as applied without running them: up to x, or as far as the old tool's journal in the database says (with repeatables it already applied)."),
        ("import flyway &lt;folder&gt;", "[--config file] [--location dir…] [--provider sqlserver|postgresql] [--dry-run]", "Convert a Flyway project into a new bsm project (written to -C). No database."),
        ("import dbup &lt;folder&gt;", "[--file-names] [--run-always glob…] [--no-variables] [--var name=value…] [--dry-run]", "Convert DbUp scripts into a new bsm project (written to -C). No database."),
        ("import grate &lt;folder&gt;", "[--folder key=name…] [--environment name…] [--ignore-directory-names] [--no-tokens] [--var name=value…] [--dry-run]", "Convert a grate or RoundhousE scripts folder into a new bsm project. No database."),
        ("import sqitch &lt;folder&gt;", "[--engine name] [--provider …] [--dry-run]", "Convert a Sqitch project into a new bsm project. No database."),
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
        f"<tr><td><code>bsm {c}</code></td><td>{f'<code>{o}</code>' if o else ''}</td><td>{d}</td></tr>" for c, o, d in cmds) + "</table>"
    return doc("docs/commands/", "Command reference", f"Every bsm command and option (version {VERSION}).", f"""
{table}

<h2>Global options</h2>
<table>
<tr><th>Option</th><th>Meaning</th></tr>
<tr><td><code>-C, --project &lt;dir&gt;</code></td><td>Project folder (default: current folder)</td></tr>
<tr><td><code>-e, --env &lt;name&gt;</code></td><td>Environment (default: <code>BSM_ENV</code>, then <code>defaultEnvironment</code>, then the only one)</td></tr>
<tr><td><code>--var name=value</code></td><td>Script variable; repeatable; overrides bsm.yaml</td></tr>
<tr><td><code>--connection-string "..."</code></td><td>Connect exactly here for this run</td></tr>
<tr><td><code>--bundle &lt;file&gt;</code></td><td>Take the plan and scripts from a verified bundle instead of the project folder (and <code>bsm.yaml</code> too, when bundled)</td></tr>
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
<tr><td>3</td><td><code>status --exit-code</code>: not up to date; <code>diff-schema --exit-code</code>: schemas differ</td></tr>
</table>

<h2>Environment variables</h2>
<table>
<tr><th>Variable</th><th>Meaning</th></tr>
<tr><td><code>BSM_ENV</code></td><td>Default environment</td></tr>
<tr><td><code>BSM_AUTHOR</code></td><td>Author written by <code>bsm add</code> (default: OS user)</td></tr>
<tr><td><code>BSM_HOME</code></td><td>Where saved sign-ins live (default <code>~/.bsm</code>)</td></tr>
<tr><td><code>NO_COLOR</code></td><td>Disable colors</td></tr>
</table>

<h2>Confirmation</h2>
<p>Revert, rebase, repair, baseline and destructive provisioning ask for confirmation. In a protected environment, so does deploy, and you must type the environment name. Non-interactive runs (redirected input, <code>--json</code>) fail instead of hanging unless you pass <code>--yes</code>.</p>

<h2>JSON output</h2>
{sh('''bsm status -e prod --json | jq '.certified, .databaseHead'
bsm log --json -n 20''')}
""")


def ci_cd():
    return doc("docs/ci-cd/", "CI/CD", "Checks on every pull request, gated deploys, no stored secrets.", f"""
<h2>On every pull request</h2>
{sh('''bsm plan check -C db''')}
<p>Needs no database. Fails on plan problems, missing folders or reverts, lint errors, and variables missing in any environment.</p>

<h2>On merge and release</h2>
{sh('''bsm deploy -C db -e test --json
bsm deploy -C db -e prod --yes --json     # --yes: protected environment, no prompt in CI
bsm verify -C db -e prod''')}
<p>Two pipelines cannot interleave: the second waits for the deploy lock. Use <code>--script review.sql</code> to attach the exact SQL to an approval step, then <code>deploy</code> after approval.</p>

<h2>Build once, deploy the same bundle everywhere</h2>
{sh('''bsm bundle create -C db -o db-bundle.zip          # build stage: publish as an artifact
bsm --bundle db-bundle.zip deploy -e test --json   # each release stage
bsm --bundle db-bundle.zip deploy -e prod --yes --json''')}
<p>A {link("docs/bundles/", "bundle")} freezes the plan and scripts at build time, and every stage checks its hashes before running anything, so production gets exactly what test got.</p>

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
ADD https://lonebadgerdownloads.blob.core.windows.net/badger-sql-migrate/v{VERSION}/badger-sql-migrate-{VERSION}-linux-x64.tar.gz /tmp/bsm.tgz
RUN tar -xzf /tmp/bsm.tgz -C /usr/local/bin bsm && rm /tmp/bsm.tgz
COPY db /db
ENTRYPOINT ["bsm", "-C", "/db"]''', "", "Dockerfile")}

<h2>Applications that migrate themselves</h2>
<p>The engine is also a .NET library; see {link("docs/extending/", "Library and providers")}.</p>
""")


def bundles():
    return doc("docs/bundles/", "Bundles", "One hash-verified file with everything a deploy needs, built once and deployed everywhere.", f"""
{sh('''bsm bundle create                    # writes bsm-bundle-<plan head>.zip
bsm bundle create -o release-1.4.zip --no-config
bsm bundle verify release-1.4.zip
bsm --bundle release-1.4.zip status -e prod
bsm --bundle release-1.4.zip deploy -e prod''')}

<h2>What is in a bundle</h2>
<p>A zip file with <code>bsm.plan</code>, the folders of every planned change, <code>repeatable/</code>, the environment scripts under <code>environments/</code>, <code>bsm.yaml</code> (unless <code>--no-config</code>) and a manifest, <code>bsm-bundle.json</code>, listing each file's SHA-256, the provider, the plan head and a content hash that identifies the bundle.</p>
<p>Text is stored normalized (no byte-order mark, LF line endings) and entries are sorted with fixed timestamps, so the same commit produces a byte-identical bundle on Windows, macOS and Linux. A project with problems (<code>bsm plan check</code> errors) cannot be bundled.</p>

<h2>Running from a bundle</h2>
<p><code>--bundle &lt;file&gt;</code> works with every command that reads the project: <code>status</code>, <code>deploy</code>, <code>revert</code>, <code>rebase</code>, <code>verify</code>, <code>repair</code>, <code>diff-schema</code>, <code>render</code>, <code>vars</code>, <code>plan check</code> and <code>plan show</code>. Before anything runs, bsm checks:</p>
<ul>
  <li>every file matches its hash, none is missing and none was added;</li>
  <li>the manifest's content hash matches its file list;</li>
  <li>the plan head computed from the extracted scripts equals the one in the manifest.</li>
</ul>
<p>Any mismatch stops the run and names the files. The bundle is extracted to a temporary folder that is deleted on exit. Commands that write project files (<code>add</code>, <code>tag</code>, <code>rework</code>, <code>init</code>) refuse <code>--bundle</code>.</p>

<h2>Configuration</h2>
<p>When the bundle includes <code>bsm.yaml</code>, it is used, so the environments are frozen with the scripts. Secrets are never stored: keep them as <code>${{env:NAME}}</code> or <code>${{file:path}}</code> references, which resolve on the machine that deploys (relative paths from <code>--project</code>). Bundle with <code>--no-config</code> to read <code>bsm.yaml</code> from <code>--project</code> at deploy time instead.</p>
""")


def drift():
    return doc("docs/drift/", "Schema drift", "Find out what changed outside bsm, or how two databases differ.", f"""
<p>After every deploy and revert, bsm records a canonical dump of the schema and its fingerprint. <code>bsm verify</code> reports when the live fingerprint no longer matches. <code>bsm diff-schema</code> shows what changed.</p>

<h2>What changed since the last deploy</h2>
{sh('''bsm diff-schema -e prod''')}
{code('''-              prod as recorded 2026-11-03 20:15Z
+              prod live
+ column dbo.Customer.Phone
+ column dbo.Hotfix.Id
~ module dbo.vCustomerOrders
    ...
    -   SELECT c.Id, c.Name, c.Email
    +   SELECT c.Id, c.Name, c.Email, c.Phone
    ...
+ object dbo.Hotfix
3 added, 0 removed, 1 changed.''')}
<p><code>+</code> exists only live, <code>-</code> was removed, <code>~</code> changed (with a line diff for views, procedures, functions and triggers). Then undo the change, or capture it as a new change so every environment gets it.</p>

<h2>Two environments</h2>
{sh('''bsm diff-schema -e test --against prod
bsm diff-schema -e test --against prod --exit-code --json   # exit 3 when they differ''')}
<p>Compares two live databases: <code>-</code> is only in the first, <code>+</code> only in the second. Add <code>--script file.sql</code> to get T-SQL that turns the first into the second (see {link("docs/generating/", "Generating changes")}). Each environment uses its own connection, sign-in and fingerprint settings, so if only one of them sets <code>schemaFingerprint.partitionBoundaries: ignore</code>, partition functions and schemes show as changed.</p>

<h2>What is compared</h2>
<p>Schemas, tables and table types with their columns (type, nullability, identity, collation, computed and default definitions, encryption, period columns), indexes, foreign keys, check constraints, views, procedures, functions, triggers, user types, sequences, synonyms, system versioning (period, history table, retention) and partitioning (functions, boundaries, schemes, and the scheme and column of each partitioned table and index). The bsm journal schema is excluded. Names SQL Server generates for unnamed constraints differ on every server, so they are compared as <code>&lt;system&gt;</code>; whitespace around a view, procedure, function or trigger definition and how its CREATE was written (which depend on the tool that sent it) are ignored; columns are compared by position, not by SQL Server's internal column id; Always Encrypted columns compare by key name, never key material, so environments with different keys still match.</p>
""")


def baseline():
    return doc("docs/baseline/", "Existing databases", "Adopting a database that is already in production.", f"""
<p>When a database already has its schema, you do not want to run your initial changes against it. Baseline records them as applied without executing them.</p>
<ol>
  <li>Script the current schema as your first change(s), e.g. with SSMS, SqlPackage or Badger SQL. Keep them as the real history for new environments.</li>
  <li>Tag that point: <code>bsm tag baseline</code>.</li>
  <li>For each existing database: <code>bsm baseline --to @baseline -e prod</code>.</li>
  <li>From here on, add changes normally. New, empty databases run the full plan, including the initial changes.</li>
</ol>
{sh('''bsm add initial-schema --irreversible     # paste the scripted schema into deploy.sql
bsm tag baseline
bsm baseline --to @baseline -e prod
bsm status -e prod                          # initial-schema shown as (baseline)''')}
<p>Baseline only works on a database with no deployed changes, takes the deploy lock, records the environment, and writes a schema fingerprint so later drift is detected.</p>

<h2>Coming from Flyway, DbUp or grate</h2>
<p>For Flyway, DbUp, grate, RoundhousE and Sqitch, <code>bsm import</code> does the conversion and <code>bsm baseline --from</code> reads each database's history for you; see {link("docs/importing/", "Moving from other tools")}. For anything else:</p>
<ul>
  <li>Convert your versioned scripts into changes (one folder each) in their existing order, or collapse history into one <code>initial-schema</code> change.</li>
  <li>Write revert scripts for the changes you might want to undo; mark the rest <code>!irreversible</code>.</li>
  <li>Move repeatable/"anytime" scripts into <code>repeatable/</code>.</li>
  <li>Replace placeholders such as <code>${{name}}</code> (Flyway) with <code>{{name}}</code> or keep <code>$(name)</code>.</li>
  <li>Baseline each existing database. The old tool's history table can stay; bsm uses its own <code>bsm</code> schema.</li>
</ul>
""")


def importing():
    return doc("docs/importing/", "Moving from other tools", "Convert a Flyway, DbUp, grate (or RoundhousE) or Sqitch project, then adopt the databases it already manages without running anything.", f"""
{sh('''bsm import flyway ./flyway-project -C db --dry-run   # see what it would do
bsm import flyway ./flyway-project -C db             # write a new bsm project in db/
bsm plan check -C db
bsm baseline --from flyway -C db -e prod             # once per existing database
bsm deploy -C db -e prod                             # from now on''')}
<p>Import writes a new project (it refuses a folder that already has one) and changes no database. Baseline reads the old tool's journal table in each database, records the changes it already ran, and runs nothing; the old journal table stays until you drop it. New, empty databases just run <code>bsm deploy</code>.</p>
<table>
<tr><th>From</th><th>Import</th><th>Adopt existing databases</th></tr>
<tr><td>{link("docs/importing/#flyway", "Flyway")}</td><td><code>bsm import flyway</code></td><td><code>bsm baseline --from flyway</code> (checksums checked)</td></tr>
<tr><td>{link("docs/importing/#dbup", "DbUp")}</td><td><code>bsm import dbup</code></td><td><code>bsm baseline --from dbup</code></td></tr>
<tr><td>{link("docs/importing/#grate", "grate, RoundhousE")}</td><td><code>bsm import grate</code></td><td><code>bsm baseline --from grate</code> or <code>--from roundhouse</code> (hashes checked)</td></tr>
<tr><td>{link("docs/importing/#sqitch", "Sqitch")}</td><td><code>bsm import sqitch</code></td><td>PostgreSQL: <code>bsm baseline --from sqitch</code> (script hashes checked). Other engines: <code>bsm baseline --to &lt;change&gt;</code></td></tr>
</table>
<p>Imports target SQL Server unless you pass <code>--provider postgresql</code>; Sqitch projects for PostgreSQL (<code>engine = pg</code>) become PostgreSQL projects on their own, and Flyway's <code>jdbc:postgresql</code> URLs become PostgreSQL environments.</p>
<p>Every import marks changes <code>!notx</code> when their scripts contain statements SQL Server refuses inside a transaction (the other tools often run without one), and lists everything it could not carry over.</p>

<h2 id="flyway">Flyway</h2>
<p>Point the import at the folder you run Flyway from. It reads <code>flyway.toml</code> or <code>flyway.conf</code> (also in <code>conf/</code>, or pass <code>--config</code>), takes migrations from <code>flyway.locations</code> (or <code>sql/</code>, <code>src/main/resources/db/migration</code>, or <code>--location</code>), and honours custom prefixes, separators, suffixes and placeholder syntax.</p>
<table>
<tr><th>Flyway</th><th>bsm</th></tr>
<tr><td><code>V1.2__Add_email.sql</code></td><td>Change <code>add-email</code>, in version order (1.10 after 1.9). The plan note <code>flyway V1.2 checksum …</code> links it to Flyway's history. Repeated descriptions get the version appended.</td></tr>
<tr><td><code>U1.2__Add_email.sql</code> (undo)</td><td>The change's <code>revert.sql</code>. Changes without one are <code>!irreversible</code>.</td></tr>
<tr><td><code>R__Customer_view.sql</code></td><td><code>repeatable/Customer_view.sql</code> (same order: by description)</td></tr>
<tr><td><code>V2__x.sql.conf</code> with <code>executeInTransaction=false</code></td><td><code>!notx</code></td></tr>
<tr><td><code>${{name}}</code> placeholders</td><td><code>{{name}}</code> variables. Values from <code>[flyway.placeholders]</code> go under <code>variables:</code>; values from <code>[environments.x.flyway.placeholders]</code> go to that environment. Names bsm reserves (such as <code>env</code>) become <code>{{flyway.env}}</code>.</td></tr>
<tr><td><code>${{flyway:database}}</code>, <code>${{flyway:environment}}</code></td><td><code>{{db}}</code>, <code>{{env}}</code></td></tr>
<tr><td><code>${{flyway:defaultSchema}}</code>, <code>${{flyway:user}}</code>, <code>${{flyway:table}}</code></td><td><code>{{flyway.defaultSchema}}</code>, <code>{{flyway.user}}</code>, <code>{{flyway.table}}</code>, with values from the configuration</td></tr>
<tr><td><code>${{flyway:timestamp}}</code>, <code>${{flyway:filename}}</code></td><td><code>{{deploy.utc}}</code> (ISO 8601 UTC), <code>{{change}}</code> (the change name)</td></tr>
<tr><td><code>flyway.url</code>, <code>[environments.x]</code></td><td>Environments: host, port, instance, database, Windows or Entra authentication, TLS and the JDBC settings SqlClient shares. Passwords are never copied: <code>${{env.DB_PASSWORD}}</code> becomes <code>${{env:DB_PASSWORD}}</code>, and a literal password becomes a reference to <code>FLYWAY_PASSWORD</code>.</td></tr>
<tr><td>Callbacks (<code>afterMigrate.sql</code> …), baseline migrations (<code>B…</code>), Java migrations</td><td>Not imported; listed in the output</td></tr>
</table>

<h3>Baseline from Flyway's history</h3>
{sh('''bsm baseline --from flyway -e prod                         # reads dbo.flyway_schema_history
bsm baseline --from flyway -e prod --journal app.flyway_history''')}
<p>Applied, undone and baselined versions are taken from the history in order. Baseline refuses, and says why, when:</p>
<ul>
  <li>a version Flyway applied has no change in the plan;</li>
  <li>a change before the last applied one was never applied (Flyway's <code>outOfOrder</code>);</li>
  <li>Flyway's recorded checksum differs from the imported script, meaning the database ran a different version of it. bsm computes checksums exactly as Flyway does; <code>--skip-checksums</code> turns the check off.</li>
</ul>
<p>Repeatable scripts whose latest run in Flyway's history has the checksum of the same script rendered for this environment are recorded too, so the first deploy only runs the ones that differ.</p>

<h2 id="dbup">DbUp</h2>
{sh('''bsm import dbup ./src/App.Database/Scripts -C db --var schemaName=app --run-always "Always/**"
bsm baseline --from dbup -C db -e prod                    # reads dbo.SchemaVersions''')}
<p>DbUp's scripts and options live in code, so tell the import what the code does:</p>
<table>
<tr><th>DbUp</th><th>bsm import</th></tr>
<tr><td>Scripts embedded as resources (<code>WithScriptsEmbeddedInAssembly</code>)</td><td>Default: script names are the folder path joined with dots (<code>Orders.Script0003.sql</code>), and the order is DbUp's: ordinal by name. Baseline matches them to the full resource names in the journal.</td></tr>
<tr><td><code>WithScriptsFromFileSystem</code></td><td><code>--file-names</code>: names are the file names</td></tr>
<tr><td><code>ScriptType.RunAlways</code> scripts</td><td><code>--run-always "glob"</code>: imported as repeatables, which run when they change rather than on every deploy</td></tr>
<tr><td><code>$name$</code> variables, <code>WithVariable("name", value)</code></td><td><code>{{name}}</code> variables; pass the values with <code>--var name=value</code>, or add them to <code>bsm.yaml</code> afterwards (<code>plan check</code> fails until every variable has a value). <code>--no-variables</code> if the code calls <code>WithVariablesDisabled()</code>.</td></tr>
</table>
<p>DbUp has no down scripts, so every change is <code>!irreversible</code>. DbUp runs without a transaction unless configured to; bsm runs each change in one, and <code>bsm plan check</code> lists any statement that needs <code>!notx</code>.</p>

<h2 id="grate">grate and RoundhousE</h2>
{sh('''bsm import grate ./db -C bsm-db --var AppRole=app_reader
bsm baseline --from grate -C bsm-db -e TEST                 # reads grate.ScriptsRun
bsm baseline --from roundhouse -C bsm-db -e TEST            # reads RoundhousE.ScriptsRun''')}
<p>Point the import at the folder you pass to grate as <code>--sqlfilesdirectory</code>. grate's folders run in a fixed order, and the import keeps it:</p>
<table>
<tr><th>grate</th><th>bsm</th></tr>
<tr><td><code>up/</code> (run once)</td><td>One change per script, in grate's order (by path, ignoring case). The note <code>grate up/&lt;name&gt;</code> links it to <code>ScriptsRun</code>. grate has no down scripts, so changes are <code>!irreversible</code>.</td></tr>
<tr><td><code>runAfterCreateDatabase/</code>, <code>alterDatabase/</code></td><td>Changes at the start of the plan: they run once on each new database</td></tr>
<tr><td><code>runBeforeUp/</code>, <code>runFirstAfterUp/</code>, <code>functions/</code>, <code>views/</code>, <code>sprocs/</code>, <code>triggers/</code>, <code>indexes/</code>, <code>runAfterOtherAnyTimeScripts/</code> (anytime)</td><td>Repeatables under numbered folders that keep grate's folder order: <code>repeatable/06-functions/</code>, <code>07-views/</code>, <code>08-sprocs/</code> …</td></tr>
<tr><td><code>beforeMigration/</code>, <code>permissions/</code>, <code>afterMigration/</code>, <code>everytime.*</code> (every time)</td><td>Repeatables, which bsm runs when they change rather than on every deploy</td></tr>
<tr><td><code>*.ENV.*</code> scripts</td><td><code>environments/&lt;name&gt;/</code> for each environment in the file name (the upper-case parts, or the names you pass with <code>--environment</code>); bsm.yaml gets those environments</td></tr>
<tr><td><code>{{{{Token}}}}</code></td><td><code>{{Token}}</code> variables; values for <code>--ut</code> tokens come from <code>--var</code>. <code>{{{{DatabaseName}}}}</code>, <code>{{{{ServerName}}}}</code> and <code>{{{{EnvironmentName}}}}</code> become <code>{{db}}</code>, <code>{{db.host}}</code> and <code>{{env}}</code>; folder-name tokens get their values; connection strings are never written into scripts.</td></tr>
<tr><td><code>createDatabase/</code>, <code>dropDatabase/</code></td><td>Not imported: bsm does not create databases</td></tr>
</table>
<p>Renamed folders: <code>--folder up=ddl</code>. If grate ran with <code>--ignoredirectorynames</code> or <code>--disabletokenreplacement</code>, pass <code>--ignore-directory-names</code> or <code>--no-tokens</code>.</p>
<p>grate records a hash of each script after replacing its tokens, so <code>baseline --from grate</code> renders every imported script with this environment's variables and compares: a mismatch means the database ran different text (or a token now has another value), and baseline stops. Repeatables and environment scripts grate already ran with the same text are recorded as applied, so they do not run again. <code>runAfterCreateDatabase/</code> scripts may be missing from the journal: grate only runs them for databases it created.</p>

<h2 id="sqitch">Sqitch</h2>
{sh('''bsm import sqitch ./flipr -C bsm-db
bsm import sqitch ./flipr -C bsm-db --engine pg      # use that engine's settings from sqitch.conf''')}
<p>Sqitch is closest to bsm: a plan of named changes with deploy, revert and verify scripts. The import reads <code>sqitch.conf</code> (top_dir, plan_file, deploy/revert/verify dirs, extension, per-engine overrides) and keeps the plan's order, dependencies, tags, planner and timestamp.</p>
<ul>
  <li>A reworked change (the same name again after a tag) becomes <code>&lt;name&gt;-v2</code>, <code>-v3</code> …; earlier versions take their scripts from <code>&lt;name&gt;@&lt;tag&gt;.sql</code>, as Sqitch keeps them. Dependencies such as <code>users@v1.0.0</code> point at the right version.</li>
  <li>Conflicts (<code>!change</code>) and dependencies on other Sqitch projects have no bsm equivalent and are listed.</li>
  <li>Scripts that manage their own transaction (<code>BEGIN; … COMMIT;</code>, as Sqitch's templates do) are <code>!notx</code>.</li>
  <li>Scripts are copied as written. A Sqitch project for PostgreSQL becomes a PostgreSQL project; one for another engine needs its SQL translated for the provider you choose (<code>bsm plan check</code> flags what does not parse).</li>
</ul>
{sh('''bsm baseline --from sqitch -e prod      # reads sqitch.changes in the same PostgreSQL database''')}
<p>On PostgreSQL, baseline reads Sqitch's registry (<code>sqitch.changes</code>), matches each deployed change by project, name and version (a reworked change is deployed once per version), and checks the SHA-1 Sqitch recorded for each deploy script against the imported one. Sqitch's registry schema stays until you drop it.</p>

<h2>Text that must stay literal</h2>
<p>None of these tools expands <code>{{name}}</code> or <code>$(name)</code>, but bsm does. The import escapes <code>{{name}}</code> to <code>{{{{name}}}}</code>, and if any script contains <code>$(name)</code> text it sets <code>preprocessor.sqlcmdSyntax: false</code> in <code>bsm.yaml</code>.</p>

<h2>Before you baseline</h2>
<ul>
  <li>Write the reverts you want now: a change's hash includes its revert script, so adding one later means <code>bsm repair</code> in every database.</li>
  <li>Review the generated environments and fill in what the output asks for (tenant ids, variables without values).</li>
  <li>Check the result: deploy the project to an empty database and compare it with one the old tool built, using <code>bsm diff-schema -e new --against old</code>. Only the old tool's journal table should differ.</li>
  <li>Scripts the old tool ran every time (DbUp run-always, grate everytime folders, Flyway callbacks) now run only when they change. If one must run on every deploy, make it idempotent and keep it in a change you add per release, or run it from your pipeline.</li>
</ul>
""")


def postgresql():
    return doc("docs/postgresql/", "PostgreSQL", "PostgreSQL 12 and later: self-hosted, Azure Database for PostgreSQL, Amazon RDS and Aurora, and other compatible services.", f"""
{sh('''bsm init --provider postgresql
bsm add create-product
bsm deploy''')}
{yaml('''provider: postgresql
defaultEnvironment: local
environments:
  local:
    host: localhost
    port: 5432
    database: shop
    authentication: password
    user: postgres
    password: ${env:BSM_LOCAL_PASSWORD}
    tls:
      mode: optional
  prod:
    host: shop-prod.postgres.database.azure.com
    database: shop
    authentication: entraDefault        # an Entra token is the password
    user: shop-deployers                # the Entra user, group or managed identity the server knows
    tls:
      mode: strict
    connectionProperties:               # any Npgsql connection-string keyword
      Search Path: app,public
      Command Timeout: 120''', "bsm.yaml")}
<p>Everything else is the same as on SQL Server: the plan, hash certification, revert from stored scripts, environments, variables, bundles, <code>diff-schema</code>, rework and repair, and imports.</p>

<h2>Sign-in and TLS</h2>
<table>
<tr><th><code>authentication</code></th><th>PostgreSQL</th></tr>
<tr><td><code>password</code></td><td>User and password (SCRAM or MD5, whatever the server asks for)</td></tr>
<tr><td><code>integrated</code></td><td>Kerberos (GSSAPI); run <code>kinit</code> first on macOS and Linux</td></tr>
<tr><td><code>entraDefault</code>, <code>entraInteractive</code>, <code>entraManagedIdentity</code>, <code>entraWorkloadIdentity</code>, <code>entraAzureCli</code>, … </td><td>Azure Database for PostgreSQL: bsm gets a token for <code>https://ossrdbms-aad.database.windows.net</code> and sends it as the password. <code>user</code> is required.</td></tr>
</table>
<table>
<tr><th><code>tls.mode</code></th><th>Npgsql <code>SSL Mode</code></th></tr>
<tr><td><code>disabled</code></td><td>Disable</td></tr>
<tr><td><code>optional</code></td><td>Prefer</td></tr>
<tr><td><code>mandatory</code></td><td>Require (encrypted, certificate not checked); VerifyCA with <code>tls.serverCertificate</code></td></tr>
<tr><td><code>strict</code></td><td>VerifyFull (certificate and host name checked)</td></tr>
</table>
<p><code>tls.serverCertificate</code> is the CA certificate file to trust. SSH tunnels work as on SQL Server; with one, use <code>mandatory</code>, because the host name no longer matches the certificate.</p>

<h2>Statements and transactions</h2>
<p>bsm splits scripts into statements at semicolons, the way psql does: semicolons inside strings, quoted identifiers, comments and <code>$$</code> dollar-quoted bodies (functions, <code>DO</code> blocks) do not count. A change still runs in one transaction, and PostgreSQL's transactional DDL means a failure leaves nothing behind. Errors name the line of the statement that failed.</p>
<p>Statements PostgreSQL refuses in a transaction block need a <code>!notx</code> change, which runs each statement on its own:</p>
{sql('''CREATE INDEX CONCURRENTLY IF NOT EXISTS product_name_idx ON product (name);''', "changes/product-name-index/deploy.sql")}
{code('''product-name-index [create-product] !notx''', "", "bsm.plan")}

<h2 id="linter">Linter</h2>
<table>
<tr><th>Code</th><th>Severity</th><th>Finding</th></tr>
<tr><td><code>BSM001</code></td><td>Error</td><td><code>CREATE/DROP INDEX CONCURRENTLY</code>, <code>REINDEX … CONCURRENTLY</code>, <code>VACUUM</code>, <code>CREATE/DROP DATABASE</code>, <code>ALTER SYSTEM</code>, tablespace and subscription commands in a change that is not <code>!notx</code></td></tr>
<tr><td><code>BSM003</code></td><td>Error</td><td><code>BEGIN</code>, <code>COMMIT</code>, <code>ROLLBACK</code> or <code>END</code> in a transactional change: it would end bsm's transaction. (Inside function bodies and <code>DO</code> blocks they are fine.)</td></tr>
<tr><td><code>BSM005</code></td><td>Error</td><td>psql meta-commands such as <code>\\set</code> or <code>\\i</code>: bsm talks to the server, not through psql</td></tr>
<tr><td><code>BSM006</code></td><td>Error</td><td><code>COPY … FROM STDIN</code>, which needs psql to stream the data</td></tr>
<tr><td><code>BSM007</code></td><td>Warning</td><td><code>ALTER TYPE … ADD VALUE</code> in a transaction: the new value cannot be used until it commits</td></tr>
</table>

<h2>Schema fingerprint</h2>
<p>The fingerprint covers schemas, extensions, tables (including partitioning and row-level security settings), columns (types, defaults, identity, generated columns, collations), constraints, indexes, views and materialized views, functions and procedures, triggers, sequences, enum, domain, composite and range types, and policies. Definitions come from PostgreSQL's own <code>pg_get_*def</code> functions with an empty <code>search_path</code>, so names are always schema-qualified and identical databases fingerprint identically. Objects that belong to extensions, identity sequences and the bsm journal schema are left out.</p>

<h2>Journal and lock</h2>
<p>The journal tables are the same as on SQL Server, in schema <code>bsm</code> (<code>stateSchema</code>). The deploy lock is a session-level advisory lock (<code>pg_try_advisory_lock</code>), so it works through PgBouncer only in session pooling mode; connect deploys directly or in session mode.</p>

<h2>Not available yet</h2>
<ul>
  <li><code>bsm add --from-diff</code> and <code>diff-schema --script</code>: generating DDL is SQL Server only for now. <code>diff-schema</code> itself works.</li>
  <li>Always Encrypted is a SQL Server feature.</li>
</ul>
""")


def journal():
    return doc("docs/journal/", "Journal tables", "What bsm stores in each database (schema bsm, configurable).", f"""
<table>
<tr><th>Table</th><th>Contents</th></tr>
<tr><td><code>bsm.changes</code></td><td>One row per applied change: sequence, name, script hash, chain id, deploy/revert/verify scripts, flags, deployed at/by/host, tool version, duration, out-of-order and baseline flags, rendered hashes</td></tr>
<tr><td><code>bsm.repeatables</code></td><td>Current and previous script and hash of each repeatable, applied at/by</td></tr>
<tr><td><code>bsm.events</code></td><td>Append-only log: deploy, revert, fail (with the error), repeatable, baseline, resource, repair; actor, host, tool version, environment</td></tr>
<tr><td><code>bsm.schema_snapshots</code></td><td>Schema fingerprint and canonical dump after each run, with the head chain id</td></tr>
<tr><td><code>bsm.meta</code></td><td><code>state_version</code> (journal layout) and <code>environment</code> (owner)</td></tr>
</table>
{sql('''-- what is deployed, in order
SELECT sequence, name, deployed_at, deployed_by, applied_out_of_order FROM bsm.changes ORDER BY sequence;

-- recent failures
SELECT TOP (20) at, name, message, actor, host FROM bsm.events WHERE kind = 'fail' ORDER BY id DESC;''')}
<p>Badger SQL Migrate creates the schema and tables on the first deploy and upgrades them itself; a newer journal than the tool understands stops the tool with a message to upgrade. Change the schema name with <code>stateSchema</code>. Do not edit these tables by hand: they are the certificate.</p>
""")


def troubleshooting():
    items = [
        ("“These changes were edited after they were deployed”", "A deployed change's scripts changed (even whitespace). For new behaviour, restore the deployed version and use <code>bsm rework</code>; for a harmless edit such as a revert fix, use <code>bsm repair</code>. See " + link("docs/fixing-changes/", "Fixing deployed changes") + "."),
        ("“This database belongs to environment 'test'…”", "You are pointing one environment's settings at another environment's database. Check <code>--env</code> and the host/database. If the database was copied on purpose, pass <code>--claim-environment</code> once."),
        ("“Timed out … waiting for the deploy lock”", "Another deploy is running against the database. Wait, or raise <code>lockTimeoutSeconds</code>."),
        ("“… cannot run inside a transaction”", "Lint BSM001. Mark the change <code>!notx</code> in the plan and make the script re-runnable."),
        ("“unknown variable {x}”", "Define it under <code>variables:</code>, pass <code>--var x=…</code>, write <code>{{x}}</code> for a literal, or set <code>preprocessor.unknown: keep</code>."),
        ("“renders differently than when it was deployed”", "A variable used by the change changed value. Run <code>bsm vars</code>; restore the value, or use <code>--allow-variable-drift</code> if the change is intended."),
        ("“Changes … come before changes already deployed” (strict ordering)", "A change merged above changes already in this database. In dev, use <code>--ordering dependency</code> or <code>rebase</code>; production should never see this if it only receives released plans."),
        ("“schema drift”", "Something changed the schema outside bsm. <code>bsm diff-schema</code> lists what changed since the last deploy; then either undo it or capture it as a new change. See " + link("docs/drift/", "Schema drift") + "."),
        ("BSM004: “SYSTEM_VERSIONING is turned OFF … and not turned back ON”", "The script switches versioning off (usually to change the table) but never back on. Add <code>ALTER TABLE … SET (SYSTEM_VERSIONING = ON (HISTORY_TABLE = …))</code> at the end, or drop the table if that is the intent. See " + link("docs/temporal-partitions/", "Temporal tables and partitions") + "."),
        ("“Bundle … failed verification”", "A file in the bundle was changed, added or removed after it was built. Rebuild it from source control with <code>bsm bundle create</code>; never edit a bundle."),
        ("Entra: “credential unavailable” or browser does not open", "On SSH or headless machines use <code>entraDeviceCode</code>. For CLI credentials, sign in with <code>az login</code> first. <code>bsm auth clear</code> resets saved sign-ins."),
        ("Linux: token cache error", "Install libsecret (and a keyring), use <code>entra.tokenCache: memory</code>, or set <code>allowUnencryptedTokenCache: true</code>."),
        ("Kerberos on macOS/Linux fails", "Run <code>kinit user@REALM</code>, check <code>klist</code>, and set <code>connectionProperties: { Server SPN: MSSQLSvc/host:1433 }</code> if the SPN differs."),
        ("“column master key … cannot be changed”", "The database's CMK points to a different key path. Add a new CMK name and rotate; bsm never rewrites CMK metadata."),
        ("macOS: “bsm cannot be opened”", "Run <code>xattr -d com.apple.quarantine bsm</code> once, or install the .NET tool."),
    ]
    rows = "".join(f"<h3>{q}</h3><p>{a}</p>" for q, a in items)
    return doc("docs/troubleshooting/", "Troubleshooting", "Common messages and what to do about them.", f"""
<p>Start with:</p>
{sh('''bsm doctor -e <environment> -v''')}
<p>It checks configuration, Entra sign-in, the connection, server facts, the journal and environment resources, and reports each separately.</p>
{rows}
<h2>Support</h2>
<p>Email <a href="mailto:support@lonebadger.tech">support@lonebadger.tech</a> or open an issue on <a href="https://github.com/lonebadger-tech/badger-sql-migrate/issues">GitHub</a>. Include <code>bsm --version</code>, the command, and the output of <code>bsm doctor</code>. Never send passwords or tokens.</p>
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
<p>A provider implements <code>IDatabaseProvider</code>: sessions, batch splitting, linting, journal tables, a deploy lock, a schema fingerprint, and optionally environment resources. It must pass the shared conformance test suite. The engine, plan, certification, variables, Entra ID tokens and SSH tunnels work unchanged; the PostgreSQL provider was added this way without touching any of them. <a href="https://github.com/lonebadger-tech/badger-sql-migrate/blob/dev/docs/writing-a-provider.md">Writing a provider</a> walks through it, and <a href="https://github.com/lonebadger-tech/badger-sql-migrate/blob/dev/CONTRIBUTING.md">CONTRIBUTING.md</a> covers sending it upstream. MySQL and SQLite are natural next providers.</p>

<h2>Source</h2>
<p><a href="https://github.com/lonebadger-tech/badger-sql-migrate">github.com/lonebadger-tech/badger-sql-migrate</a> · <a href="{BASE}/license/">MIT license</a>. Contributions welcome.</p>
""")
