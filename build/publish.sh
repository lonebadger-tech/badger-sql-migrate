#!/usr/bin/env bash
# Builds release artifacts for Badger Trek into ./artifacts:
#   - self-contained single-file executables for each runtime (no .NET install needed)
#   - a portable framework-dependent build (any OS with the .NET 10 runtime)
#   - the .NET global tool package (Badger.Trek.<version>.nupkg)
#   - SHA256SUMS for everything
# Usage: build/publish.sh [version]   (default: version from Directory.Build.props)
set -euo pipefail

root="$(cd "$(dirname "$0")/.." && pwd)"
cd "$root"
version="${1:-$(sed -n 's:.*<Version>\(.*\)</Version>.*:\1:p' Directory.Build.props)}"
out="$root/artifacts"
cli="src/Badger.Trek.Cli/Badger.Trek.Cli.csproj"
rids=(osx-arm64 osx-x64 linux-x64 linux-arm64 linux-musl-x64 win-x64 win-arm64)

rm -rf "$out" && mkdir -p "$out"
work="$(mktemp -d)"
trap 'rm -rf "$work"' EXIT

for rid in "${rids[@]}"; do
  echo "== $rid"
  dotnet publish "$cli" -c Release -r "$rid" -o "$work/$rid" -p:Version="$version" \
    --self-contained true -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true \
    -p:EnableCompressionInSingleFile=true -p:DebugType=none -p:GenerateDocumentationFile=false -v quiet
  cp LICENSE README.md THIRD-PARTY-NOTICES.md "$work/$rid/"
  name="badger-trek-$version-$rid"
  if [[ "$rid" == win-* ]]; then
    (cd "$work/$rid" && zip -q -X "$out/$name.zip" trek.exe LICENSE README.md THIRD-PARTY-NOTICES.md)
  else
    chmod +x "$work/$rid/trek"
    tar -C "$work/$rid" -czf "$out/$name.tar.gz" trek LICENSE README.md THIRD-PARTY-NOTICES.md
  fi
done

echo "== portable (framework-dependent)"
dotnet publish "$cli" -c Release -o "$work/portable" -p:Version="$version" --self-contained false -p:UseAppHost=false -p:DebugType=none -v quiet
cp LICENSE README.md THIRD-PARTY-NOTICES.md "$work/portable/"
printf '#!/bin/sh\nexec dotnet "$(dirname "$0")/trek.dll" "$@"\n' > "$work/portable/trek" && chmod +x "$work/portable/trek"
printf '@dotnet "%%~dp0trek.dll" %%*\r\n' > "$work/portable/trek.cmd"
(cd "$work/portable" && zip -q -r -X "$out/badger-trek-$version-portable.zip" .)

echo "== dotnet tool"
dotnet pack "$cli" -c Release -o "$out" -p:Version="$version" -p:DebugType=none -v quiet

(cd "$out" && shasum -a 256 * > SHA256SUMS)
ls -lh "$out"
