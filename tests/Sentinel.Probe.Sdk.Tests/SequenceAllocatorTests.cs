using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Sentinel.Probe.Sdk.Emission;
using Xunit;

namespace Sentinel.Probe.Sdk.Tests;

public class SequenceAllocatorTests
{
    private const ulong StartMs = 1_794_000_000_000UL;
    private const ulong Max = long.MaxValue;

    private readonly ManualTimeProvider _clock = ManualTimeProvider.AtUnixMilliseconds((long)StartMs);

    private SequenceAllocator Allocator(ulong? epoch = null, ulong? previousEpoch = null) =>
        new(_clock, epoch, previousEpoch);

    private static EpochDeclaration Counters(SequenceDeclaration d) => new(d.Epoch, d.LastEmitted, d.CompleteThrough);

    private static EpochDeclaration Strip(EpochDeclaration d) => new(d.Epoch, d.LastEmitted, d.CompleteThrough);

    [Fact]
    public void TheEpochIsTheClockInUnixMilliseconds()
    {
        var sequence = Allocator();
        Assert.Equal(StartMs, sequence.Epoch());
        Assert.Equal(new Slot(StartMs, 1), sequence.Allocate());
    }

    [Fact]
    public void AnExplicitEpochOverridesTheClock()
    {
        Assert.Equal(new Slot(42, 1), Allocator(epoch: 42).Allocate());
    }

    [Fact]
    public void APreviousEpochTheClockIsAheadOfKeepsTheClock()
    {
        Assert.Equal(StartMs, Allocator(previousEpoch: StartMs - 60_000).Epoch());
    }

    [Fact]
    public void APreviousEpochTheClockIsBehindTakesPreviousPlusOne()
    {
        Assert.Equal(StartMs + 1, Allocator(previousEpoch: StartMs).Epoch());
        Assert.Equal(StartMs + 60_001, Allocator(previousEpoch: StartMs + 60_000).Epoch());
    }

    [Fact]
    public void AnOverrideThatIsNotGreaterThanThePreviousEpochIsRefused()
    {
        Assert.Equal(SequenceException.EpochNotGreater, Assert.Throws<SequenceException>(() => Allocator(100, 100)).Code);
        Assert.Equal(SequenceException.EpochNotGreater, Assert.Throws<SequenceException>(() => Allocator(50, 100)).Code);
        Assert.Equal(101UL, Allocator(101, 100).Epoch());
    }

    [Fact]
    public void EpochsAboveTheInt64MaximumAreRefusedAtConstruction()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => Allocator(epoch: Max + 1));
        Assert.Throws<ArgumentOutOfRangeException>(() => Allocator(previousEpoch: Max + 1));
    }

    [Fact]
    public void AFreshEpochDeclaresZeroWithNoFilteredRangeAndNoPreviousEpoch()
    {
        var declaration = Allocator().Declaration();
        Assert.Equal(new EpochDeclaration(StartMs, 0, 0), Counters(declaration));
        Assert.Empty(declaration.Filtered);
        Assert.Null(declaration.Previous);
        Assert.Equal(0UL, Allocator().HighestAllocated());
    }

    [Fact]
    public void AllocationNumbersSlotsFromOneWithinTheEpoch()
    {
        var sequence = Allocator();
        Assert.Equal(new[] { 1UL, 2UL, 3UL }, Enumerable.Range(0, 3).Select(_ => sequence.Allocate().Number).ToArray());
        Assert.Equal(3UL, sequence.HighestAllocated());
    }

    [Fact]
    public void CompleteThroughAdvancesOnlyOverAWholeExportedPrefix()
    {
        var sequence = Allocator();
        var slots = Enumerable.Range(0, 3).Select(_ => sequence.Allocate()).ToArray();
        sequence.MarkExported(slots[2]);
        Assert.Equal(0UL, sequence.Declaration().CompleteThrough);
        sequence.MarkExported(slots[0]);
        Assert.Equal(1UL, sequence.Declaration().CompleteThrough);
        sequence.MarkExported(slots[1]);
        Assert.Equal(3UL, sequence.Declaration().CompleteThrough);
    }

    [Fact]
    public void AFailedExportStallsThePrefixUntilTheSlotIsExported()
    {
        var sequence = Allocator();
        var slots = Enumerable.Range(0, 3).Select(_ => sequence.Allocate()).ToArray();
        sequence.MarkExported(slots[0]);
        sequence.MarkFailed(slots[1]);
        sequence.MarkExported(slots[2]);
        Assert.Equal(1UL, sequence.Declaration().CompleteThrough);
        sequence.MarkExported(slots[1]);
        Assert.Equal(3UL, sequence.Declaration().CompleteThrough);
    }

    [Fact]
    public void AFilteredSlotStallsThePrefixAndRecordsItsFilterEpoch()
    {
        var sequence = Allocator();
        var slots = Enumerable.Range(0, 3).Select(_ => sequence.Allocate()).ToArray();
        sequence.MarkExported(slots[0]);
        sequence.MarkFiltered(slots[1], 4);
        sequence.MarkExported(slots[2]);
        var declaration = sequence.Declaration();
        Assert.Equal(1UL, declaration.CompleteThrough);
        Assert.Equal(new[] { new FilteredRange(2, 2, 4) }, declaration.Filtered);
        sequence.MarkExported(slots[1]);
        Assert.Equal(1UL, sequence.Declaration().CompleteThrough);
    }

    [Fact]
    public void AdjacentFilteredSlotsMergeOnlyWithinOneFilterEpoch()
    {
        var sequence = Allocator();
        var slots = Enumerable.Range(0, 7).Select(_ => sequence.Allocate()).ToArray();
        sequence.MarkFiltered(slots[3], 4);
        sequence.MarkFiltered(slots[1], 4);
        sequence.MarkFiltered(slots[2], 4);
        sequence.MarkFiltered(slots[4], 5);
        sequence.MarkFiltered(slots[6], 5);
        Assert.Equal(
            new[] { new FilteredRange(2, 4, 4), new FilteredRange(5, 5, 5), new FilteredRange(7, 7, 5) },
            sequence.Declaration().Filtered);
    }

    [Fact]
    public void MarksForUnknownForeignOrSettledSlotsAreNoOps()
    {
        var sequence = Allocator();
        var first = sequence.Allocate();
        var second = sequence.Allocate();
        sequence.MarkExported(first with { Epoch = first.Epoch + 1 });
        sequence.MarkExported(first with { Number = 3 });
        sequence.MarkExported(first with { Number = 0 });
        Assert.Equal(0UL, sequence.Declaration().CompleteThrough);
        sequence.MarkExported(first);
        sequence.MarkFailed(first);
        sequence.MarkFiltered(first, 1);
        Assert.Equal(1UL, sequence.Declaration().CompleteThrough);
        Assert.Empty(sequence.Declaration().Filtered);
        sequence.MarkFiltered(second, 2);
        sequence.MarkFailed(second);
        sequence.MarkExported(second);
        Assert.Equal(1UL, sequence.Declaration().CompleteThrough);
        Assert.Equal(new[] { new FilteredRange(2, 2, 2) }, sequence.Declaration().Filtered);
    }

    [Fact]
    public void AFailedSlotCannotBeMarkedFiltered()
    {
        var sequence = Allocator();
        var slot = sequence.Allocate();
        sequence.MarkFailed(slot);
        sequence.MarkFiltered(slot, 1);
        Assert.Empty(sequence.Declaration().Filtered);
    }

    [Fact]
    public void DeclarationNeverSealsAndTheNextAllocationStaysInTheEpoch()
    {
        var sequence = Allocator();
        sequence.MarkExported(sequence.Allocate());
        sequence.Declaration();
        Assert.Equal(new Slot(StartMs, 2), sequence.Allocate());
    }

    [Fact]
    public void DeclareWithASlotInFlightDoesNotSeal()
    {
        var sequence = Allocator();
        sequence.Allocate();
        Assert.Equal(new EpochDeclaration(StartMs, 1, 0), Counters(sequence.Declare()));
        Assert.Equal(new Slot(StartMs, 2), sequence.Allocate());
    }

    [Fact]
    public void ADeclaredCloseSealsTheEpochAndTheNextAllocationRolls()
    {
        var sequence = Allocator();
        sequence.MarkExported(sequence.Allocate());
        Assert.Equal(new EpochDeclaration(StartMs, 1, 1), Counters(sequence.Declare()));
        Assert.Equal(new Slot(StartMs + 1, 1), sequence.Allocate());
        var rolled = sequence.Declaration();
        Assert.Equal(new EpochDeclaration(StartMs, 1, 1), Strip(rolled.Previous!));
        Assert.Equal(new EpochDeclaration(StartMs + 1, 1, 0), Counters(rolled));
    }

    [Fact]
    public void TheRollTakesTheClockWhenItIsAheadOfTheNextEpoch()
    {
        var sequence = Allocator();
        sequence.MarkExported(sequence.Allocate());
        sequence.Declare();
        _clock.SetUnixMilliseconds((long)StartMs + 60_000);
        Assert.Equal(new Slot(StartMs + 60_000, 1), sequence.Allocate());
    }

    [Fact]
    public void AnEmptyEpochDeclaredClosedRollsOnTheNextAllocation()
    {
        var sequence = Allocator();
        Assert.Equal(new EpochDeclaration(StartMs, 0, 0), Counters(sequence.Declare()));
        Assert.Equal(new Slot(StartMs + 1, 1), sequence.Allocate());
        Assert.Equal(new EpochDeclaration(StartMs, 0, 0), Strip(sequence.Declaration().Previous!));
    }

    [Fact]
    public void AfterTheRollAMarkForTheClosedEpochIsANoOp()
    {
        var sequence = Allocator();
        var slot = sequence.Allocate();
        sequence.MarkFailed(slot);
        sequence.MarkExported(slot);
        sequence.Declare();
        var next = sequence.Allocate();
        sequence.MarkFiltered(next, 2);
        sequence.MarkExported(slot);
        var declaration = sequence.Declaration();
        Assert.Equal(new[] { new FilteredRange(1, 1, 2) }, declaration.Filtered);
        Assert.Equal(new EpochDeclaration(StartMs + 1, 1, 0), Counters(declaration));
    }

    [Fact]
    public void AnEpochPastTheMaximumIsAnOverflowNeverAWrap()
    {
        var sequence = Allocator(epoch: Max);
        sequence.Declare();
        Assert.Equal(SequenceException.SequenceOverflow, Assert.Throws<SequenceException>(() => sequence.Allocate()).Code);
        Assert.Equal(Max, sequence.Epoch());
        Assert.Equal(SequenceException.SequenceOverflow, Assert.Throws<SequenceException>(() => Allocator(previousEpoch: Max)).Code);
    }

    [Fact]
    public async Task NoSlotIsEverAllocatedInAnEpochWhoseCloseWasDeclared()
    {
        // I3: allocation and Declare() race on the one lock. Either Declare() sees the slot in
        // flight, or the allocation opens a new epoch; a slot never lands in a closed epoch.
        var sequence = Allocator();
        var slots = new ConcurrentBag<Slot>();
        var closed = new ConcurrentDictionary<ulong, ulong>();
        using var done = new CancellationTokenSource();
        using var ready = new Barrier(5);
        var declarer = Task.Factory.StartNew(() =>
        {
            ready.SignalAndWait();
            while (!done.IsCancellationRequested)
            {
                var declaration = sequence.Declare();
                if (declaration.CompleteThrough == declaration.LastEmitted)
                {
                    closed[declaration.Epoch] = declaration.LastEmitted;
                }
            }
        }, TaskCreationOptions.LongRunning);
        var workers = Enumerable.Range(0, 4).Select(_ => Task.Factory.StartNew(() =>
        {
            ready.SignalAndWait();
            for (var i = 0; i < 2_000; i++)
            {
                var slot = sequence.Allocate();
                slots.Add(slot);
                sequence.MarkExported(slot);
            }
        }, TaskCreationOptions.LongRunning)).ToArray();
        await Task.WhenAll(workers);
        done.Cancel();
        await declarer;
        Assert.Equal(8_000, slots.Distinct().Count());
        foreach (var slot in slots)
        {
            if (closed.TryGetValue(slot.Epoch, out var close))
            {
                Assert.True(slot.Number <= close, $"{slot} was allocated after epoch {slot.Epoch} closed at {close}");
            }
        }
    }

    // --- Epoch chain (ADR-0024 amendment, formal/wire-spec.md section 5) ---

    private static void SealedEpoch(SequenceAllocator sequence)
    {
        sequence.NoteAttempt();
        sequence.MarkExported(sequence.Allocate());
        sequence.Declare();
    }

    [Fact]
    public void C1AHookEntryCountsBeforeAllocationAndAllocateNeverCountsAgain()
    {
        var sequence = Allocator();
        sequence.NoteAttempt();
        sequence.Allocate();
        Assert.Equal(1UL, sequence.Declaration().Attempted);
        sequence.NoteAttempt();
        sequence.NoteAttempt();
        sequence.Allocate();
        Assert.Equal(3UL, sequence.Declaration().Attempted);
        Assert.Equal(2UL, sequence.Declaration().LastEmitted);
    }

    [Fact]
    public void C1ALostAttemptShowsAttemptedAboveLastEmitted()
    {
        var sequence = Allocator();
        sequence.NoteAttempt();
        sequence.MarkExported(sequence.Allocate());
        sequence.NoteAttempt(); // the hook entry fails before Allocate()
        var declaration = sequence.Declaration();
        Assert.Equal(2UL, declaration.Attempted);
        Assert.Equal(1UL, declaration.LastEmitted);
    }

    [Fact]
    public void C1ALostAttemptAcrossASealRollsAndCountsInTheNextEpochLeavingTheSealedOneExact()
    {
        var sequence = Allocator();
        SealedEpoch(sequence);
        sequence.NoteAttempt(); // sealed: rolls exactly as Allocate() would
        var rolled = sequence.Declaration();
        Assert.Equal(StartMs + 1, rolled.Epoch);
        Assert.Equal(1UL, rolled.Attempted);
        Assert.Equal(0UL, rolled.LastEmitted);
        Assert.Equal(StartMs, rolled.PredecessorEpoch);
        var closing = rolled.Previous!;
        Assert.Equal(1UL, closing.Attempted);
        Assert.Equal(closing.LastEmitted, closing.Attempted);
    }

    [Fact]
    public void AnAllocationWithoutNoteAttemptNeverDeclaresAttemptedBelowLastEmitted()
    {
        var sequence = Allocator();
        sequence.Allocate();
        sequence.Allocate();
        Assert.Equal(2UL, sequence.Declaration().Attempted);
    }

    [Fact]
    public void C2SealedAtIsTheProducerClockAtTheFirstClosingDeclarationAndNeverMoves()
    {
        var sequence = Allocator();
        sequence.NoteAttempt();
        var slot = sequence.Allocate();
        Assert.Null(sequence.Declare().SealedAtUnixNanoseconds);
        sequence.MarkExported(slot);
        _clock.SetUnixMilliseconds((long)StartMs + 5);
        var sealedAt = sequence.Declare().SealedAtUnixNanoseconds;
        Assert.Equal(((long)StartMs + 5) * 1_000_000L, sealedAt);
        _clock.SetUnixMilliseconds((long)StartMs + 900);
        Assert.Equal(sealedAt, sequence.Declare().SealedAtUnixNanoseconds);
        Assert.Equal(sealedAt, sequence.Declaration().SealedAtUnixNanoseconds);
    }

    // C2 amended 2026-11-15: an epoch seals only when attempted == last_emitted.

    [Fact]
    public void C2AHeartbeatBetweenNoteAttemptAndAllocateDoesNotSeal()
    {
        var sequence = Allocator();
        sequence.NoteAttempt();
        var during = sequence.Declare();
        Assert.Null(during.SealedAtUnixNanoseconds);
        Assert.Equal((0UL, 0UL, 1UL), (during.LastEmitted, during.CompleteThrough, during.Attempted));
        Assert.Equal(new Slot(StartMs, 1), sequence.Allocate());
        Assert.Null(sequence.Declare().SealedAtUnixNanoseconds);
    }

    [Fact]
    public void C2TheEpochStaysOpenWithCompleteThroughEqualLastWhileAttemptedIsAboveLast()
    {
        var sequence = Allocator();
        sequence.NoteAttempt();
        var first = sequence.Allocate();
        sequence.NoteAttempt();
        sequence.MarkExported(first);
        var open = sequence.Declare();
        Assert.Equal((1UL, 1UL, 2UL), (open.LastEmitted, open.CompleteThrough, open.Attempted));
        Assert.Null(open.SealedAtUnixNanoseconds);
        var second = sequence.Allocate();
        Assert.Equal(new Slot(StartMs, 2), second);
        sequence.MarkExported(second);
        var closed = sequence.Declare();
        Assert.NotNull(closed.SealedAtUnixNanoseconds);
        Assert.Equal(closed.LastEmitted, closed.Attempted);
    }

    [Fact]
    public void C2ALostAttemptKeepsTheEpochOpenSoASealedEpochNeverHasAttemptedAboveLast()
    {
        var sequence = Allocator();
        sequence.NoteAttempt();
        var slot = sequence.Allocate();
        sequence.NoteAttempt();
        sequence.MarkExported(slot);
        for (var i = 0; i < 3; i++)
        {
            var d = sequence.Declare();
            Assert.Null(d.SealedAtUnixNanoseconds);
            Assert.True(d.Attempted > d.LastEmitted);
        }
    }

    [Fact]
    public void C2AnIdleEpochWithNoAttemptsStillSeals()
    {
        Assert.Equal((long)StartMs * 1_000_000L, Allocator().Declare().SealedAtUnixNanoseconds);
    }

    [Fact]
    public void C2AndC7SealedAtNeverStepsBackwardsWhenTheClockDoes()
    {
        var sequence = Allocator();
        SealedEpoch(sequence);
        var sealedAt = sequence.Declaration().SealedAtUnixNanoseconds!.Value;
        sequence.NoteAttempt();
        var slot = sequence.Allocate();
        _clock.SetUnixMilliseconds((long)StartMs - 10_000);
        sequence.MarkExported(slot);
        Assert.True(sequence.Declare().SealedAtUnixNanoseconds!.Value >= sealedAt);
    }

    [Fact]
    public void C3ARollSetsThePredecessorAndKeepsTheClosingDeclarationUnchanged()
    {
        var sequence = Allocator();
        SealedEpoch(sequence);
        var closing = sequence.Declaration().CurrentEpoch();
        sequence.NoteAttempt();
        var slot = sequence.Allocate();
        _clock.SetUnixMilliseconds((long)StartMs + 77);
        var rolled = sequence.Declare();
        Assert.Equal(StartMs, rolled.PredecessorEpoch);
        Assert.Null(rolled.SealedAtUnixNanoseconds);
        Assert.Equal(closing, rolled.Previous);
        Assert.Null(closing.PredecessorEpoch);
        sequence.MarkExported(slot);
        sequence.Declare();
        sequence.NoteAttempt();
        var third = sequence.Declaration();
        Assert.Equal(StartMs + 1, third.PredecessorEpoch);
        Assert.Equal(StartMs + 1, third.Previous!.Epoch);
        Assert.Equal(StartMs, third.Previous!.PredecessorEpoch);
        Assert.NotNull(third.Previous!.SealedAtUnixNanoseconds);
    }

    [Fact]
    public void C4RestartSetsThePredecessorOnlyWhenAPreviousEpochWasPersisted()
    {
        Assert.Equal(100UL, Allocator(previousEpoch: 100).Declaration().PredecessorEpoch);
        Assert.Null(Allocator().Declaration().PredecessorEpoch);
        Assert.Equal(100UL, Allocator(epoch: 200, previousEpoch: 100).Declaration().PredecessorEpoch);
        Assert.Null(Allocator(epoch: 200).Declaration().PredecessorEpoch);
        // attempted is not persisted: a restarted allocator starts at zero.
        Assert.Equal(0UL, Allocator(previousEpoch: 100).Declaration().Attempted);
    }

    [Fact]
    public void C1AnAttemptOnAnOverflowingRollIsASequenceException()
    {
        var rolled = Allocator(epoch: Max);
        rolled.Declare();
        Assert.Equal(SequenceException.SequenceOverflow, Assert.Throws<SequenceException>(() => rolled.NoteAttempt()).Code);
        Assert.Equal(0UL, rolled.Declaration().Attempted);
    }
}
