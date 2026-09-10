using Swoosh.Input;
using Xunit;

namespace Swoosh.Tests;

public class TouchpadParserSerialTests
{
    [Fact]
    public void HybridFrame_SplitAcrossMultipleReports_EmitsSingleCompleteFrame()
    {
        var aggregator = new SerialFrameAggregator();
        long now = 1000;

        // Report 1: First contact of a 2-finger frame (Contact Count = 2)
        var frame1 = aggregator.ProcessReport(
            hasContactCount: true,
            contactCount: 2,
            tip: true,
            contactId: 1,
            nx: 0.2,
            ny: 0.5,
            timestampMs: now);

        // Should not emit a partial 1-contact frame
        Assert.Null(frame1);
        Assert.True(aggregator.IsAggregating);
        Assert.Equal(2, aggregator.ExpectedCount);
        Assert.Equal(1, aggregator.PendingCount);

        // Report 2: Second contact of the same frame (Contact Count = 0 on subsequent reports)
        var frame2 = aggregator.ProcessReport(
            hasContactCount: true,
            contactCount: 0,
            tip: true,
            contactId: 2,
            nx: 0.8,
            ny: 0.5,
            timestampMs: now + 2);

        // Should emit the complete frame with both contacts
        Assert.NotNull(frame2);
        Assert.False(aggregator.IsAggregating);
        Assert.Equal(2, frame2.DownCount);
        Assert.Equal(2, frame2.Contacts.Count);
        Assert.Contains(frame2.Contacts, c => c.Id == 1 && c.X == 0.2 && c.TipDown);
        Assert.Contains(frame2.Contacts, c => c.Id == 2 && c.X == 0.8 && c.TipDown);
    }

    [Fact]
    public void HybridFrame_SplitAcrossMultipleParserCalls_EmitsSingleCompleteFrame()
    {
        // Simulates two separate WM_INPUT message payloads
        var aggregator = new SerialFrameAggregator();

        // WM_INPUT 1: Report with Contact Count = 2, Contact ID = 10
        var f1 = aggregator.ProcessReport(
            hasContactCount: true,
            contactCount: 2,
            tip: true,
            contactId: 10,
            nx: 0.3,
            ny: 0.4,
            timestampMs: 100);

        Assert.Null(f1);

        // WM_INPUT 2: Report with Contact Count = 0, Contact ID = 20
        var f2 = aggregator.ProcessReport(
            hasContactCount: true,
            contactCount: 0,
            tip: true,
            contactId: 20,
            nx: 0.7,
            ny: 0.6,
            timestampMs: 108);

        Assert.NotNull(f2);
        Assert.Equal(2, f2.DownCount);
        Assert.Contains(f2.Contacts, c => c.Id == 10);
        Assert.Contains(f2.Contacts, c => c.Id == 20);
    }

    [Fact]
    public void HybridFrame_ThreeContacts_AssembledAcrossReports()
    {
        var aggregator = new SerialFrameAggregator();
        long t = 500;

        // Report 1 declares 3 contacts
        Assert.Null(aggregator.ProcessReport(true, 3, true, 1, 0.1, 0.1, t));
        // Report 2 delivers contact 2
        Assert.Null(aggregator.ProcessReport(true, 0, true, 2, 0.5, 0.5, t + 2));
        // Report 3 delivers contact 3
        var frame = aggregator.ProcessReport(true, 0, true, 3, 0.9, 0.9, t + 4);

        Assert.NotNull(frame);
        Assert.Equal(3, frame.DownCount);
        Assert.Equal(3, frame.Contacts.Count);
        Assert.Contains(frame.Contacts, c => c.Id == 1);
        Assert.Contains(frame.Contacts, c => c.Id == 2);
        Assert.Contains(frame.Contacts, c => c.Id == 3);
    }

    [Fact]
    public void HybridFrame_ContinuousMotion_DoesNotEmitTemporallyMixedContacts()
    {
        var aggregator = new SerialFrameAggregator();

        // Initial 2-finger frame at t = 100
        aggregator.ProcessReport(true, 2, true, 1, 0.20, 0.50, 100);
        var f1 = aggregator.ProcessReport(true, 0, true, 2, 0.80, 0.50, 102);
        Assert.NotNull(f1);

        // Move fingers at t = 110:
        // Report 1 has new pos for contact 1. MUST NOT emit frame with (new contact 1, old contact 2).
        var intermediate = aggregator.ProcessReport(true, 2, true, 1, 0.25, 0.55, 110);
        Assert.Null(intermediate);

        // Report 2 has new pos for contact 2. Complete frame emitted.
        var f2 = aggregator.ProcessReport(true, 0, true, 2, 0.85, 0.55, 112);
        Assert.NotNull(f2);
        Assert.Equal(2, f2.DownCount);
        var c1 = f2.Contacts.First(c => c.Id == 1);
        var c2 = f2.Contacts.First(c => c.Id == 2);
        Assert.Equal(0.25, c1.X);
        Assert.Equal(0.85, c2.X);
    }

    [Fact]
    public void ContactRemoval_ReducedContactCount_RemovesOmittedContact()
    {
        var aggregator = new SerialFrameAggregator();

        // 2 fingers down
        aggregator.ProcessReport(true, 2, true, 1, 0.2, 0.5, 100);
        var f1 = aggregator.ProcessReport(true, 0, true, 2, 0.8, 0.5, 102);
        Assert.NotNull(f1);
        Assert.Equal(2, f1.DownCount);

        // User lifts finger 2; next frame declares Contact Count = 1 with only contact 1
        var f2 = aggregator.ProcessReport(true, 1, true, 1, 0.21, 0.51, 110);

        Assert.NotNull(f2);
        Assert.Equal(1, f2.DownCount);
        Assert.Single(f2.Contacts);
        Assert.Equal(1, f2.Contacts[0].Id);
        Assert.Equal(1, aggregator.ActiveContactCount);
    }

    [Fact]
    public void ContactRemoval_ExplicitTipUp_RemovesContact()
    {
        var aggregator = new SerialFrameAggregator();

        // 1 finger down
        var f1 = aggregator.ProcessReport(true, 1, true, 1, 0.5, 0.5, 100);
        Assert.NotNull(f1);
        Assert.Equal(1, f1.DownCount);

        // Finger lifts with tip = false
        var f2 = aggregator.ProcessReport(true, 0, false, 1, 0.5, 0.5, 110);
        Assert.NotNull(f2);
        Assert.Equal(0, f2.DownCount);
        Assert.Empty(f2.Contacts);
        Assert.Equal(0, aggregator.ActiveContactCount);
    }

    [Fact]
    public void ContactRemoval_ZeroContactCount_EmitsEmptyFrame()
    {
        var aggregator = new SerialFrameAggregator();

        // 2 fingers down
        aggregator.ProcessReport(true, 2, true, 1, 0.2, 0.5, 100);
        aggregator.ProcessReport(true, 0, true, 2, 0.8, 0.5, 102);

        // Lift off: device reports Contact Count = 0
        var liftFrame = aggregator.ProcessReport(true, 0, false, 0, 0, 0, 115);

        Assert.NotNull(liftFrame);
        Assert.Equal(0, liftFrame.DownCount);
        Assert.Empty(liftFrame.Contacts);
        Assert.Equal(0, aggregator.ActiveContactCount);
    }

    [Fact]
    public void Timeout_IncompletePendingFrame_TimesOutAndDoesNotMixWithNextFrame()
    {
        var aggregator = new SerialFrameAggregator { PendingTimeoutMs = 50 };

        // Frame starts declaring 2 contacts, but only contact 1 arrives
        Assert.Null(aggregator.ProcessReport(true, 2, true, 1, 0.2, 0.5, 1000));
        Assert.True(aggregator.IsAggregating);

        // 80ms later (> 50ms timeout), a new frame arrives declaring 1 contact (ID 99)
        var f = aggregator.ProcessReport(true, 1, true, 99, 0.6, 0.6, 1080);

        // The stale pending frame should have timed out, and the new frame should emit cleanly with only ID 99
        Assert.NotNull(f);
        Assert.Equal(1, f.DownCount);
        Assert.Single(f.Contacts);
        Assert.Equal(99, f.Contacts[0].Id);
    }

    [Fact]
    public void Timeout_StaleActiveContacts_RemovedAndEmitsEmptyFrame()
    {
        var aggregator = new SerialFrameAggregator { ContactTimeoutMs = 150 };

        // 1 finger down at t = 1000
        var f1 = aggregator.ProcessReport(true, 1, true, 1, 0.5, 0.5, 1000);
        Assert.NotNull(f1);
        Assert.Equal(1, f1.DownCount);

        // Device goes completely silent. Check timeouts at t = 1100 (not expired yet)
        var f2 = aggregator.PruneTimeouts(1100);
        Assert.Null(f2);
        Assert.Equal(1, aggregator.ActiveContactCount);

        // Check timeouts at t = 1160 (> 150ms since last seen)
        var f3 = aggregator.PruneTimeouts(1160);
        Assert.NotNull(f3);
        Assert.Equal(0, f3.DownCount);
        Assert.Empty(f3.Contacts);
        Assert.Equal(0, aggregator.ActiveContactCount);
    }

    [Fact]
    public void MultipleDevices_HaveIndependentAggregationState()
    {
        var devA = new SerialFrameAggregator();
        var devB = new SerialFrameAggregator();

        // Device A receives contact 1 of 2
        Assert.Null(devA.ProcessReport(true, 2, true, 1, 0.1, 0.1, 100));
        Assert.True(devA.IsAggregating);
        Assert.False(devB.IsAggregating);

        // Device B receives 1 contact frame
        var fB = devB.ProcessReport(true, 1, true, 5, 0.5, 0.5, 100);
        Assert.NotNull(fB);
        Assert.Equal(1, fB.DownCount);

        // Device A is still waiting for contact 2
        Assert.True(devA.IsAggregating);

        // Device A completes
        var fA = devA.ProcessReport(true, 0, true, 2, 0.9, 0.9, 102);
        Assert.NotNull(fA);
        Assert.Equal(2, fA.DownCount);
    }
}
