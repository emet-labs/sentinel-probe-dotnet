# .NET Probe SDK

The .NET Probe SDK (issue #30), ported module-for-module from the Go reference SDK
(`sdk/go/`, issue #33). It implements against the locked proto contract (#27, closed).

The SDK:

- connects to Sentinel and holds the versioned Event Filter for its source (`Client/`)
- wraps the OpenTelemetry .NET SDK to produce `Activity` spans the OTLP Adapter
  normalizes (`Emission/`)
- applies attribute-level Filter projection before shipping — relevance, never
  sampling (`Filter/`, ADR-0006)
- enforces `ASK_AND_BLOCK` specs via the decision endpoint (`Enforcement/`, the
  ADR-0023 bounded fragment)
- reads source-tier from deployment config (`Config/`, ADR-0022, never hard-coded)

## Deferred acceptance criterion: SagaShop integration

The live SagaShop integration test (acceptance criterion 5) is deferred. No
`SentinelDecisionService` is served anywhere in this repository — the same blocker
documented in `sdk/go/README.md`. The SDK and its xUnit test suite against the proto
contract are buildable and fully green now.

## Generated proto types

The C# proto types under `gen/` are hand-written in the protoc-gen-csharp style
(`Google.Protobuf.CodedOutputStream` / `CodedInputStream` wire codec) because `buf`
is not available in this environment. They are **committed** (not gitignored) and
include `buf.gen.dotnet.yaml` plus `tools/generate-dotnet-sdk.sh` as the future
regeneration path. `buf.gen.dotnet.yaml` uses the `managed:` mode (S2 fix), not
`generated:`.

## Build and test

```
dotnet build sdk/dotnet/Sentinel.Probe.Sdk.sln
dotnet test  sdk/dotnet/Sentinel.Probe.Sdk.sln
```

Both pass on .NET 10.0.302 (pinned via `global.json`). The test suite uses xUnit,
is hermetic (ADR-0019: no network, no wall clock at test-execution time), and
covers 171 cases mirroring the Go `*_test.go` files.

## Key design decisions

- **`Activity.TagObjects`** (S1 fix): span→event conversion reads `Activity.TagObjects`
  rather than `Activity.Tags` so all attribute types round-trip.
- **`System.Numerics.BigInteger`** for Int128: the proto `Int128` (two `int64` words)
  is decoded into `BigInteger` via the `Int128Codec` helper, avoiding any external
  dependency.
- **Hand-written Connect unary over `HttpClient`**: the decision endpoint uses a
  straight `POST` with `Content-Type: application/proto` rather than a gRPC
  channel, mirroring the Go SDK's `Connect` unary-over-HTTP approach.
- **`ProbeSpanProcessor : BaseProcessor<Activity>`**: the OTel span processor hooks
  `OnEnd` to convert and emit events, with `Activity.TagObjects` for tag access.
- **Pinned NuGet versions**: all package references pin exact versions
  (`Google.Protobuf` 3.33.4, `OpenTelemetry`/`OpenTelemetry.Api` 1.17.0, `xunit`
  2.9.3, `xunit.runner.visualstudio` 3.1.5, `Microsoft.NET.Test.Sdk` 18.8.1).
- **`RestorePackagesWithLockFile=true`**: `packages.lock.json` files are committed
  per project for reproducible restores, analogous to `go.sum`.
- **`gen/**/*.cs` excluded from `TreatWarningsAsErrors`**: generated code has
  `WarningLevel=0` so nullable annotations and other analyzer noise don't break the
  build.
