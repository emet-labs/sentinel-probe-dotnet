using System.Collections.Generic;
using System.Threading;
using Xunit;
using Emet.Sentinel.Model.V1;
using Sentinel.Probe.Sdk.Client;

namespace Sentinel.Probe.Sdk.Tests;

public class FilterStoreTests
{
    private static EventFilter MakeFilter(ulong? epoch) => TestSupport.MakeFilter(epoch);

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

    // A held filter and a new filter that both declare no epoch: first Set updates, second no-ops;
    // any present epoch, including 0, then replaces the epoch-less Filter.
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
        Assert.False(store.ShouldRefresh(U64(4))); // an older epoch is not newer
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
        Assert.True(filter.HasEpoch);
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
        Assert.True(snapshot!.HasEpoch);
        Assert.Equal(5UL, snapshot!.Epoch);
    }

    // The epoch-order table: (held, offered) -> replaced. null is an absent epoch; 0 is a real one.
    [Theory]
    [InlineData(null, null, false)]
    [InlineData(null, 0UL, true)]
    [InlineData(null, 7UL, true)]
    [InlineData(0UL, null, false)]
    [InlineData(7UL, null, false)]
    [InlineData(0UL, 0UL, false)]
    [InlineData(7UL, 7UL, false)]
    [InlineData(7UL, 8UL, true)]
    [InlineData(8UL, 7UL, false)]
    [InlineData(0UL, 1UL, true)]
    [InlineData(1UL, 0UL, false)]
    [InlineData(ulong.MaxValue, ulong.MaxValue, false)]
    [InlineData(9223372036854775807UL, 9223372036854775808UL, true)]
    [InlineData(9223372036854775808UL, 9223372036854775807UL, false)]
    public void ReplacesCases(ulong? held, ulong? offered, bool want)
    {
        Assert.Equal(want, FilterStore.Replaces(held, offered));
    }

    // Table rows driven through Set on a store seeded with the held epoch: a refused Set keeps the
    // held reference, an applied one swaps it in.
    [Theory]
    [InlineData(null, null, false)]
    [InlineData(null, 0UL, true)]
    [InlineData(0UL, null, false)]
    [InlineData(0UL, 0UL, false)]
    [InlineData(5UL, 4UL, false)]
    [InlineData(5UL, 6UL, true)]
    public void SetFollowsEpochOrder(ulong? held, ulong? offered, bool want)
    {
        var seeded = MakeFilter(held);
        var store = new FilterStore(seeded);
        var next = MakeFilter(offered);
        Assert.Equal(want, store.Set(next));
        Assert.Same(want ? next : seeded, store.Get());
        Assert.Equal(want ? offered : held, store.Epoch());
    }

    // TS/Go parity: an older epoch never downgrades the Filter, and the refusal leaves later
    // comparisons where they were.
    [Fact]
    public void RegressionIsRefusedAndKeepsHeldReference()
    {
        var store = new FilterStore();
        var five = MakeFilter(U64(5));
        Assert.True(store.Set(five));
        Assert.False(store.Set(MakeFilter(U64(4))));
        Assert.Same(five, store.Get());
        Assert.False(store.Set(MakeFilter(null)));
        Assert.Same(five, store.Get());
        Assert.False(store.ShouldRefresh(U64(4)));
        Assert.False(store.ShouldRefresh(U64(5)));
        Assert.True(store.ShouldRefresh(U64(6)));
        var six = MakeFilter(U64(6));
        Assert.True(store.Set(six));
        Assert.Same(six, store.Get());
    }

    // Unsigned order across the signed boundary: a signed compare would call 2^63 older than 2^63-1.
    [Fact]
    public void UnsignedEpochOrderAcrossTheSignedBoundary()
    {
        var store = new FilterStore();
        Assert.True(store.Set(MakeFilter(U64(9223372036854775807UL))));
        Assert.True(store.Set(MakeFilter(U64(9223372036854775808UL))));
        var max = MakeFilter(U64(ulong.MaxValue));
        Assert.True(store.Set(max));
        Assert.False(store.Set(MakeFilter(U64(9223372036854775808UL))));
        Assert.Same(max, store.Get());
        Assert.Equal(ulong.MaxValue, store.Epoch());
        Assert.False(store.ShouldRefresh(U64(ulong.MaxValue)));
    }

    // A seeded store follows the same order as one filled by Set.
    [Fact]
    public void SeededStoreRefusesOlderEpoch()
    {
        var seeded = MakeFilter(U64(10));
        var store = new FilterStore(seeded);
        Assert.False(store.Set(MakeFilter(U64(9))));
        Assert.False(store.Set(MakeFilter(U64(10))));
        Assert.Same(seeded, store.Get());
        Assert.True(store.ShouldRefresh(U64(11)));
    }

    // Concurrent writers released together: the highest epoch always wins, and an observer reading
    // throughout never sees the held epoch decrease. Deterministic in outcome; no sleeps.
    [Fact]
    public void ConcurrentSetsEndAtTheHighestEpoch()
    {
        const int writers = 8;
        const ulong perWriter = 200;
        for (int round = 0; round < 20; round++)
        {
            var store = new FilterStore();
            using var start = new Barrier(writers + 1);
            var done = 0;
            var regressions = 0;
            var threads = new List<Thread>();
            for (int w = 0; w < writers; w++)
            {
                ulong offset = (ulong)w;
                var t = new Thread(() =>
                {
                    start.SignalAndWait();
                    for (ulong i = 0; i < perWriter; i++)
                    {
                        store.Set(MakeFilter(i * writers + offset));
                    }
                    Interlocked.Increment(ref done);
                });
                threads.Add(t);
                t.Start();
            }
            var observer = new Thread(() =>
            {
                start.SignalAndWait();
                ulong? last = null;
                while (Volatile.Read(ref done) < writers)
                {
                    ulong? now = store.Epoch();
                    if (last.HasValue && (!now.HasValue || now.GetValueOrDefault() < last.GetValueOrDefault()))
                    {
                        Interlocked.Increment(ref regressions);
                    }
                    last = now ?? last;
                }
            });
            observer.Start();
            foreach (var t in threads) t.Join();
            observer.Join();
            Assert.Equal(0, regressions);
            Assert.Equal(perWriter * writers - 1, store.Epoch());
        }
    }
}
