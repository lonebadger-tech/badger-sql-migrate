---
name: deploy
description: Release Badger SQL Migrate - push dev, tag vX.Y.Z so the release workflow builds and publishes binaries and the NuGet tool, mirror the release to the downloads container, and publish the regenerated website pages.
disable-model-invocation: true
argument-hint: "[version, default: <Version> in Directory.Build.props]"
---

# Release Badger SQL Migrate

Releases the version in `Directory.Build.props` (or `$ARGUMENTS` if given; it must equal that file). One build
is published everywhere: the GitHub release workflow builds the binaries, nuget.org gets the tool from the same
run, `build/mirror-release.sh` copies those exact files to the Azure downloads container, and the website is
regenerated from them so its SHA-256 hashes match. Never upload a local `build/publish.sh` build for a tag.

Steps 3 onwards publish things that cannot be taken back (a nuget.org version is permanent). Do steps 1-2, show
the user the summary, and continue only after they confirm. Stop and report on any failure; do not retry a
publish step blindly.

## 1. Preflight (this repo)

- On `dev`, nothing uncommitted except `.claude/`: `git status --short`.
- `VERSION` (call it V) agrees in `Directory.Build.props` `<Version>` and `docs/website/build.py` `VERSION`.
- `docs/website/pages.py` `releases()` has a section for V describing what changed; `RELEASE_DATE` in
  `build.py` is right.
- Tag `vV` does not exist locally or on origin: `git tag -l vV`, `git ls-remote --tags origin vV`.
- `gh release view vV` fails (no release yet) and https://api.nuget.org/v3-flatcontainer/badger.sqlmigrate/index.json
  does not list V.
- `dotnet test` passes. The SQL Server tests need Docker running (Testcontainers); if they skip, say so.
- `az account show` works (needed by the mirror step). If not, ask the user to run `az login`.
- `../badger-website` exists, is on `dev`, and is clean apart from unrelated work (leave that alone).

## 2. Summary for the user

Show V, the commits since the previous tag (`git log --oneline $(git describe --tags --abbrev=0)..dev`), and
what will happen: push dev, push tag vV (release workflow: tests, binaries, nuget.org, GitHub release), mirror
to the downloads container, website PR. Ask to proceed.

## 3. Push dev and wait for CI

```bash
git push origin dev
gh run list --branch dev --workflow ci.yml --limit 1
gh run watch <id> --exit-status
```

## 4. Tag and release

```bash
git tag -a vV -m "Badger SQL Migrate V"
git push origin vV
gh run list --workflow release.yml --limit 1
gh run watch <id> --exit-status        # run in the background; takes ~4 minutes
```

Then check:
- `gh release view vV --json assets -q '.assets[].name'` lists the 8 platform archives
  (osx-arm64, osx-x64, linux-x64, linux-arm64, linux-musl-x64, win-x64, win-arm64, portable), the nupkg and SHA256SUMS.
- nuget.org lists V (indexing can take several minutes; check again rather than republish).

## 5. Mirror to the downloads container

```bash
build/mirror-release.sh V
curl -sf -o /dev/null -w "%{http_code}\n" https://lonebadgerdownloads.blob.core.windows.net/badger-sql-migrate/vV/SHA256SUMS
```

The script downloads the GitHub release into `artifacts/`, checks SHA256SUMS, and uploads. Leave `artifacts/` in
place: the website build reads sizes and hashes from it.

## 6. Website

The site lives in `../badger-website` (`site/`), deploys on every push to its `dev`, and takes changes through a PR.

```bash
cd ../badger-website
git switch -c migrate-V-release origin/dev
python3 ../badger-sql-migrate/docs/website/build.py site
git add site/badger-sql-migrate && git commit -m "Badger SQL Migrate: V release pages and download hashes"
git push -u origin migrate-V-release
gh pr create --base dev --title "Badger SQL Migrate V" --body "Release pages and download hashes for the vV release build."
```

End commits and the PR body with the attribution lines from the session's system reminder. Spot-check that the
download page shows V and the hashes from `artifacts/SHA256SUMS`. Merging the PR deploys the site: give the user
the PR link and merge only if they say so. After the merge, check
https://www.lonebadger.tech/badger-sql-migrate/releases/ shows V and one download link returns 200.

## 7. Report

The release URL, the nuget.org version, the mirror URL, the website PR (and whether it is merged and live), and
anything that was skipped or still pending.
