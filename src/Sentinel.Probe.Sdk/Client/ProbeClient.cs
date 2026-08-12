using System.Threading;
using System.Threading.Tasks;
using Emet.Sentinel.Model.V1;
using Emet.Sentinel.Probe.V1;
using Sentinel.Probe.Sdk.Enforcement;

namespace Sentinel.Probe.Sdk.Client;

// ProbeClient — the .NET analog of sdk/go/client/client.go (mirrors probe-client.ts).
//
// Owns the Probe's filter state and builds decision requests against it. Safe for concurrent use:
// all mutable state lives behind FilterStore's volatile reference.
public sealed class ProbeClient
{
    /// <summary>Describes the Probe's identity and the Sentinel it talks to.</summary>
    public sealed class Config
    {
        /// <summary>The Probe's source_handle, stamped into DecideRequest.source_handle.</summary>
        public string SourceHandle { get; set; } = "";

        /// <summary>
        /// The Sentinel decision endpoint base URL. The SDK never reads it directly — the decider
        /// passed at construction carries requests and already holds its own base URL. The field
        /// exists for parity with the reference's sentinelBaseUrl and so a host keeps its endpoint
        /// configuration in one struct.
        /// </summary>
        public string SentinelBaseURL { get; set; } = "";

        /// <summary>Optionally seeds the store before the first push, e.g. from a local cache.</summary>
        public EventFilter? InitialFilter { get; set; }
    }

    private readonly Config _config;
    private readonly FilterStore _store;
    private readonly ISentinelDecisionServiceClient _decider;

    /// <summary>
    /// Builds a ProbeClient. The decider is a REQUIRED parameter with no default: a silently-defaulted
    /// transport would hide a misconfigured endpoint until the first enforcement call. Pass
    /// <see cref="SentinelTransport"/> in production and a stub in tests.
    /// </summary>
    public ProbeClient(Config config, ISentinelDecisionServiceClient decider)
    {
        _config = config;
        _store = new FilterStore(config.InitialFilter);
        _decider = decider;
    }

    /// <summary>Returns the EventFilter for this source, or null before the first refresh.</summary>
    public EventFilter? CurrentFilter() => _store.Get();

    /// <summary>
    /// Returns the held filter epoch, the value a Probe stamps into
    /// ProducerEvent.acknowledged_filter_epoch. null means no epoch is held; it does not mean 0.
    /// </summary>
    public ulong? AcknowledgedEpoch() => _store.Epoch();

    /// <summary>
    /// Swaps in a new EventFilter, reporting whether the store was actually updated. Where the new
    /// filter comes from is out of SDK scope: in v1 Sentinel pushes filters (ADR-0006) and no push
    /// RPC exists in decision.proto, so the host calls SetFilter when a push arrives.
    /// </summary>
    public bool SetFilter(EventFilter? filter) => _store.Set(filter);

    /// <summary>Reports whether an announced epoch warrants fetching a new filter.</summary>
    public bool RefreshOnEpoch(ulong? newEpoch) => _store.ShouldRefresh(newEpoch);

    /// <summary>
    /// Builds a DecideRequest against the held filter epoch. The event must already have been
    /// projected by ApplyFilter; this method does not project, exactly as the reference does not.
    /// remainingBudgetNs is null when the caller set no latency budget.
    /// </summary>
    public DecideRequest BuildDecideRequest(
        ProducerEvent @event,
        string requestId,
        string idempotencyKey,
        ulong? remainingBudgetNs)
    {
        return new DecideRequest
        {
            RequestId = requestId,
            IdempotencyKey = idempotencyKey,
            SourceHandle = _config.SourceHandle,
            FilterEpoch = _store.Epoch(),
            ProducerEvent = @event,
            RemainingTransportBudgetNanoseconds = remainingBudgetNs,
        };
    }

    /// <summary>Exposes the decision client so the enforcement gate can call it.</summary>
    public ISentinelDecisionServiceClient Decider() => _decider;

    /// <summary>The configured source handle, stamped into every DecideRequest.</summary>
    public string SourceHandle() => _config.SourceHandle;

    /// <summary>
    /// Adapts this client's decider to the shape the enforcement gate's Decide dependency expects,
    /// so a host does not have to write the Connect result handling by hand. The enforcement package
    /// deliberately takes a plain delegate rather than the client interface, so it stays testable
    /// without a transport; this is the adapter between the two. Errors are returned unwrapped so
    /// the gate can classify ConnectException and cancellation errors distinctly.
    /// </summary>
    public DecideFunc DecideFunc() => DecideFunc(_decider);

    /// <summary>
    /// Adapts any SentinelDecisionServiceClient to the enforcement gate's Decide dependency. Use
    /// the instance method unless the decider is not the one a ProbeClient holds.
    /// </summary>
    public static DecideFunc DecideFunc(ISentinelDecisionServiceClient decider)
    {
        return async (request, cancellationToken) =>
        {
            var r = await decider.DecideAsync(request, cancellationToken).ConfigureAwait(false);
            return new DecideOutcome(r.Response, r.Error);
        };
    }
}
