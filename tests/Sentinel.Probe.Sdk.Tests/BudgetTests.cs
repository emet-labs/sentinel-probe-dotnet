using Xunit;
using Sentinel.Probe.Sdk.Enforcement;

namespace Sentinel.Probe.Sdk.Tests;

public class BudgetTests
{
    public static IEnumerable<object[]> Cases()
    {
        yield return new object[] { 10000L, 5000L, 5000UL };
        yield return new object[] { 10000L, 10000L, 0UL };
        yield return new object[] { 10000L, 99999L, 0UL };
        yield return new object[] { 10000L, 0L, 10000UL };
        yield return new object[] { 0L, -5000L, 5000UL };
        yield return new object[] { long.MinValue + 4, long.MaxValue - 5, 10UL };
        yield return new object[] { long.MinValue, 0L, 0UL };
    }

    [Theory]
    [MemberData(nameof(Cases))]
    public void RemainingTransportBudgetNs(long deadline, long now, ulong want)
    {
        Assert.Equal(want, Budget.RemainingTransportBudgetNs(deadline, now));
    }
}
