#!/usr/bin/env bash
# Uploads ./artifacts (from build/publish.sh) to the public downloads container:
#   https://lonebadgerdownloads.blob.core.windows.net/badger-sql-migrate/v<version>/<file>
# The website's download links (docs/website/build.py DOWNLOAD_BASE) point there. Needs `az login`.
# Usage: build/upload-release.sh [version]   (default: version from Directory.Build.props)
set -euo pipefail

root="$(cd "$(dirname "$0")/.." && pwd)"
cd "$root"
version="${1:-$(sed -n 's:.*<Version>\(.*\)</Version>.*:\1:p' Directory.Build.props)}"
[[ -f artifacts/SHA256SUMS ]] || { echo "Run build/publish.sh first" >&2; exit 1; }
grep -q -- "-$version[-.]" artifacts/SHA256SUMS || { echo "artifacts/ isn't version $version" >&2; exit 1; }

az storage blob upload-batch --subscription 19a38b65-1dbb-430f-a4ef-9cec80a739fa \
  --account-name lonebadgerdownloads --auth-mode key --destination badger-sql-migrate \
  --destination-path "v$version" --source artifacts --overwrite --only-show-errors \
  --content-cache-control "public, max-age=86400" -o none
echo "Uploaded to https://lonebadgerdownloads.blob.core.windows.net/badger-sql-migrate/v$version/"
