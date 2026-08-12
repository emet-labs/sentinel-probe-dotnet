namespace Sentinel.Probe.Sdk.Ids;

// Ids — the .NET analog of sdk/go/ids/ids.go (which mirrors sdk/typescript's util/id.ts).
//
// .NET's Guid.NewGuid() is the native UUID v4 generator, the idiomatic equivalent of Go's
// github.com/google/uuid and node:crypto's randomUUID.
public static class ProbeIds
{
    /// <summary>Returns a fresh request ID (UUID v4).</summary>
    public static string GenerateRequestId() => Guid.NewGuid().ToString();

    /// <summary>
    /// Returns a fresh idempotency key (UUID v4). A distinct function from GenerateRequestId even
    /// though the implementation is identical: the two identify different things. A retry of the
    /// same logical decision reuses the idempotency key while taking a new request ID, so
    /// collapsing them would make retries indistinguishable from fresh asks at the receiver.
    /// </summary>
    public static string GenerateIdempotencyKey() => Guid.NewGuid().ToString();
}
