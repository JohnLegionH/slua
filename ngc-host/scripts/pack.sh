#!/bin/sh
# Copyright (c) 2026 Legion Builds
# Licensed under the MIT License; see LICENSE.txt in the repository root.
#
# Packs the native-only NuGet package (UtopiaSkye.SLua) from a release bundle (see make-bundle.py).
# Every file of the bundle is checked against its SHA256SUMS.txt first; the package version is the
# bundle's version.
# Usage: pack.sh <repo root> <bundle folder> <output folder> [dotnet pack arguments]
# The package is written to the output folder; nothing is published.
set -e
fwd() { printf %s "$1" | tr "\\" /; }   # a Windows path arrives with backslashes
REPO=$(fwd "$1"); BUNDLE=$(fwd "$2"); OUT=$(fwd "$3")
shift 3
while read -r sum name; do
    [ -n "$name" ] || continue
    got=$(sha256sum < "$BUNDLE/$name" | cut -d " " -f 1) || got=missing
    [ "$got" = "$sum" ] || { echo "pack: $name does not match SHA256SUMS.txt in $BUNDLE" >&2; exit 1; }
done < "$BUNDLE/SHA256SUMS.txt"
ZIP=$(ls "$BUNDLE"/slua-native-win-x64-*.zip)
VERSION=${ZIP##*/slua-native-win-x64-}; VERSION=${VERSION%.zip}
STAGE="$OUT/stage"
mkdir -p "$STAGE/win" "$STAGE/linux"
unzip -q -o "$ZIP" -d "$STAGE/win"
tar -xzf - -C "$STAGE/linux" < "$BUNDLE/slua-native-linux-x64-$VERSION.tar.gz"
unzip -q -o "$BUNDLE/licenses.zip" -d "$STAGE"   # gives $STAGE/licenses, which the package carries as licenses/
dotnet pack "$REPO/ngc-host/package/UtopiaSkye.SLua.csproj" -c Release \
    -p:Version="$VERSION" -p:BundleDir="$BUNDLE" -p:LicensesDir="$STAGE/licenses" -p:WinNativeDir="$STAGE/win" -p:LinuxNativeDir="$STAGE/linux" \
    --artifacts-path "$STAGE/art" -o "$OUT" -nodeReuse:false -p:UseSharedCompilation=false "$@"
