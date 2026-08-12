namespace Sentinel.Probe.Sdk.Enforcement;

// Budget — the .NET analog of sdk/go/enforcement/budget.go (mirrors monotonic-budget.ts).
//
// Computes the remaining transport budget from a monotonic absolute deadline. deadlineNs is a
// monotonic ABSOLUTE, computed by the caller at gate entry as nowMonotonicNs + latency_budget_ns.
// nowNs is the current monotonic reading, injected. A passed deadline yields 0, never a negative
// budget.
//
// Pure: no clock access, no side effects. The clock is injected through Deps.NowMonotonicNs and
// never read inside, so budget behaviour is testable without sleeping.
public static class Budget
{
    public static ulong RemainingTransportBudgetNs(long deadlineNs, long nowNs)
    {
        long remaining = deadlineNs - nowNs;
        return remaining <= 0 ? 0 : (ulong)remaining;
    }
}
