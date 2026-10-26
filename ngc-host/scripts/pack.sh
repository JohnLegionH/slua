#!/bin/sh
# Copyright (c) 2026 Legion Builds
# Licensed under the MIT License; see LICENSE.txt in the repository root.
#
# Packs the NuGet package from the two native libraries the CI builds produce.
# Usage: pack.sh <repo root> <folder with slua_shim.dll> <folder with libslua_shim.so> <output folder> [dotnet pack arguments]
# The package is written to the output folder; nothing is published.
set -e
REPO="$1"; WIN="$2"; LINUX="$3"; OUT="$4"
shift 4
dotnet pack "$REPO/ngc-host/dotnet/SluaHost/SluaHost.csproj" -c Release \
    -p:WinNativeDir="$WIN" -p:LinuxNativeDir="$LINUX" -o "$OUT" -nodeReuse:false -p:UseSharedCompilation=false "$@"
