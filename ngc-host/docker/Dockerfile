# Copyright (c) 2026 Legion Builds
# Licensed under the MIT License; see LICENSE.txt in the repository root.
#
# Build and test image for the shim on Linux: a .NET 10 SDK with cmake and g++.
FROM mcr.microsoft.com/dotnet/sdk:10.0
RUN apt-get update \
 && apt-get install -y --no-install-recommends cmake g++ make git ca-certificates \
 && rm -rf /var/lib/apt/lists/*
