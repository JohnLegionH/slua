#!/bin/sh
# Copyright (c) 2026 Legion Builds
# Licensed under the MIT License; see LICENSE.txt in the repository root.
#
# Builds libslua_shim.so with cmake and g++. At most 2 compile jobs.
# Usage: build-linux.sh <repo root> <build dir>
set -e
cmake -S "$1/ngc-host" -B "$2" -G "Unix Makefiles" -DCMAKE_BUILD_TYPE=Release
cmake --build "$2"
