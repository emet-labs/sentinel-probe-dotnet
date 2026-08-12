using System;

namespace Sentinel.Probe.Sdk.Connect;

// Connect wire codes — the .NET analog of connectrpc.com/connect's Code, since there is no
// connectrpc/csharp. The string spellings are the Connect protocol's canonical wire names (lower
// snake/camel per the spec); the gate's audit record reproduces them as "connect-<code>".
public enum ConnectCode
{
    Unspecified,
    Cancelled,
    Unknown,
    InvalidArgument,
    DeadlineExceeded,
    NotFound,
    AlreadyExists,
    PermissionDenied,
    ResourceExhausted,
    FailedPrecondition,
    Aborted,
    OutOfRange,
    Unimplemented,
    Internal,
    Unavailable,
    DataLoss,
    Unauthenticated,
}

/// <summary>
/// A Connect-protocol error surfaced by the hand-written transport, the .NET analog of Go's
/// <c>*connect.Error</c>. Carries the wire code so the enforcement gate can record it for audit
/// exactly as the Go reference's describeError does.
/// </summary>
public sealed class ConnectException : Exception
{
    public ConnectCode Code { get; }
    public ConnectException(ConnectCode code, string message) : base(message) => Code = code;

    /// <summary>The wire spelling of a code, for audit records ("connect-unavailable", …).</summary>
    public static string WireName(ConnectCode code) => code switch
    {
        ConnectCode.Cancelled => "canceled",
        ConnectCode.Unknown => "unknown",
        ConnectCode.InvalidArgument => "invalid_argument",
        ConnectCode.DeadlineExceeded => "deadline_exceeded",
        ConnectCode.NotFound => "not_found",
        ConnectCode.AlreadyExists => "already_exists",
        ConnectCode.PermissionDenied => "permission_denied",
        ConnectCode.ResourceExhausted => "resource_exhausted",
        ConnectCode.FailedPrecondition => "failed_precondition",
        ConnectCode.Aborted => "aborted",
        ConnectCode.OutOfRange => "out_of_range",
        ConnectCode.Unimplemented => "unimplemented",
        ConnectCode.Internal => "internal",
        ConnectCode.Unavailable => "unavailable",
        ConnectCode.DataLoss => "data_loss",
        ConnectCode.Unauthenticated => "unauthenticated",
        _ => "unknown",
    };

    /// <summary>Parses the wire spelling back to a code, defaulting to Unknown.</summary>
    public static ConnectCode Parse(string? wire) => (wire ?? "") switch
    {
        "canceled" or "cancelled" => ConnectCode.Cancelled,
        "unknown" => ConnectCode.Unknown,
        "invalid_argument" => ConnectCode.InvalidArgument,
        "deadline_exceeded" => ConnectCode.DeadlineExceeded,
        "not_found" => ConnectCode.NotFound,
        "already_exists" => ConnectCode.AlreadyExists,
        "permission_denied" => ConnectCode.PermissionDenied,
        "resource_exhausted" => ConnectCode.ResourceExhausted,
        "failed_precondition" => ConnectCode.FailedPrecondition,
        "aborted" => ConnectCode.Aborted,
        "out_of_range" => ConnectCode.OutOfRange,
        "unimplemented" => ConnectCode.Unimplemented,
        "internal" => ConnectCode.Internal,
        "unavailable" => ConnectCode.Unavailable,
        "data_loss" => ConnectCode.DataLoss,
        "unauthenticated" => ConnectCode.Unauthenticated,
        _ => ConnectCode.Unknown,
    };

    /// <summary>Maps a Connect code to the HTTP status the protocol assigns it.</summary>
    public static int HttpStatus(ConnectCode code) => code switch
    {
        ConnectCode.Cancelled => 499,
        ConnectCode.Unknown => 500,
        ConnectCode.InvalidArgument => 400,
        ConnectCode.DeadlineExceeded => 504,
        ConnectCode.NotFound => 404,
        ConnectCode.AlreadyExists => 409,
        ConnectCode.PermissionDenied => 403,
        ConnectCode.ResourceExhausted => 429,
        ConnectCode.FailedPrecondition => 400,
        ConnectCode.Aborted => 409,
        ConnectCode.OutOfRange => 400,
        ConnectCode.Unimplemented => 501,
        ConnectCode.Internal => 500,
        ConnectCode.Unavailable => 503,
        ConnectCode.DataLoss => 500,
        ConnectCode.Unauthenticated => 401,
        _ => 500,
    };
}
