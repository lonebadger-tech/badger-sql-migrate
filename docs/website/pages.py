"""Content for the overview, download, releases, comparison and examples pages."""
import html
import os
import re

from build import BASE, DOWNLOAD_BASE, REPO, RELEASE_DATE, VERSION, Page, code, sh, sql, yaml
import docs_pages


def all_pages(artifacts):
    return [home(), download(artifacts), releases(), compare(), examples(), license_page(), *docs_pages.pages()]


# ---------------------------------------------------------------- license

def license_page():
    # The repository's LICENSE, the same text that ships in every download.
    with open(os.path.join(REPO, "LICENSE"), encoding="utf-8") as f:
        text = f.read().split("\n", 2)[2].strip()  # drop the title line; the page heading replaces it
    # Unwrap the file's hard line breaks: one paragraph per block (one line per item, if it has numbered items).
    paragraphs = []
    for block in text.split("\n\n"):
        items = []
        for line in block.splitlines():
            if not items or re.match(r"\s*(\d+|[a-z])\. ", line):
                items.append(line.strip())
            else:
                items[-1] += " " + line.strip()
        paragraphs.append("<p>" + "<br>".join(html.escape(i) for i in items) + "</p>")
    body = "\n".join(paragraphs)
    return Page("license/", "Badger SQL Migrate license (MIT)", "Badger SQL Migrate is free software under the MIT license.", body, heading="License", lede="Badger SQL Migrate is open source under the MIT license.")


# ---------------------------------------------------------------- home

def home():
    body = f"""
<div class="hero">
  <img class="icon" src="{BASE}/icon.svg" alt="Badger SQL Migrate icon">
  <div>
    <h1>Badger SQL Migrate</h1>
    <p class="lede">Database migrations for SQL Server and Azure SQL that you can prove. Free, cross-platform, with real up <em>and</em> down.</p>
    <div class="actions">
      <a class="button primary" href="{BASE}/download/">Download {VERSION}</a>
      <a class="button" href="{BASE}/docs/quick-start/">Quick start</a>
      <a class="button" href="{BASE}/examples/">Examples</a>
    </div>
  </div>
</div>

<div class="features">
  <div><h3>Named changes, no collisions</h3><p>Changes have names, not version numbers. The plan file merges with git's union driver, so two developers adding changes the same day never collide.</p></div>
  <div><h3>Hash-certified</h3><p>SHA-256 per change and a hash chain over the whole plan. Same head hash means byte-identical history on every machine and environment.</p></div>
  <div><h3>Real down, for free</h3><p>Revert scripts are first-class. They are stored in the database at deploy time, so rollback works from any branch, even after the change is gone.</p></div>
  <div><h3>Environments built in</h3><p>dev, test, prod with inheritance, protected environments, fix-forward production, and a database that remembers which environment owns it.</p></div>
  <div><h3>Every Entra ID flow</h3><p>Browser and device code with MFA (cached between runs), managed identity, service principals, workload identity, Azure CLI, pipelines, plus SQL and Windows auth.</p></div>
  <div><h3>Always Encrypted keys</h3><p>Scripts use the same key names everywhere; each environment brings its own column master keys. Badger provisions and rotates them.</p></div>
  <div><h3>Variables that render</h3><p><code>[calc_{{env}}]</code> becomes <code>[calc_test]</code> in test. Built-ins, nested variables, per-environment values, and a hash of the SQL that actually ran.</p></div>
  <div><h3>Safe by default</h3><p>A deploy lock for concurrent pipelines, transactional changes with verify scripts, a T-SQL linter, drift detection, and edited-after-deploy detection.</p></div>
  <div><h3>One binary, any OS</h3><p>Windows, macOS and Linux (x64 and Arm), or a .NET tool. No Java, no Perl, no sqlcmd. Connects through SSH bastions too.</p></div>
</div>

<h2>How it works</h2>
<p>A project is a folder with a <code>badger.plan</code> (the order), one folder per change with <code>deploy.sql</code>, <code>revert.sql</code> and an optional <code>verify.sql</code>, and a <code>badger.yaml</code> describing your environments.</p>
{code('''db/
  badger.yaml              environments, authentication, variables, keys
  badger.plan              one change per line, in deploy order
  changes/
    create-customer/
      deploy.sql           up
      revert.sql           down
      verify.sql           proves the deploy worked (optional)
  repeatable/              views, procedures, functions: re-applied when changed
  environments/dev/        scripts that run only in dev (seed data)''')}
{sh('''bsm add add-customer-email --requires create-customer
bsm deploy -e test
bsm status -e prod
bsm revert --to create-customer -e test''')}
<p>Every deploy records the script hashes, who ran it, where, and a hash chain that certifies the order. <code>bsm status</code> tells you when a database is <strong>certified</strong>: it received exactly the plan, in order, byte for byte.</p>

<h2>Why another migration tool?</h2>
<p>Flyway charges for undo and collides when two branches pick the same version number. DbUp only goes up and ignores edited scripts. SSDT computes a different script on each machine. Sqitch has the right model but a Perl install and weak SQL Server support. Badger SQL Migrate keeps the best ideas of each and fixes the gaps. <a href="{BASE}/compare/">See the comparison →</a></p>

<p><a href="{BASE}/docs/">Documentation</a> · <a href="{BASE}/examples/">Example scripts</a> · <a href="{BASE}/download/">Download</a> · <a href="{BASE}/releases/">What's new</a> · <a href="https://github.com/lonebadger-tech/badger-sql-migrate">Source on GitHub</a></p>
"""
    return Page("", "Badger SQL Migrate: hash-certified SQL Server migrations", "Free, cross-platform, hash-certified database migrations for SQL Server and Azure SQL with real up and down, named changes, environments, Entra ID and Always Encrypted.", body)


# ---------------------------------------------------------------- download

DOWNLOADS = [
    ("Windows x64", "win-x64", "zip"),
    ("Windows Arm64", "win-arm64", "zip"),
    ("macOS Apple silicon", "osx-arm64", "tar.gz"),
    ("macOS Intel", "osx-x64", "tar.gz"),
    ("Linux x64 (glibc)", "linux-x64", "tar.gz"),
    ("Linux Arm64 (glibc)", "linux-arm64", "tar.gz"),
    ("Linux x64 (Alpine / musl)", "linux-musl-x64", "tar.gz"),
    ("Any OS with .NET 10 runtime", "portable", "zip"),
]


def size(n):
    return f"{n / 1024 / 1024:.0f} MB" if n else ""


def download(artifacts):
    rows = []
    for label, rid, ext in DOWNLOADS:
        name = f"badger-sql-migrate-{VERSION}-{rid}.{ext}"
        digest, n = artifacts.get(name, ("", 0))
        rows.append(f'<tr><td>{label}</td><td><a href="{DOWNLOAD_BASE}/{name}">{name}</a></td><td>{size(n)}</td>'
                    f'<td><code title="{digest}">{digest[:16]}{"…" if digest else ""}</code></td></tr>')
    tool = f"Badger.SqlMigrate.{VERSION}.nupkg"
    tool_digest, tool_size = artifacts.get(tool, ("", 0))
    rows.append(f'<tr><td>.NET tool package</td><td><a href="{DOWNLOAD_BASE}/{tool}">{tool}</a></td><td>{size(tool_size)}</td>'
                f'<td><code title="{tool_digest}">{tool_digest[:16]}{"…" if tool_digest else ""}</code></td></tr>')

    body = f"""
<p class="lede">Version {VERSION} · {RELEASE_DATE} · <a href="{BASE}/license/">MIT license</a></p>

<h2>.NET tool</h2>
<p>If you have the .NET 10 SDK, this is the easiest way to install and update:</p>
{sh('''dotnet tool install --global Badger.SqlMigrate
bsm --version''')}
<p>Update with <code>dotnet tool update --global Badger.SqlMigrate</code>. To pin a version for a repository, use a local tool manifest:</p>
{sh(f'''dotnet new tool-manifest
dotnet tool install Badger.SqlMigrate --version {VERSION}
dotnet bsm status''')}

<h2>Standalone binaries</h2>
<p>Self-contained single-file executables: nothing else to install. Each archive contains <code>bsm</code> (or <code>bsm.exe</code>), the license and the readme.</p>
<table class="downloads">
<thead><tr><th>Platform</th><th>File</th><th>Size</th><th>SHA-256</th></tr></thead>
<tbody>{''.join(rows)}</tbody>
</table>
<p>All hashes: <a href="{DOWNLOAD_BASE}/SHA256SUMS">SHA256SUMS</a>. Hover a hash for the full value.</p>

<h3>macOS</h3>
{sh(f'''curl -LO {DOWNLOAD_BASE}/badger-sql-migrate-{VERSION}-osx-arm64.tar.gz
tar -xzf badger-sql-migrate-{VERSION}-osx-arm64.tar.gz
xattr -d com.apple.quarantine bsm   # downloaded with a browser? clear Gatekeeper's quarantine flag
sudo mv bsm /usr/local/bin/
bsm --version''')}
<p class="note">The macOS binaries are signed ad hoc but not notarized, so a copy downloaded with a browser needs the <code>xattr</code> line above (or right-click › Open once). The .NET tool has no such step.</p>

<h3>Linux</h3>
{sh(f'''curl -LO {DOWNLOAD_BASE}/badger-sql-migrate-{VERSION}-linux-x64.tar.gz
tar -xzf badger-sql-migrate-{VERSION}-linux-x64.tar.gz
sudo mv bsm /usr/local/bin/''')}
<p>Use the <code>linux-musl-x64</code> build on Alpine. Integrated (Kerberos) authentication needs the system's GSSAPI library (<code>libgssapi-krb5-2</code> on Debian/Ubuntu, <code>krb5-libs</code> on RHEL, <code>krb5</code> on Alpine).</p>

<h3>Windows</h3>
{code(f'''Invoke-WebRequest {DOWNLOAD_BASE}/badger-sql-migrate-{VERSION}-win-x64.zip -OutFile bsm.zip
Expand-Archive bsm.zip -DestinationPath "$env:LOCALAPPDATA\\bsm"
# add %LOCALAPPDATA%\\bsm to PATH, then:
bsm --version''', "powershell")}

<h3>Verify a download</h3>
<p>A migration tool that certifies hashes should be verifiable itself:</p>
{sh(f'''curl -LO {DOWNLOAD_BASE}/SHA256SUMS
shasum -a 256 --check --ignore-missing SHA256SUMS      # macOS
sha256sum --check --ignore-missing SHA256SUMS          # Linux''')}
{code(f'''(Get-FileHash badger-sql-migrate-{VERSION}-win-x64.zip -Algorithm SHA256).Hash   # Windows: compare with SHA256SUMS''', "powershell")}

<h2>Build from source</h2>
{sh('''git clone https://github.com/lonebadger-tech/badger-sql-migrate
cd badger-sql-migrate
dotnet test                 # SQL Server tests use Docker (Testcontainers) and skip without it
build/publish.sh            # all of the files above, into ./artifacts''')}
<p>Next: <a href="{BASE}/docs/quick-start/">Quick start</a>.</p>
"""
    return Page("download/", "Download Badger SQL Migrate", "Download Badger SQL Migrate for Windows, macOS and Linux, or install it as a .NET tool.", body, heading="Download", wide=True)


# ---------------------------------------------------------------- releases

def releases():
    body = f"""
<section class="release" id="v{VERSION}">
  <h2>Version {VERSION} <span class="date">{RELEASE_DATE}</span></h2>
  <p>The first release.</p>
  <h3>Migrations</h3>
  <ul>
    <li>Named changes ordered by <code>badger.plan</code>, with dependencies, tags, <code>!notx</code> and <code>!irreversible</code>; merges cleanly with git's union driver</li>
    <li>Deploy, revert and verify scripts; each change, its verify script and its journal row commit in one transaction</li>
    <li>Revert runs the scripts stored in the database at deploy time; <code>--to</code>, <code>--last</code>, <code>--all</code> and <code>rebase</code></li>
    <li>Repeatable scripts (views, procedures, functions) with dependency ordering, and environment-only scripts</li>
    <li>Baseline an existing database; reviewable SQL script output with <code>deploy --script</code></li>
  </ul>
  <h3>Certification and safety</h3>
  <ul>
    <li>SHA-256 script hashes and a hash chain over the plan; certified status when a database matches the plan exactly</li>
    <li>Schema fingerprint after each run and drift detection with <code>verify</code></li>
    <li>Edited-after-deploy detection, deploy lock for concurrent deployers, dependency or strict ordering</li>
    <li>T-SQL linter: statements that cannot run in a transaction, <code>USE</code>, explicit transactions</li>
  </ul>
  <h3>Environments and connectivity</h3>
  <ul>
    <li>Environments with <code>extends</code>, templates, <code>protected</code>, <code>allowRevert</code> and a database ownership guard</li>
    <li>SQL logins, Windows/Kerberos, and every Microsoft Entra ID flow including MFA with a persistent token cache</li>
    <li>Any SqlClient connection-string setting, strict TLS (TDS 8.0), SSH tunnels with host-key verification</li>
    <li>Always Encrypted column master and column encryption keys per environment, with rotation; Azure Key Vault, Windows stores and custom key store providers</li>
    <li>Script variables: built-ins like <code>{{env}}</code> and <code>{{db}}</code>, nested and per-environment variables, rendered-SQL hashes</li>
  </ul>
  <h3>Platforms</h3>
  <ul>
    <li>Windows, macOS and Linux (x64 and Arm64, glibc and musl), a portable build, and a .NET global tool</li>
  </ul>
</section>
"""
    return Page("releases/", "Badger SQL Migrate version history", "What's new in each version of Badger SQL Migrate.", body, heading="What's new in Badger SQL Migrate")


# ---------------------------------------------------------------- compare

def compare():
    y, n, p = '<span class="yes">Yes</span>', '<span class="no">No</span>', '<span class="part">Partly</span>'
    rows = [
        ("Free down / undo", y, '<span class="no">Paid</span>', n, n, y + " (generated)", p + " (older dacpac)", y, y + " (targeted: Pro)"),
        ("Two developers adding changes at once", y + " (named + union merge)", n + " (same V number)", p, p, p + " (snapshot conflicts)", y, p + " (plan conflicts)", p + " (changelog conflicts)"),
        ("Cryptographic hash of every change", y + " SHA-256", p + " CRC32", n, y, n, n, y + " SHA-1", p + " MD5"),
        ("Certifies the order across machines", y + " hash chain", n, n, n, n, n, y, n),
        ("Same SQL on every machine", y, y, y, y, y, n + " (diff at deploy time)", y, y),
        ("Rollback without the original checkout", y + " (stored in DB)", n, n, n, n, n, n, n),
        ("Explicit dependencies between changes", y, n, n, n, n, "n/a", y, n),
        ("Schema drift detection", y, p + " (paid)", n, n, n, y, n, p + " (Pro)"),
        ("Deploy lock for concurrent pipelines", y, y, n, p, y + " (EF 9+)", n, y, y),
        ("Per-environment config and protection", y, y, n, p, n, p, y, y),
        ("Every Entra ID flow incl. MFA", y, p, p, p, p, p, n, p),
        ("Always Encrypted key provisioning", y, n, n, n, n, p, n, n),
        ("Repeatable scripts (views, procs)", y, y, n, y, n, y, n, y),
        ("No runtime to install", y + " single binary", n + " JVM", "library", ".NET tool", ".NET", "SqlPackage", n + " Perl", n + " JVM"),
    ]
    head = "<tr><th></th><th>Badger SQL Migrate</th><th>Flyway</th><th>DbUp</th><th>grate</th><th>EF Core</th><th>SSDT / dacpac</th><th>Sqitch</th><th>Liquibase</th></tr>"
    table = "<table><thead>" + head + "</thead><tbody>" + "".join(
        "<tr>" + "".join(f"<td>{c}</td>" for c in r) + "</tr>" for r in rows) + "</tbody></table>"
    body = f"""
<p>Every tool here is good at something. This is how Badger SQL Migrate's design relates to them, for SQL Server and Azure SQL. Feature sets change; check each project for its current state.</p>
{table}

<h2>What Badger SQL Migrate keeps</h2>
<ul>
  <li><strong>From Flyway:</strong> validate, repair, baseline and repeatable scripts; a simple mental model.</li>
  <li><strong>From Sqitch:</strong> named changes, a plan file, explicit dependencies, deploy/revert/verify triplets and a hash chain.</li>
  <li><strong>From grate:</strong> folders for programmable objects that re-run when they change.</li>
  <li><strong>From DbUp:</strong> an embeddable library, so an app can migrate itself at startup.</li>
  <li><strong>From SSDT:</strong> knowing when the live schema drifted from what was deployed.</li>
</ul>

<h2>What it fixes</h2>
<ul>
  <li><strong>Version collisions.</strong> Changes are named; order lives in a line-based plan merged by git's union driver. <code>bsm plan check</code> catches the rare real conflict (two changes with the same name).</li>
  <li><strong>Paid or lossy down.</strong> Revert scripts are written by you, required by default, free, and stored with the deploy.</li>
  <li><strong>Weak checksums.</strong> SHA-256 over normalized content (CRLF and BOM differences do not matter, everything else does) plus a chain over the order.</li>
  <li><strong>Rollback depending on your checkout.</strong> The database holds the revert script it will need.</li>
  <li><strong>Non-deterministic deployments.</strong> The SQL that runs is the SQL you reviewed, and a rendered-SQL hash records exactly what ran in each environment.</li>
  <li><strong>SQL Server as a second-class citizen.</strong> Native T-SQL batch splitting, a T-SQL linter, Entra ID, Always Encrypted, Azure SQL connection settings, TDS 8.0.</li>
</ul>
<p>Coming from another tool? See <a href="{BASE}/docs/baseline/">Existing databases</a> for adopting a database that is already in production.</p>
"""
    return Page("compare/", "Badger SQL Migrate compared with Flyway, DbUp, grate, EF, SSDT, Sqitch and Liquibase",
                "How Badger SQL Migrate compares with other database migration tools for SQL Server.", body,
                heading="How it compares", lede="Flyway, DbUp, grate, EF Core migrations, SSDT, Sqitch and Liquibase.", wide=True)


# ---------------------------------------------------------------- examples

def examples():
    sections = []

    def ex(anchor, title, intro, *blocks):
        sections.append((anchor, title, f'<h2 id="{anchor}">{title}</h2>{intro}{"".join(blocks)}'))

    ex("create-table", "Create a table", "<p>The basic shape of every change: deploy, the exact opposite in revert, and a verify script that raises an error if the deploy did not take effect. Verify runs inside the same transaction, so a failing check rolls the change back.</p>",
       sql('''CREATE TABLE dbo.Customer (
    Id int IDENTITY NOT NULL CONSTRAINT PK_Customer PRIMARY KEY,
    Name nvarchar(200) NOT NULL,
    Email nvarchar(320) NULL,
    CreatedAt datetime2(3) NOT NULL CONSTRAINT DF_Customer_CreatedAt DEFAULT (sysutcdatetime())
);

CREATE UNIQUE INDEX UX_Customer_Email ON dbo.Customer (Email) WHERE Email IS NOT NULL;''', "changes/create-customer/deploy.sql"),
       sql('''DROP TABLE dbo.Customer;''', "changes/create-customer/revert.sql"),
       sql('''IF OBJECT_ID(N'dbo.Customer', N'U') IS NULL
    THROW 50000, N'dbo.Customer was not created', 1;
IF INDEXPROPERTY(OBJECT_ID(N'dbo.Customer'), N'UX_Customer_Email', 'IsUnique') <> 1
    THROW 50000, N'UX_Customer_Email is missing or not unique', 1;''', "changes/create-customer/verify.sql"),
       sh('''bsm add create-customer --note "customer table"'''))

    ex("add-column", "Add a NOT NULL column to a table with data", "<p>Add it nullable, backfill, then tighten. Batches separated by <code>GO</code> run in order inside the change's transaction. Name constraints explicitly: system-generated names differ between servers.</p>",
       sql('''ALTER TABLE dbo.Customer ADD Status tinyint NULL;
GO
UPDATE dbo.Customer SET Status = 1 WHERE Status IS NULL;
GO
ALTER TABLE dbo.Customer ALTER COLUMN Status tinyint NOT NULL;
ALTER TABLE dbo.Customer ADD CONSTRAINT DF_Customer_Status DEFAULT (1) FOR Status;
ALTER TABLE dbo.Customer ADD CONSTRAINT CK_Customer_Status CHECK (Status IN (1, 2, 3));''', "changes/add-customer-status/deploy.sql"),
       sql('''ALTER TABLE dbo.Customer DROP CONSTRAINT CK_Customer_Status;
ALTER TABLE dbo.Customer DROP CONSTRAINT DF_Customer_Status;
ALTER TABLE dbo.Customer DROP COLUMN Status;''', "changes/add-customer-status/revert.sql"),
       code('''add-customer-status [create-customer] @2026-11-04T09:02Z alice''', "", "badger.plan (line added by bsm add -r create-customer)"))

    ex("rename", "Rename a column", "<p>Renames are reversible when you write both directions.</p>",
       sql('''EXEC sp_rename N'dbo.Customer.Email', N'EmailAddress', N'COLUMN';''', "changes/rename-customer-email/deploy.sql"),
       sql('''EXEC sp_rename N'dbo.Customer.EmailAddress', N'Email', N'COLUMN';''', "changes/rename-customer-email/revert.sql"),
       sql('''IF COL_LENGTH(N'dbo.Customer', N'EmailAddress') IS NULL
    THROW 50000, N'EmailAddress column missing', 1;''', "changes/rename-customer-email/verify.sql"))

    ex("data-migration", "Data migration with a reversible split", "<p>Move data into a new shape and keep the revert honest. For very large tables, batch the update.</p>",
       sql('''ALTER TABLE dbo.Customer ADD FirstName nvarchar(100) NULL, LastName nvarchar(100) NULL;
GO
DECLARE @batch int = 5000;
WHILE 1 = 1
BEGIN
    UPDATE TOP (@batch) dbo.Customer
       SET FirstName = LEFT(Name, CHARINDEX(N' ', Name + N' ') - 1),
           LastName  = LTRIM(SUBSTRING(Name, CHARINDEX(N' ', Name + N' ') + 1, 200))
     WHERE FirstName IS NULL;
    IF @@ROWCOUNT < @batch BREAK;
END''', "changes/split-customer-name/deploy.sql"),
       sql('''UPDATE dbo.Customer SET Name = CONCAT_WS(N' ', FirstName, NULLIF(LastName, N''));
ALTER TABLE dbo.Customer DROP COLUMN FirstName, LastName;''', "changes/split-customer-name/revert.sql"))

    ex("irreversible", "An irreversible change", "<p>Some changes cannot be undone, such as dropping data you no longer keep. Mark them <code>!irreversible</code>: no <code>revert.sql</code> is needed and <code>bsm revert</code> stops with a clear message instead of guessing. Archiving first is good manners.</p>",
       sql('''SELECT * INTO archive.LegacyOrder_20261103 FROM dbo.LegacyOrder;
DROP TABLE dbo.LegacyOrder;''', "changes/drop-legacy-orders/deploy.sql"),
       sh('''bsm add drop-legacy-orders --irreversible'''))

    ex("notx", "Statements that cannot run in a transaction", "<p>SQL Server refuses some statements inside a transaction (<code>ALTER DATABASE</code>, full-text DDL, <code>RECONFIGURE</code>, resumable index operations). The linter catches them; mark the change <code>!notx</code> and make the script safe to re-run, because a failure halfway leaves partial work.</p>",
       sql('''IF NOT EXISTS (SELECT 1 FROM sys.fulltext_catalogs WHERE name = N'ftCatalog')
    CREATE FULLTEXT CATALOG ftCatalog;
GO
IF NOT EXISTS (SELECT 1 FROM sys.fulltext_indexes WHERE object_id = OBJECT_ID(N'dbo.Product'))
    CREATE FULLTEXT INDEX ON dbo.Product (Name, Description) KEY INDEX PK_Product ON ftCatalog;''', "changes/product-fulltext/deploy.sql"),
       sql('''IF EXISTS (SELECT 1 FROM sys.fulltext_indexes WHERE object_id = OBJECT_ID(N'dbo.Product'))
    DROP FULLTEXT INDEX ON dbo.Product;
GO
IF EXISTS (SELECT 1 FROM sys.fulltext_catalogs WHERE name = N'ftCatalog')
    DROP FULLTEXT CATALOG ftCatalog;''', "changes/product-fulltext/revert.sql"),
       code('''product-fulltext [create-product] !notx''', "", "badger.plan"))

    ex("repeatable", "Views, procedures and functions (repeatables)", "<p>Programmable objects live in <code>repeatable/</code> and use <code>CREATE OR ALTER</code>. Badger re-applies one only when its hash changes, after all changes, in dependency order. Declare dependencies with a header.</p>",
       sql('''CREATE OR ALTER VIEW dbo.vActiveCustomer
AS
SELECT Id, Name, EmailAddress
FROM dbo.Customer
WHERE Status = 1;''', "repeatable/views/vActiveCustomer.sql"),
       sql('''-- badger:requires views/vActiveCustomer.sql
CREATE OR ALTER PROCEDURE dbo.usp_FindCustomer
    @search nvarchar(200)
AS
BEGIN
    SET NOCOUNT ON;
    SELECT TOP (50) Id, Name, EmailAddress
    FROM dbo.vActiveCustomer
    WHERE Name LIKE @search + N'%'
    ORDER BY Name;
END''', "repeatable/procedures/usp_FindCustomer.sql"))

    ex("seed", "Seed data for one environment", "<p>Scripts under <code>environments/&lt;name&gt;/</code> run only in that environment, after the shared repeatables. Write them idempotently; they re-run when they change.</p>",
       sql('''-- badger:requires views/vActiveCustomer.sql
MERGE dbo.Customer AS t
USING (VALUES
    (N'Ada Lovelace', N'ada@example.com', 1),
    (N'Grace Hopper', N'grace@example.com', 1)
) AS s (Name, EmailAddress, Status)
    ON t.EmailAddress = s.EmailAddress
WHEN NOT MATCHED THEN
    INSERT (Name, EmailAddress, Status) VALUES (s.Name, s.EmailAddress, s.Status);''', "environments/dev/seed-customers.sql"))

    ex("variables", "Environment-specific names with variables", "<p>The same script targets <code>calc_test</code> in test and <code>calc_prod</code> in prod. Built-ins like <code>{env}</code> and <code>{db}</code> are always available; your own variables live in <code>badger.yaml</code>.</p>",
       yaml('''variables:
  schemas:
    calc: calc_{env}
  AppRole: app_reader
environments:
  test:
    database: AppTest
  prod:
    database: AppProd
    variables:
      AppRole: app_reader_prod
      linkedServer: REPORTING01''', "badger.yaml"),
       sql('''CREATE SCHEMA [{schemas.calc}];
GO
CREATE TABLE [{schemas.calc}].[table1] (
    Id int NOT NULL PRIMARY KEY,
    Note nvarchar(100) NOT NULL DEFAULT (N'{{env}} stays literal')
);
GO
CREATE ROLE [{AppRole}];
GRANT SELECT ON SCHEMA::[{schemas.calc}] TO [{AppRole}];
PRINT 'deployed {change} to {env} ({db}) with bsm {badger.version}';''', "changes/create-calc-schema/deploy.sql"),
       sh('''bsm render create-calc-schema -e prod    # prints the exact SQL prod will receive
bsm vars -e prod                          # every variable, its value and where it came from'''),
       sql('''CREATE SCHEMA [calc_prod];
GO
CREATE TABLE [calc_prod].[table1] (
    Id int NOT NULL PRIMARY KEY,
    Note nvarchar(100) NOT NULL DEFAULT (N'{env} stays literal')
);
GO
CREATE ROLE [app_reader_prod];
GRANT SELECT ON SCHEMA::[calc_prod] TO [app_reader_prod];
PRINT 'deployed create-calc-schema to prod (AppProd) with bsm 0.1.0';''', "output of bsm render -e prod"))

    ex("security", "Logins, users and grants that differ per environment", "<p>Principals usually differ per environment. Keep the shape in the change and the names in variables. Secrets come from the environment, never from the file.</p>",
       yaml('''environments:
  prod:
    variables:
      apiIdentity: myapp-api-prod          # Entra managed identity name
  test:
    variables:
      apiIdentity: myapp-api-test''', "badger.yaml"),
       sql('''CREATE USER [{apiIdentity}] FROM EXTERNAL PROVIDER;
ALTER ROLE db_datareader ADD MEMBER [{apiIdentity}];
GRANT EXECUTE ON SCHEMA::dbo TO [{apiIdentity}];''', "changes/grant-api-identity/deploy.sql"),
       sql('''REVOKE EXECUTE ON SCHEMA::dbo FROM [{apiIdentity}];
ALTER ROLE db_datareader DROP MEMBER [{apiIdentity}];
DROP USER [{apiIdentity}];''', "changes/grant-api-identity/revert.sql"))

    ex("always-encrypted", "An Always Encrypted column", "<p>The change names a column encryption key; each environment decides which column master key protects it. Badger creates the keys before the change runs.</p>",
       yaml('''environments:
  test:
    alwaysEncrypted:
      columnMasterKeys:
        CMK_Test: { keyStore: AZURE_KEY_VAULT, keyPath: "https://myapp-test-kv.vault.azure.net/keys/cmk/1a2b3c" }
      columnEncryptionKeys:
        CEK_Customer: { columnMasterKey: CMK_Test }
  prod:
    alwaysEncrypted:
      columnMasterKeys:
        CMK_Prod_2026: { keyStore: AZURE_KEY_VAULT, keyPath: "https://myapp-prod-kv.vault.azure.net/keys/cmk/9f8e7d" }
      columnEncryptionKeys:
        CEK_Customer: { columnMasterKey: CMK_Prod_2026 }''', "badger.yaml"),
       sql('''ALTER TABLE dbo.Customer ADD TaxId char(11) COLLATE Latin1_General_BIN2
    ENCRYPTED WITH (
        COLUMN_ENCRYPTION_KEY = CEK_Customer,
        ENCRYPTION_TYPE = Deterministic,
        ALGORITHM = 'AEAD_AES_256_CBC_HMAC_SHA_256') NULL;''', "changes/add-customer-taxid/deploy.sql"),
       sql('''ALTER TABLE dbo.Customer DROP COLUMN TaxId;''', "changes/add-customer-taxid/revert.sql"),
       sh('''bsm env status -e prod      # keys: in sync, to create, to rotate, conflicting
bsm deploy -e prod           # creates CMK_Prod_2026 and CEK_Customer, then runs the change'''))

    ex("config", "A complete badger.yaml", "<p>A realistic configuration: a local container, Azure test and production, a template for shared settings, and production rules.</p>",
       yaml('''provider: sqlserver
defaultEnvironment: local

preprocessor:
  unknown: error                    # unknown {name} in a script is an error

variables:
  AppRole: app_reader
  schemas:
    reporting: reporting_{env}

environments:
  _azure:                           # template: shared by test and prod through extends
    database: App
    tls: { mode: strict }
    authentication: entraInteractive
    entra:
      tenantId: 7f3c1e2a-0000-0000-0000-000000000000
    connectionProperties:
      Connect Retry Count: 3
      Connect Retry Interval: 10

  local:
    host: localhost
    database: App
    authentication: password
    user: sa
    password: ${env:BADGER_LOCAL_PASSWORD}
    tls: { mode: mandatory, trustServerCertificate: true }

  test:
    extends: _azure
    description: Shared test database
    host: myapp-test.database.windows.net

  prod:
    extends: _azure
    description: Production
    host: myapp-prod.database.windows.net
    protected: true                 # type "prod" (or pass --yes) to deploy
    allowRevert: false              # fix forward
    ordering: strict                # exact plan order only
    authentication: entraWorkloadIdentity
    variables:
      AppRole: app_reader_prod
    connectionProperties:
      Command Timeout: 600''', "badger.yaml"))

    ex("plan", "A plan after two developers merged", "<p>Alice and Bob each appended a line on their branches. Git's union merge driver (set up by <code>bsm init</code>) kept both lines without a conflict.</p>",
       code('''# Badger SQL Migrate plan: one change per line, in deploy order.

create-customer                                  @2026-11-03T14:10Z brock  # customer table
add-customer-status [create-customer]            @2026-11-03T16:42Z brock
@release-1.0
rename-customer-email [create-customer]          @2026-11-04T09:02Z alice
add-order-table [create-customer]                @2026-11-04T09:15Z bob
product-fulltext [create-product] !notx          @2026-11-05T11:30Z bob
drop-legacy-orders !irreversible                 @2026-11-06T08:00Z alice''', "", "badger.plan"),
       sh('''bsm plan check     # unique names, dependencies, folders, lint, variables in every environment'''))

    ex("github-actions", "GitHub Actions", "<p>Check on every pull request; deploy to test on merge; deploy to production with workload identity federation (no secrets).</p>",
       yaml('''name: database
on:
  pull_request:
  push:
    branches: [main]

permissions:
  id-token: write      # Entra workload identity federation
  contents: read

jobs:
  check:
    runs-on: ubuntu-latest
    steps:
      - uses: actions/checkout@v4
      - uses: actions/setup-dotnet@v4
        with: { dotnet-version: 10.0.x }
      - run: dotnet tool install --global Badger.SqlMigrate
      - run: bsm plan check -C db

  deploy:
    if: github.ref == 'refs/heads/main'
    needs: check
    runs-on: ubuntu-latest
    environment: production
    steps:
      - uses: actions/checkout@v4
      - uses: actions/setup-dotnet@v4
        with: { dotnet-version: 10.0.x }
      - run: dotnet tool install --global Badger.SqlMigrate
      - uses: azure/login@v2
        with:
          client-id: ${{ vars.AZURE_CLIENT_ID }}
          tenant-id: ${{ vars.AZURE_TENANT_ID }}
          allow-no-subscriptions: true
      - run: bsm deploy -C db -e test --json
      - run: bsm deploy -C db -e prod --yes --json
      - run: bsm verify -C db -e prod''', ".github/workflows/database.yml"),
       yaml('''  prod:
    extends: _azure
    authentication: entraAzureCli      # uses the azure/login session above''', "badger.yaml (prod for this pipeline)"))

    ex("azure-pipelines", "Azure Pipelines", "<p>With a workload-identity service connection, use <code>entraAzurePipelines</code>, or run inside an AzureCLI task with <code>entraAzureCli</code>.</p>",
       yaml('''steps:
  - task: UseDotNet@2
    inputs: { packageType: sdk, version: 10.0.x }
  - script: dotnet tool install --global Badger.SqlMigrate
  - script: bsm deploy -C db -e prod --yes
    env:
      SYSTEM_ACCESSTOKEN: $(System.AccessToken)''', "azure-pipelines.yml"),
       yaml('''  prod:
    extends: _azure
    authentication: entraAzurePipelines
    entra:
      tenantId: 7f3c1e2a-0000-0000-0000-000000000000
      clientId: 0a1b2c3d-0000-0000-0000-000000000000
      serviceConnectionId: 5e6f7a8b-0000-0000-0000-000000000000
      systemAccessToken: ${env:SYSTEM_ACCESSTOKEN}''', "badger.yaml"))

    toc = '<div class="toc"><strong>Examples</strong><ul>' + "".join(f'<li><a href="#{a}">{t}</a></li>' for a, t, _ in sections) + "</ul></div>"
    body = toc + "".join(s for _, _, s in sections) + f'<p>A runnable sample project is in the repository under <a href="https://github.com/lonebadger-tech/badger-sql-migrate/tree/dev/samples/demo-db">samples/demo-db</a>. For every option, see the <a href="{BASE}/docs/">documentation</a>.</p>'
    return Page("examples/", "Badger SQL Migrate examples", "Example migration scripts, plans and configuration for Badger SQL Migrate.", body,
                heading="Examples", lede="Copy-ready scripts for common SQL Server changes, plus configuration and CI pipelines.", wide=True)
