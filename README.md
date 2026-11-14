# .NET Probe SDK

The .NET Probe SDK (issue #30), ported module-for-module from the Go reference SDK
(`sdk/go/`, issue #33). It implements against the locked proto contract (#27, closed).

The SDK:

- connects to Sentinel and holds the versioned Event Filter for its source (`Client/`)
- wraps the OpenTelemetry .NET SDK to produce `Activity` spans the OTLP Adapter
  normalizes (`Emission/`)
- applies attribute-level Filter projection before shipping — relevance, never
  sampling (`Filter/`, ADR-0006)
- exports ended spans and producer completeness as OTLP/HTTP protobuf, and
  receives Verdict, EventFilter, and DecideResponse on `127.0.0.1` (`Otlp/`,
  the #398 Probe-wire exception). The gate waits on that log. Connect is not
  the live path.
- reads source-tier from deployment config (`Config/`, ADR-0022, never hard-coded)

## OTLP wire

`OtlpExporter` posts `ExportTraceServiceRequest` bytes to `/v1/traces` and a
`ProducerCompletenessSignal` log to `/v1/logs`, with `x-sentinel-source-handle`.
A missing predecessor id fails that export. `OtlpLogReceiver` binds `127.0.0.1`
and serves `POST /v1/logs`. `harness-probe-sdk-dotnet` builds `sentinel-periphery`
and runs the sample CAT-1 charge/ship round trip against it.

## Sequence numbers and completeness

Create one `Emission.SequenceAllocator` per source and add
`new SequenceStampingProcessor(sequence)` to the host's TracerProvider. It allocates
at Activity start and stamps `sentinel.sequence.epoch` and `sentinel.sequence.number`;
an Activity already carrying both keeps its reserved slot. This SDK has no exporting
processor: read the slot with `SequenceStampingProcessor.TryGetSlot`, build a
`SpanExport` using `slot.Epoch` and `slot.Number`, and call
`OtlpExporter.ExportSpansCheckedAsync`. Mark it exported only on HTTP 2xx with zero
`partial_success.rejected_spans`; on a refusal, error or undecodable response use
`sequence.MarkFailed(slot)`. A host Filter drop uses `MarkFiltered(slot, filterEpoch)`.

`Otlp.CompletenessHeartbeat` sends the allocator's declarations on `/v1/logs`:

```csharp
using var heartbeat = new CompletenessHeartbeat(
    sequence,
    (signal, ct) => exporter.ExportCompletenessAsync(sourceHandle, signal, ct),
    filterEpoch: filterStore.Epoch);
heartbeat.Start();
```

The default interval is 15 seconds; `interval` accepts 1 s to 59.999 s or
`TimeSpan.Zero` for `ReportAsync` only. `timeProvider` and `send` inject time,
scheduling and transport. An absent Filter is acknowledged as epoch 0.
`DeclareIdle()` declares intentional idle; the next allocation or `DeclareActive()`
makes the source active again.

`last_emitted` is the highest allocated number; `complete_through` advances only over
the contiguous successfully exported prefix. In-flight, failed and Filter-dropped
slots keep the boundary open, preserving Inconclusive instead of inventing evidence.
Filtered ranges retain the Filter epoch for #542, but are not yet declared on the
wire and do not advance completeness. The allocator does not retry exports.

Equal counters declare an epoch closed. The allocator seals it, and the next
allocation opens `max(previous epoch + 1, current Unix milliseconds)`. The heartbeat
restates the previous epoch's final declaration before the new one until accepted
under the current Filter epoch. Only the epoch current at Filter publication can
settle Authoritative; Events straddling a source's epoch roll may leave their Binding
Inconclusive.

A restart needs a strictly greater epoch. Unix milliseconds are the default; if the
clock can step backwards, persist the last epoch and pass `previousEpoch` to the
allocator. A clock-derived epoch then advances past it, while an explicit `epoch`
that is not greater is refused. Without persistence Sentinel refuses a regression
with HTTP 409.

`Stop()` cancels scheduling and sends nothing further. `ShutdownAsync()` waits for
a running report and sends a final declaration, which closes only if every allocated
slot was exported. Let exports settle before `ShutdownAsync()` on a clean exit;
`Dispose()` silences the heartbeat without a final send.

## Generated proto types

The C# proto types under `src/Sentinel.Probe.Sdk/gen/` are protoc-gen-csharp output
from the BSR remote plugin `buf.build/protocolbuffers/csharp:v33.4`, which targets the
pinned `Google.Protobuf` 3.33.4 runtime (#466). The tree is **gitignored**, like the Go,
Python, JVM and Elixir SDK trees. `tools/generate-dotnet-sdk.sh` regenerates it from
`proto/` through `buf.gen.dotnet.yaml`, and every Justfile .NET recipe reaches it through
`_sdk-dotnet-restore`. A content digest (`gen/.proto-digest`) makes the step a no-op until
a proto or the template changes; a cold tree costs one remote-plugin call.

`buf.gen.dotnet.yaml` copies the root template's `managed:` block verbatim, so this tree is
byte-identical to the root template's `.generated/dotnet`.
`tools/check-dotnet-template-parity.sh` checks the templates on every pull request, and
`just _generated-check` compares the trees (`sdk-drift:sdk/dotnet`).

The generated API, for readers of the code:

- enum members drop their type prefix (`DecisionAction.Permit`);
- the `AttributeValue` oneof is read through `ValueCase`, and setting an arm sets the case;
- proto3 `optional` fields are a plain value plus `HasX` and `ClearX()`. Reading `X` on an
  absent field yields 0, and C# widens that into a `ulong?` without a diagnostic, so the
  SDK never reads `X` without its `HasX`: either through the `Proto/Presence.cs` helpers,
  which return null for absent, or directly behind an explicit `HasX` check (the gate reads
  `EventFilter.Epoch` that way). Epoch 0 is a real epoch;
- repeated fields are get-only `RepeatedField<T>`; parse with `X.Parser.ParseFrom`.

## Build and test

Run `just harness-probe-sdk-dotnet` (it generates, restores, builds and tests). By hand,
generate first, because a fresh checkout has no `gen/`:

```
tools/generate-dotnet-sdk.sh
cd sdk/dotnet
dotnet restore --locked-mode Sentinel.Probe.Sdk.sln
dotnet build --no-restore Sentinel.Probe.Sdk.sln
dotnet test --no-build --no-restore Sentinel.Probe.Sdk.sln
```

Run the `dotnet` commands from `sdk/dotnet` so SDK discovery uses its `global.json`. All
three pass on .NET 10.0.302. The test suite uses xUnit, is hermetic at test-execution time
(ADR-0019: no network, no wall clock), and covers 220 cases mirroring the Go `*_test.go`
files plus the generated-API presence regressions.

## Key design decisions

- **`Activity.TagObjects`** (S1 fix): span→event conversion reads `Activity.TagObjects`
  rather than `Activity.Tags` so all attribute types round-trip.
- **`System.Numerics.BigInteger`** for Int128: the proto `Int128` (two `int64` words)
  is decoded into `BigInteger` via the `Int128Codec` helper, avoiding any external
  dependency.
- **OTLP/HTTP protobuf**: traces and completeness are posted by `OtlpExporter`.
  The compiled Connect client remains for codec tests and is not the gate's path.
- **`ProbeSpanProcessor : BaseProcessor<Activity>`**: the OTel span processor hooks
  `OnEnd` to convert and emit events, with `Activity.TagObjects` for tag access.
- **Pinned NuGet versions**: all package references pin exact versions
  (`Google.Protobuf` 3.33.4, `OpenTelemetry`/`OpenTelemetry.Api` 1.17.0, `xunit`
  2.9.3, `xunit.runner.visualstudio` 3.1.5, `Microsoft.NET.Test.Sdk` 18.8.1).
- **`RestorePackagesWithLockFile=true`**: `packages.lock.json` files are committed
  per project for reproducible restores, analogous to `go.sum`.
- **Generated code builds under `TreatWarningsAsErrors`** with no exemption:
  protoc-gen-csharp marks every file `// <auto-generated>` (nullable context off,
  analyzers skip it) and emits its own `#pragma warning disable` lines.
