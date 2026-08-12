using Xunit;
using Emet.Sentinel.Model.V1;
using Sentinel.Probe.Sdk.Client;

namespace Sentinel.Probe.Sdk.Tests;

public class FilterStoreTests
{
    private static EventFilter MakeFilter(ulong? epoch) => new() { Epoch = epoch };

    private static ulong? U64(ulong v) => v;

    [Fact]
    public void Get_IsNullBeforeFirstSet()
    {
        var store = new FilterStore(null);
        Assert.Null(store.Get());
        Assert.Null(store.Epoch());
    }

    [Fact]
    public void ZeroValueStoreIsUsable()
    {
        var store = new FilterStore();
        Assert.Null(store.Get());
        Assert.True(store.Set(MakeFilter(U64(5))));
        Assert.Equal(5UL, store.Epoch());
    }

    [Fact]
    public void SetFirstSwapsPointer()
    {
        var store = new FilterStore(null);
        var first = MakeFilter(U64(5));
        Assert.True(store.Set(first));
        Assert.Same(first, store.Get());
    }

    [Fact]
    public void SetUnchangedEpochIsNoOp()
    {
        var store = new FilterStore(MakeFilter(U64(5)));
        Assert.False(store.Set(MakeFilter(U64(5))));
    }

    // TestFilterStoreSetComparesEpochsByValueNotPointer: two distinct allocations holding the
    // same epoch must compare equal — the trap a naive reference comparison walks into.
    [Fact]
    public void SetComparesEpochsByValueNotPointer()
    {
        var a = MakeFilter(U64(7));
        var b = MakeFilter(U64(7));
        Assert.NotSame(a, b);
        var store = new FilterStore(a);
        Assert.False(store.Set(b)); // same epoch value -> no update despite distinct references
    }

    [Fact]
    public void SetChangedEpochSwaps()
    {
        var store = new FilterStore(null);
        store.Set(MakeFilter(U64(5)));
        var next = MakeFilter(U64(6));
        Assert.True(store.Set(next));
        Assert.Same(next, store.Get());
    }

    // epoch 0 is a legitimate epoch; presence must be tested with Epoch == null, not == 0.
    [Fact]
    public void EpochZeroIsPresent()
    {
        var store = new FilterStore(MakeFilter(U64(0)));
        ulong? epoch = store.Epoch();
        Assert.NotNull(epoch);
        Assert.Equal(0UL, epoch);
        Assert.False(store.ShouldRefresh(U64(0)));
        Assert.True(store.ShouldRefresh(U64(1)));
        Assert.False(store.Set(MakeFilter(U64(0))));
    }

    // A held filter and a new filter that both declare no epoch: first Set updates, second no-ops.
    [Fact]
    public void SetOnFilterWithoutEpoch()
    {
        var store = new FilterStore(null);
        Assert.True(store.Set(MakeFilter(null)));
        Assert.Null(store.Epoch());
        Assert.False(store.Set(MakeFilter(null)));
        Assert.True(store.Set(MakeFilter(U64(0))));
    }

    [Fact]
    public void ShouldRefreshWithNoFilterHeld()
    {
        var store = new FilterStore(null);
        Assert.True(store.ShouldRefresh(null));
        Assert.True(store.ShouldRefresh(U64(1)));
    }

    [Fact]
    public void ShouldRefreshWithNoAnnouncedEpoch()
    {
        var store = new FilterStore(MakeFilter(U64(5)));
        Assert.False(store.ShouldRefresh(null));
    }

    [Fact]
    public void ShouldRefreshComparesEpochs()
    {
        var store = new FilterStore(MakeFilter(U64(5)));
        Assert.True(store.ShouldRefresh(U64(6)));
        Assert.False(store.ShouldRefresh(U64(5)));
    }

    [Fact]
    public void EpochIsACopy()
    {
        var filter = MakeFilter(U64(5));
        var store = new FilterStore(filter);
        ulong? epoch = store.Epoch();
        Assert.NotNull(epoch);
        epoch = 99; // mutating the returned local must not reach the held filter
        Assert.Equal(5UL, store.Epoch());
        Assert.Equal(5UL, filter.Epoch);
    }

    // Reference-swap property: a snapshot taken for an emit-and-enforce flow stays consistent even
    // when a push lands mid-flow.
    [Fact]
    public void SnapshotSurvivesMidFlowSet()
    {
        var store = new FilterStore(null);
        var first = MakeFilter(U64(5));
        store.Set(first);
        var snapshot = store.Get();
        store.Set(MakeFilter(U64(6)));
        Assert.Same(first, snapshot);
        Assert.NotSame(first, store.Get());
        Assert.Equal(5UL, snapshot!.Epoch);
    }

    [Theory]
    [InlineData(null, null, true)]
    [InlineData(null, 0UL, false)]
    [InlineData(0UL, null, false)]
    [InlineData(0UL, 0UL, true)]
    [InlineData(7UL, 7UL, true)]
    [InlineData(7UL, 8UL, false)]
    [InlineData(ulong.MaxValue, ulong.MaxValue, true)]
    public void EqualEpochCases(ulong? a, ulong? b, bool want)
    {
        Assert.Equal(want, FilterStore.EqualEpoch(a, b));
    }
}
