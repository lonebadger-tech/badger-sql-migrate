#!/usr/bin/env bash
# Builds release artifacts for Badger SQL Migrate into ./artifacts:
#   - self-contained single-file executables for each runtime (no .NET install needed)
#   - a portable framework-dependent build (any OS with the .NET 10 runtime)
#   - the .NET global tool package (Badger.SqlMigrate.<version>.nupkg)
#   - SHA256SUMS for everything
# Usage: build/publish.sh [version]   (default: version from Directory.Build.props)
set -euo pipefail

root="$(cd "$(dirname "$0")/.." && pwd)"
cd "$root"
version="${1:-$(sed -n 's:.*<Version>\(.*\)</Version>.*:\1:p' Directory.Build.props)}"
out="$root/artifacts"
cli="src/Badger.SqlMigrate.Cli/Badger.SqlMigrate.Cli.csproj"
rids=(osx-arm64 osx-x64 linux-x64 linux-arm64 linux-musl-x64 win-x64 win-arm64)

rm -rf "$out" && mkdir -p "$out"
work="$(mktemp -d)"
trap 'rm -rf "$work"' EXIT

for rid in "${rids[@]}"; do
  echo "== $rid"
  dotnet publish "$cli" -c Release -r "$rid" -o "$work/$rid" -p:Version="$version" \
    --self-contained true -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true \
    -p:EnableCompressionInSingleFile=true -p:DebugType=none -p:GenerateDocumentationFile=false -v quiet
  cp LICENSE README.md "$work/$rid/"
  name="badger-sql-migrate-$version-$rid"
  if [[ "$rid" == win-* ]]; then
    (cd "$work/$rid" && zip -q -X "$out/$name.zip" bsm.exe LICENSE README.md)
  else
    chmod +x "$work/$rid/bsm"
    tar -C "$work/$rid" -czf "$out/$name.tar.gz" bsm LICENSE README.md
  fi
done

echo "== portable (framework-dependent)"
dotnet publish "$cli" -c Release -o "$work/portable" -p:Version="$version" --self-contained false -p:UseAppHost=false -p:DebugType=none -v quiet
cp LICENSE README.md "$work/portable/"
printf '#!/bin/sh\nexec dotnet "$(dirname "$0")/bsm.dll" "$@"\n' > "$work/portable/bsm" && chmod +x "$work/portable/bsm"
printf '@dotnet "%%~dp0bsm.dll" %%*\r\n' > "$work/portable/bsm.cmd"
(cd "$work/portable" && zip -q -r -X "$out/badger-sql-migrate-$version-portable.zip" .)

echo "== dotnet tool"
dotnet pack "$cli" -c Release -o "$out" -p:Version="$version" -p:DebugType=none -v quiet

(cd "$out" && shasum -a 256 * > SHA256SUMS)
ls -lh "$out"
