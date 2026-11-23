# .NET Probe SDK for Sentinel

[![CI](https://github.com/emet-labs/sentinel-probe-dotnet/actions/workflows/ci.yml/badge.svg)](https://github.com/emet-labs/sentinel-probe-dotnet/actions/workflows/ci.yml)
[![License: MPL-2.0](https://img.shields.io/badge/License-MPL--2.0-informational.svg)](LICENSE)

The .NET Probe SDK for [Sentinel](https://github.com/emet-labs) — instrument a .NET
application as a Sentinel **Probe**: the in-process component that reports what your application
really did, as Events, and receives Sentinel's Verdicts and Event Filters in return.

Sentinel verifies cross-system action sequences against Specifications you declare, using
recorded evidence rather than logs and dashboards. A Probe is how your code joins that
contract. Missing evidence is never a pass: until Sentinel can show it has seen everything
a source emitted, a Verdict that depends on that source stays Inconclusive.

## Requirements

- .NET 10 SDK.
- A [Sentinel](https://github.com/emet-labs) deployment on the same host as the Probe.
  The receiver uses the IPv4 loopback address `127.0.0.1`. Start it before Sentinel,
  following the order below.

## Installation

The package is not yet on NuGet. Until it is, build it from this repository:

```sh
dotnet restore --locked-mode Sentinel.Probe.Sdk.sln
dotnet build --no-restore Sentinel.Probe.Sdk.sln
```

The generated protobuf types are committed, so a bare clone builds with no code
generator, and the committed `packages.lock.json` files make restores reproducible.

## Quickstart

The Probe wire is OTLP/HTTP protobuf, in both directions:

- each ended `Activity` is exported as one Event to Sentinel's `POST /v1/traces`, with the
  `x-sentinel-source-handle` header naming your source;
- completeness signals go to `POST /v1/logs` on the same base URL, with the same
  `x-sentinel-source-handle` header;
- Sentinel posts Verdicts and published Event Filters as OTLP logs to the Probe's loopback
  log receiver.

Start the Probe and its log receiver first. Then start Sentinel with
`SENTINEL_PROBE_LOG_URL` set to the receiver's full `http://127.0.0.1:<port>/v1/logs`
URL, and publish the Specification only after the receiver is listening. Sentinel reads
this URL once at startup and does not retry a failed Filter push. If the receiver's port
changes when the Probe restarts, restart Sentinel with the new URL and publish again.
One Sentinel process has one Probe log URL, so only one Probe process receives these
Verdicts and Event Filters today.

Both ingest routes require a source handle admitted by your Sentinel deployment; replace
`checkout-service` below with that handle. A missing header returns HTTP 401; an unknown
handle returns HTTP 403, and neither stores evidence. A completeness record's
`sentinel.source_handle` must match the header; the SDK exporter sets both.

Sentinel accepts `Content-Type: application/x-protobuf`, with identity or gzip encoding.
OTLP/JSON, OTLP/gRPC and other encodings are unsupported (HTTP 415 on the HTTP route).
An invalid span can refuse the whole request during decoding, including an unknown
`sentinel.*` attribute. Admission conflicts may instead return HTTP 200 with nonzero
`partial_success.rejected_spans`; this is still a failed export for completeness.
The OTel span name becomes the Event kind.

```csharp
using Emet.Sentinel.Model.V1;
using OpenTelemetry.Trace;
using Sentinel.Probe.Sdk.Client;
using Sentinel.Probe.Sdk.Emission;
using Sentinel.Probe.Sdk.Otlp;

const string SentinelUrl = "http://127.0.0.1:8080";
const string SourceHandle = "checkout-service";

// The loopback log receiver: point Sentinel's SENTINEL_PROBE_LOG_URL at receiver.LogsUrl.
using var receiver = OtlpLogReceiver.Start();

// The versioned Event Filter for this source.
var store = new FilterStore();

// One sequence allocator per source. The stamping processor numbers each Activity when
// it starts (see "Sequence numbers and completeness").
var sequence = new SequenceAllocator();
using var tracer = ProbeTracer.NewProbeTracer(new ProbeTracerOptions
{
    TracerName = SourceHandle,
    ServiceName = SourceHandle,
    ConfigureProvider = builder => builder.AddProcessor(new SequenceStampingProcessor(sequence)),
});
using var exporter = new OtlpExporter(SentinelUrl);

// Declares this source's completeness on POST /v1/logs.
using var heartbeat = new CompletenessHeartbeat(
    sequence,
    (signal, ct) => exporter.ExportCompletenessAsync(SourceHandle, signal, ct),
    filterEpoch: store.Epoch,
    onError: error => Console.Error.WriteLine(error));
heartbeat.Start();

// Repeat whenever a new Filter arrives, before emitting or reporting completeness.
if (receiver.Filters.Count > 0) store.Set(receiver.Filters[^1]);

var activity = tracer.Source.StartActivity("checkout.charge.completed")!;
activity.SetTag("order.id", "order-1001");
// ... the work the Specification watches ...
activity.Stop();

// This SDK has no exporting processor: export the ended Activity and mark its slot.
if (SequenceStampingProcessor.TryGetSlot(activity, out var slot))
{
    try
    {
        long rejected = await exporter.ExportSpansCheckedAsync(SourceHandle, new[]
        {
            new SpanExport
            {
                Activity = activity,
                EventId = Guid.NewGuid().ToString(),
                SequenceEpoch = slot.Epoch,
                SequenceNumber = slot.Number,
                SchemaVersion = "1.0.0",
                AcknowledgedFilterEpoch = store.Epoch() ?? 0UL,
                ClaimedSensitivity = Sensitivity.SensitivityInternal,
            },
        }, CancellationToken.None);
        if (rejected == 0) sequence.MarkExported(slot); else sequence.MarkFailed(slot);
    }
    catch (OtlpExportException)
    {
        sequence.MarkFailed(slot);
    }
}

// On a clean exit, once the exports have settled: send the final completeness report.
await heartbeat.ShutdownAsync();
```

`SpanExport` supplies the Event contract for one ended `Activity`: `EventId`,
`SchemaVersion`, `AcknowledgedFilterEpoch` (0 until the first Event Filter arrives),
`ClaimedSensitivity` (anything but unspecified) and the two sequence numbers. A causal
predecessor goes in the `sentinel.parent.event.id` tag, or in a link's own
`sentinel.event.id` tag. An `Activity` that breaks this contract, or an export Sentinel
refuses, throws `OtlpExportException`, and nothing is sent for a refused `Activity`. A
span id never stands in for a missing Event id.

Event Filters collect in `receiver.Filters` (oldest first) and are not applied for you.
Hand the newest to `store.Set`, which is a no-op for an unchanged or older epoch. No export
path in this SDK applies the Event Filter; `ApplyFilter.Apply` projects a `ProducerEvent`
against the held Filter.

Start Probe spans as roots, or stamp the parent's Event id in
`sentinel.parent.event.id`: an ambient OTel parent without that Event id is refused.

### Enforcement gate

The gate is in the SDK, but Sentinel does not answer decision requests yet (see Status).

```csharp
using Sentinel.Probe.Sdk.Enforcement;
using Sentinel.Probe.Sdk.Filter;
using Sentinel.Probe.Sdk.Ids;

// `projected` is the ProducerEvent for the action: tracer.ToEvent(...), then
// ApplyFilter.Apply against store.Get(). The receiver waits for a DecideResponse for at most
// the request's remaining Transport Budget, then the gate applies its fail mode.
GateOutcome outcome = await Gate.GateAsync(
    projected,
    store.Get(),
    deadlineNs: null, // an absolute monotonic deadline, if you set one
    new Deps
    {
        Decide = Gate.FromLogReceiver(receiver),
        NowMonotonicNs = MonotonicNanos,        // your monotonic clock, in nanoseconds
        AcceptedFailModeFor = AcceptedFailMode, // the fail mode your deployment contracted
    },
    new Options
    {
        SourceHandle = SourceHandle,
        RequestId = ProbeIds.GenerateRequestId(),
        IdempotencyKey = ProbeIds.GenerateIdempotencyKey(),
    },
    CancellationToken.None); // cancel early only if the host gives up before the budget

if (outcome.Kind.Permitted()) CommitTheAction(); // Permit, FailOpenPermit or NoFilter
else Rollback();                                 // Deny, Defer or FailClosedDeny
```

## What the Probe does

| Namespace | Duty |
|---|---|
| `Sentinel.Probe.Sdk.Otlp` | The Probe wire. `OtlpExporter` posts ended Activities to `POST /v1/traces` and completeness signals to `POST /v1/logs`; `OtlpLogReceiver` is the loopback log receiver for Verdicts, Event Filters and DecideResponses; `CompletenessHeartbeat` reports completeness |
| `Sentinel.Probe.Sdk.Emission` | OTel `Activity` spans become Events (`ProbeTracer`, `SpanToEventConverter`). `SequenceAllocator` and `SequenceStampingProcessor` number the Events |
| `Sentinel.Probe.Sdk.Client` | Holds the versioned Event Filter for your source (`FilterStore`) |
| `Sentinel.Probe.Sdk.Filter` | Relevance projection — drops attributes no Specification needs. Never samples (`ApplyFilter`) |
| `Sentinel.Probe.Sdk.Enforcement` | The blocking decision — permit / deny / defer, with per-Specification fail modes and a monotonic latency budget (`Gate`, `Gate.FromLogReceiver`). Not answered yet: see Status |
| `Sentinel.Probe.Sdk.Config` | Source Tier resolution from deployment config, never hard-coded (`SourceTierLoader`) |

## Sequence numbers and completeness

To show it has seen everything a source emitted, Sentinel needs two things from the
source:

- **Sequence numbers.** Every exported Event carries `sentinel.sequence.epoch`, its
  Sequence Epoch, and `sentinel.sequence.number`: 1, 2, 3, … within that epoch.
- **Completeness signals.** A `ProducerCompletenessSignal` on `POST /v1/logs` declares, for
  the current epoch, `last_emitted` (the highest number allocated) and `complete_through`
  (the highest number up to which every Event was exported successfully), with the Filter
  epoch the Probe acknowledges. The source's Watermark advances from these signals.

The SDK provides both:

- `Sentinel.Probe.Sdk.Emission.SequenceAllocator`: create one per source.
  `SequenceStampingProcessor` allocates a slot when each `Activity` starts and stamps both
  sequence tags; `SequenceStampingProcessor.TryGetSlot` reads the slot back. An `Activity`
  that already carries both keeps them, so you can reserve a slot with `Allocate()` ahead
  of its Event and stamp it yourself.
- This SDK has no exporting processor, so your code marks each slot, as the quickstart
  shows. An export succeeded only when Sentinel answered HTTP 2xx with no rejected spans
  (`partial_success.rejected_spans` absent or 0): `ExportSpansCheckedAsync` returns that
  count and throws on any other status. Mark the slot with `MarkExported` on success and
  `MarkFailed` otherwise. If you drop an Event for the Event Filter, mark its slot with
  `MarkFiltered(slot, filterEpoch)`.
- `Sentinel.Probe.Sdk.Otlp.CompletenessHeartbeat`: after `Start()` it reports the
  allocator's declaration on a `PeriodicTimer` over a `TimeProvider`, every 15 seconds by
  default; `ReportAsync` sends one on demand. It acknowledges the held Filter epoch (0
  before the first Event Filter), sends one signal at a time, and restates the previous
  signal when nothing has changed. `CompletenessSignals.Build` builds a signal.

**`complete_through` never runs ahead of the evidence.** It advances only across a
contiguous run of successful exports. A failed export, an `Activity` that never ends or is
never exported, and today an Event dropped for the Event Filter each hold it below that
slot: the epoch stays open and the Verdicts that depend on this source stay Inconclusive
instead of passing. The allocator does not retry. Re-export the identical Event and call
`MarkExported(slot)`.

**The epoch rolls after a declared close.** A signal whose `complete_through` equals
`last_emitted` is a declared close, and Sentinel accepts nothing more in that epoch. The
allocator seals it, and the next allocation opens a new epoch: the greater of the previous
epoch plus one and the clock's Unix milliseconds. Expect a new Sequence Epoch after every
quiet period. If Sentinel has not yet accepted the previous epoch's final declaration
under the Filter epoch the Probe now acknowledges, the heartbeat restates it before the
first signal of the new epoch.

**A restarted process needs a strictly greater epoch.** By default the first epoch is the
clock's Unix milliseconds. If your clock can step backwards across a restart, persist the
last epoch and pass it to the next run's `SequenceAllocator` as the previous epoch: the
allocator then starts at the greater of the clock and that epoch plus one, and refuses an
explicit epoch that is not greater. Without it, a clock that stepped backwards yields an
epoch that Sentinel refuses with HTTP 409.

**Idle sources.** `DeclareIdle()` reports the source as intentionally idle. The next
allocation, or `DeclareActive()`, makes it active again.

**`Stop()` and `ShutdownAsync()`.** `Stop()` cancels the schedule and sends nothing more:
use it when the process must go silent. `ShutdownAsync()` waits for a running report and
sends one final report, which is a declared close only if every slot was exported. On a
clean exit, let your exports settle first, then call `ShutdownAsync()`.

## Status

- **Detect-only.** Sentinel ingests Events, evaluates Specifications and reports Verdicts.
  It blocks nothing.
- **Decide is not served.** Sentinel answers no decision requests today. When an enforcing
  Specification selects an Event, the gate waits on the log receiver for a DecideResponse
  for at most the request's remaining Transport Budget. No answer arrives, so when the
  budget runs out the gate resolves by its fail mode: `OutcomeKind.FailClosedDeny` when one
  of those Specifications is fail-closed and your deployment accepted that,
  `OutcomeKind.FailOpenPermit` otherwise. Cancelling the token you pass to
  `Gate.GateAsync` ends the wait sooner, with the same fail mode.
- **One wire.** OTLP/HTTP protobuf, as described above, is the only Probe wire.
- **Unversioned.** There is no release yet, and no compatibility guarantee before one: the
  API and the wire may still change. A `0.1.0` package manifest is not a release.

A Specification with no blocking budget or a zero budget resolves by fail mode at once.

## Contributing

See [CONTRIBUTING.md](CONTRIBUTING.md). This repository is a published mirror; pull
requests are reviewed and re-landed upstream.

## License

[MPL-2.0](LICENSE)

## Development environment (this mirror): Devbox + just

This repository pins its own toolchain — `devbox.json` + `devbox.lock` — and every task
runs inside it, the same convention as the Sentinel source repository:

    devbox install        # once; resolves devbox.lock
    devbox shell          # then `just --list` for the recipes

`build`, `test`, `lint` and `fmt-check` reuse the canonical gate names of the source
repository, scoped to this one language.

The toolchain is tested with Devbox 0.19.0. Devbox release installs are a launcher that
runs `DEVBOX_USE_VERSION` when it is set, so `export DEVBOX_USE_VERSION=0.19.0` keeps you
on the tested version.
