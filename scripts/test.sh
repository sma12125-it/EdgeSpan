#!/usr/bin/env bash
set -euo pipefail
root="$(cd "$(dirname "$0")/.." && pwd)"
dotnet test "$root/src/EdgeSpan.Core.Tests/EdgeSpan.Core.Tests.csproj" -c Release
(cd "$root/relay" && go test ./...)
(cd "$root/android" && ./gradlew :protocol:test :server-core:test --no-daemon)
dotnet test "$root/src/EdgeSpan.Windows.Tests/EdgeSpan.Windows.Tests.csproj" -c Release
dotnet build "$root/src/EdgeSpan.Sim/EdgeSpan.Sim.csproj" -c Release
