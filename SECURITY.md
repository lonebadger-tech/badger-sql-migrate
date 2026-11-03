# Security policy

## Reporting a vulnerability

Please report security problems privately, not in a public issue:

- Use GitHub's [private vulnerability reporting](https://github.com/lonebadger-tech/badger-sql-migrate/security/advisories/new), or
- email [support@lonebadger.tech](mailto:support@lonebadger.tech) with "Security" in the subject.

Include the version (`badger --version`), what an attacker could do, and steps to reproduce. We'll acknowledge the report within 5 business days and keep you updated until it's fixed. With your permission we'll credit you in the advisory.

## Supported versions

Security fixes go into the latest release. Upgrade with `dotnet tool update --global Badger.SqlMigrate` or download the latest build from [the download page](https://lonebadger.tech/badger-sql-migrate/download/).

## Scope

Badger SQL Migrate handles database credentials, Entra ID tokens, SSH keys and Always Encrypted keys. Reports about any of these leaking (into logs, `--json` output, the journal tables or saved sign-ins under `~/.badger`), about a migration running against the wrong environment, or about the hash certification being bypassed are especially welcome.
