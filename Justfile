# Tasks for this published mirror of the Sentinel .NET Probe SDK (#160, ADR-0032).
#
# Run inside the Devbox shell: `devbox install` once, then `devbox shell`. These
# recipes reuse the canonical gate names of Sentinel's source repository, scoped to
# this one language. NuGet locked mode: the committed packages.lock.json files are
# the sole dependency graph accepted, so restore uses --locked-mode and every
# consumer suppresses its implicit restore.

build:
    dotnet restore --locked-mode Sentinel.Probe.Sdk.sln
    dotnet build --no-restore Sentinel.Probe.Sdk.sln

test: build
    dotnet test --no-build --no-restore Sentinel.Probe.Sdk.sln

lint:
    dotnet format analyzers --no-restore --verify-no-changes --severity warn Sentinel.Probe.Sdk.sln

fmt-check:
    dotnet format analyzers --no-restore --verify-no-changes --severity warn Sentinel.Probe.Sdk.sln
