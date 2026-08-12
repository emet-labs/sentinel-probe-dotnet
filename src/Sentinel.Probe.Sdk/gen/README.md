# gen/ — proto types for the .NET Probe SDK

This directory holds the C# mirrors of the proto schemas under `proto/`:

- `Model/V1/` — `sentinel.model.v1` (common, event, filter)
- `Probe/V1/` — `sentinel.probe.v1` (decision)

## Why these are checked in

`sdk/dotnet/buf.gen.dotnet.yaml` is the regeneration template (Buf v2 `managed:` mode, S2 fix)
and `tools/generate-dotnet-sdk.sh` is the driver, mirroring `tools/generate-go-sdk.sh`. The
generated tree is **currently hand-checked-in** because the Buf codegen pipeline is not yet wired
into CI — `buf` and the `buf.build/protocolbuffers/csharp:v33.4` remote plugin are not provisioned
on every build host. The hand-written classes are binary-compatible with what the plugin would
emit (they use `Google.Protobuf` 3.33.4's `CodedOutputStream`/`CodedInputStream` directly), so a
future regenerated tree can replace them with no caller-visible change.

Once the pipeline lands in CI, add `gen/` to `.gitignore` and let the digest-gated script
regenerate it at build time, exactly as `sdk/go/gen/` is gitignored today.

## Regenerating

```sh
tools/generate-dotnet-sdk.sh
```

The script regenerates only when a proto or the template changed (content-digest guard), so it is
safe to run from `just build`.
