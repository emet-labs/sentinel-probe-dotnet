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
  receives Verdict, EventFilter, and DecideResponse (`Otlp/`, loopback by default and bindable;
  the #398 Probe-wire exception). The gate waits on that log for at most the
  request's remaining Transport Budget, then applies its fail mode. Connect is not
  the live path.
- reads source-tier from deployment config (`Config/`, ADR-0022, never hard-coded)

## OTLP wire

`OtlpExporter` posts `ExportTraceServiceRequest` bytes to `/v1/traces` and a
`ProducerCompletenessSignal` log to `/v1/logs`, with `x-sentinel-source-handle`.
A missing predecessor id fails that export. `OtlpLogReceiver` serves `POST /v1/logs`; it binds
`127.0.0.1` on a random port by default, and `OtlpLogReceiver.Start("0.0.0.0", 9300)` (or
`Start(IPAddress, port)`) lets a containerised Probe receive Event Filters.
`Start(TimeProvider)` and `Start(IPAddress, port, TimeProvider)` take the clock that times each
decision wait, so a test can expire one without a wall clock. A wait lasts at most the request's
remaining Transport Budget, floored to whole 100 ns ticks and capped at the `Timer` limit
(`uint.MaxValue - 1` ms, about 49.7 days). Below 100 ns no timer is armed and the wait fails at
once. On `TimeProvider.System` the timer counts whole milliseconds, so a budget below 1 ms arms a
0 ms timer that fires at once, close to TypeScript and Elixir, which fail without arming (#536).
`new OtlpExporter(url, http, resourceAttributes)` sends OTLP **resource** attributes (`service.name`
and the provenance keys of `platform/probe-config`) with every trace export. They are never span
attributes, so the eight-key span contract is unchanged. A non-2xx answer throws an
`OtlpExportException` whose message, `StatusCode` and `ResponseBody` carry Sentinel's reply, and
whose `IsTransient` is true for no response, timeout, and HTTP 408, 425, 429, 500, 502, 503 and 504. `harness-probe-sdk-dotnet` builds `sentinel-periphery`
and runs the sample CAT-1 charge/ship round trip against it.

## Sequence numbers and completeness

Create one `Emission.SequenceAllocator` per source and add
`new SequenceStampingProcessor(sequence)` to the host's TracerProvider. It allocates
at Activity start and stamps `sentinel.sequence.epoch` and `sentinel.sequence.number`;
an Activity already carrying both keeps its reserved slot. Add `OtlpExportingSpanProcessor` after it: it reads the slot, builds the `SpanExport`, queues it and
marks the slot (below). A host may still call `OtlpExporter.ExportSpansCheckedAsync` itself: mark the
slot exported only on HTTP 2xx with zero `partial_success.rejected_spans`; on a refusal, error or
undecodable response use `sequence.MarkFailed(slot)`. A host Filter drop uses `MarkFiltered(slot, filterEpoch)`.

## Exporting processor

```csharp
var processor = new OtlpExportingSpanProcessor(new OtlpExportingSpanProcessorOptions
{
    SourceHandle = handle, Sequence = sequence, Exporter = exporter, SchemaVersion = "my-schema-v1",
    Filters = filterStore,
    AllowUnattributedRemoteParent = true,
    OnError = f => metrics.Increment(f.Kind.ToString()),
    Log = Console.Error.WriteLine,
});
builder.AddProcessor(new SequenceStampingProcessor(sequence)).AddProcessor(processor);
// shutdown: dispose the provider, then flush and send the final declaration, in that order
await processor.ShutdownAsync(heartbeat, ct);
```

- **Never throws from the span-end path.** A conversion, validation, encode or transport error is
  caught: the slot is marked FAILED (`complete_through` never advances past it), the error goes to
  `OnError` (an `ExportFailure` with kind, error and slots) and to a rate-limited `Log`
  (`LogInterval`, default 10 s). An `OnError` or `Log` that throws is swallowed. A span with no slot is ignored.
- **Bounded async queue.** One worker exports batches (`MaxBatch` 32). When the queue
  (`QueueCapacity` 4096) is full the **newest** Event is dropped, its slot is FAILED and
  `DroppedCount` rises: a dropped Event is missing evidence, never a pass.
- **Retries.** The Probe SDK retry policy ([`../README.md`](../README.md)): transport errors (no
  response, timeout) and HTTP 408, 425, 429, 500, 502, 503 and 504 are retried with exponential
  backoff and jitter (`RetryBaseDelay` 100 ms, capped at `RetryMaxDelay` 5 s, a `Retry-After` on a
  429 or 503 honored within the cap) up to `MaxAttempts` (5), then FAILED.
  Validation errors, every other status and `rejected_spans` are deterministic: FAILED at once, not
  retried. Each Event is validated alone first, so one bad span does not fail its batch, and a
  batch Sentinel rejects is resent one Event at a time so a refusal fails only the Event it names.
- **Duplicate Event id.** When Sentinel refuses a second delivery of an Event id it holds
  (`evidence_conflict`, `identity_conflict`), `OnError` receives `ExportFailureKind.DuplicateEventId`
  (reason `duplicate-event-id`), the slot stays FAILED, `complete_through` never passes it and
  nothing is retried: the slot number never reached Sentinel as an Event, so completeness through it
  would be overstated. Any other rejection (`slot_conflict`) also stays FAILED.
- **Shutdown.** `ShutdownAsync(heartbeat)` stops intake, waits until every queued Event is exported
  or FAILED, then sends the heartbeat's final declaration. `Dispose` only stops intake.

## Remote parents that are not Probes

A span whose remote parent comes from a caller that is not a Probe (a browser, curl, an
uninstrumented service) carries a `traceparent` but no `sentinel.parent.event.id`. By default the
export refuses it and its slot stays FAILED. With `AllowUnattributedRemoteParent = true`
(`SpanConversion`, `SpanExport` and the processor options; default false, as in the Go SDK) it is
exported as a causal **root**: no predecessor, and no parent span id on the Sentinel copy (the
periphery refuses a parent span id without a parent Event id; this encoder never writes one). The
host's own copy keeps its parent span for Tempo. Only a remote parent qualifies: a local parent
without the id is still refused.

`ProbeActivities.StartEvent(source, name, kind, parent, parentEventId)` starts the Event activity.
.NET parents to `Activity.Current` whatever context you pass, so the helper clears it around the
start, then restores it. Without `parent` it uses the ambient activity: a Probe Event
(`sentinel.event.id` tag) becomes a local parent with its id as `sentinel.parent.event.id`; any
other ambient span (an inbound request span) becomes a remote parent without one.

`ParentEventPropagation.InjectParentEventId(carrier, eventId)` writes the `x-sentinel-parent-event-id`
header next to `traceparent`; `ExtractParentEventId(carrier)` reads it, falling back to the
`sentinel` member of `tracestate` (`sentinel=<eventId>`). Dictionary carriers (case-insensitive),
`HttpHeaders` and `Inject`/`Extract` with a getter/setter (ASP.NET `IHeaderDictionary`, Kafka headers) are supported.
Pass the extracted id to `StartEvent` as `parentEventId`.

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
(ADR-0019: no network, no wall clock), and covers 310 cases mirroring the Go `*_test.go`
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
