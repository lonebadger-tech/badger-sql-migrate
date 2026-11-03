#!/usr/bin/env bash
# Copies the files the release workflow attached to GitHub release v<version> into ./artifacts, then
# uploads them to the downloads container (build/upload-release.sh). The website, nuget.org and the
# GitHub release then all serve the same build, so every published SHA-256 matches. Builds aren't
# byte-reproducible across machines, so never upload a local build/publish.sh run for a tagged release.
# Then regenerate the website: python3 docs/website/build.py ../badger-website/site
# Usage: build/mirror-release.sh [version]   (default: version from Directory.Build.props)
set -euo pipefail

root="$(cd "$(dirname "$0")/.." && pwd)"
cd "$root"
version="${1:-$(sed -n 's:.*<Version>\(.*\)</Version>.*:\1:p' Directory.Build.props)}"

rm -rf artifacts && mkdir artifacts
gh release download "v$version" --repo lonebadger-tech/badger-sql-migrate --dir artifacts
(cd artifacts && shasum -a 256 --check --quiet SHA256SUMS)
build/upload-release.sh "$version"
