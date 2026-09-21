using Swoosh.Input;
using Xunit;

namespace Swoosh.Tests;

public class TouchpadParserSerialTests
{
    #region TouchpadParser.Parse Level Tests

    [Fact]
    public void Parse_SingleCall_AggregatesContinuationReports()
    {
        var parser = new TouchpadParser();
        var dev = new IntPtr(0x1001);
        parser.RegisterSerialDeviceForTesting(dev, minX: 0, maxX: 1000, minY: 0, maxY: 1000);

        // Single Parse call with a 2-report batch: report 0 declares CC=2, report 1 is continuation (CC=0)
        var rep1 = new TouchpadParser.TestSerialReportPayload
        {
            IsTouchReport = 1,
            HasContactCount = 1,
            ContactCount = 2,
            Tip = 1,
            ContactId = 1,
            RawX = 200,
            RawY = 500
        };
        var rep2 = new TouchpadParser.TestSerialReportPayload
        {
            IsTouchReport = 1,
            HasContactCount = 1,
            ContactCount = 0,
            Tip = 1,
            ContactId = 2,
            RawX = 800,
            RawY = 500
        };

        var frames = parser.ParseForTesting(dev, rep1, rep2);

        Assert.Single(frames);
        var frame = frames[0];
        Assert.Equal(2, frame.DownCount);
        Assert.Equal(2, frame.Contacts.Count);
        Assert.Contains(frame.Contacts, c => c.Id == 1 && Math.Abs(c.X - 0.2) < 0.001);
        Assert.Contains(frame.Contacts, c => c.Id == 2 && Math.Abs(c.X - 0.8) < 0.001);
    }

    [Fact]
    public void Parse_SplitCalls_RetainsPendingStateAcrossCalls()
    {
        var parser = new TouchpadParser();
        var dev = new IntPtr(0x1002);
        parser.RegisterSerialDeviceForTesting(dev, minX: 0, maxX: 1000, minY: 0, maxY: 1000);

        // Call 1: First WM_INPUT payload delivers contact 1 of 2
        var rep1 = new TouchpadParser.TestSerialReportPayload
        {
            IsTouchReport = 1,
            HasContactCount = 1,
            ContactCount = 2,
            Tip = 1,
            ContactId = 10,
            RawX = 300,
            RawY = 400
        };
        var frames1 = parser.ParseForTesting(dev, rep1);
        Assert.Empty(frames1);

        // Call 2: Second WM_INPUT payload delivers contact 2 of 2
        var rep2 = new TouchpadParser.TestSerialReportPayload
        {
            IsTouchReport = 1,
            HasContactCount = 1,
            ContactCount = 0,
            Tip = 1,
            ContactId = 20,
            RawX = 700,
            RawY = 600
        };
        var frames2 = parser.ParseForTesting(dev, rep2);

        Assert.Single(frames2);
        var frame = frames2[0];
        Assert.Equal(2, frame.DownCount);
        Assert.Contains(frame.Contacts, c => c.Id == 10 && Math.Abs(c.X - 0.3) < 0.001);
        Assert.Contains(frame.Contacts, c => c.Id == 20 && Math.Abs(c.X - 0.7) < 0.001);
    }

    [Fact]
    public void Parse_DifferentDeviceHandles_MaintainsStateIsolation()
    {
        var parser = new TouchpadParser();
        var devA = new IntPtr(0x2001);
        var devB = new IntPtr(0x2002);

        parser.RegisterSerialDeviceForTesting(devA, minX: 0, maxX: 1000, minY: 0, maxY: 1000);
        parser.RegisterSerialDeviceForTesting(devB, minX: 0, maxX: 1000, minY: 0, maxY: 1000);

        // Device A receives contact 1 of a 2-finger gesture
        var repA1 = new TouchpadParser.TestSerialReportPayload
        {
            IsTouchReport = 1,
            HasContactCount = 1,
            ContactCount = 2,
            Tip = 1,
            ContactId = 1,
            RawX = 100,
            RawY = 100
        };
        var framesA1 = parser.ParseForTesting(devA, repA1);
        Assert.Empty(framesA1);

        // Device B receives a complete 1-finger gesture
        var repB1 = new TouchpadParser.TestSerialReportPayload
        {
            IsTouchReport = 1,
            HasContactCount = 1,
            ContactCount = 1,
            Tip = 1,
            ContactId = 5,
            RawX = 500,
            RawY = 500
        };
        var framesB = parser.ParseForTesting(devB, repB1);
        Assert.Single(framesB);
        Assert.Equal(1, framesB[0].DownCount);
        Assert.Equal(5, framesB[0].Contacts[0].Id);

        // Device A now receives contact 2 of 2
        var repA2 = new TouchpadParser.TestSerialReportPayload
        {
            IsTouchReport = 1,
            HasContactCount = 1,
            ContactCount = 0,
            Tip = 1,
            ContactId = 2,
            RawX = 900,
            RawY = 900
        };
        var framesA2 = parser.ParseForTesting(devA, repA2);
        Assert.Single(framesA2);
        Assert.Equal(2, framesA2[0].DownCount);
        Assert.Contains(framesA2[0].Contacts, c => c.Id == 1);
        Assert.Contains(framesA2[0].Contacts, c => c.Id == 2);
    }

    [Fact]
    public void Parse_NonTouchReport_SkippedWithoutAffectingTouchFrame()
    {
        var parser = new TouchpadParser();
        var dev = new IntPtr(0x1003);
        parser.RegisterSerialDeviceForTesting(dev, minX: 0, maxX: 1000, minY: 0, maxY: 1000);

        var mouseRep = new TouchpadParser.TestSerialReportPayload
        {
            IsTouchReport = 0 // e.g. Mouse button / pointer packet
        };
        var touchRep = new TouchpadParser.TestSerialReportPayload
        {
            IsTouchReport = 1,
            HasContactCount = 1,
            ContactCount = 1,
            Tip = 1,
            ContactId = 1,
            RawX = 400,
            RawY = 400
        };

        var frames = parser.ParseForTesting(dev, mouseRep, touchRep);
        Assert.Single(frames);
        Assert.Equal(1, frames[0].DownCount);
        Assert.Equal(1, frames[0].Contacts[0].Id);
    }

    [Fact]
    public void Parse_CoordinateNormalization_ClampsAndScales()
    {
        var parser = new TouchpadParser();
        var dev = new IntPtr(0x1004);
        // Range: X in [100, 1100] (span 1000), Y in [200, 1200] (span 1000)
        parser.RegisterSerialDeviceForTesting(dev, minX: 100, maxX: 1100, minY: 200, maxY: 1200);

        var repInBounds = new TouchpadParser.TestSerialReportPayload
        {
            IsTouchReport = 1,
            HasContactCount = 1,
            ContactCount = 1,
            Tip = 1,
            ContactId = 1,
            RawX = 600,  // (600 - 100) / 1000 = 0.5
            RawY = 700   // (700 - 200) / 1000 = 0.5
        };
        var frames1 = parser.ParseForTesting(dev, repInBounds);
        Assert.Single(frames1);
        Assert.Equal(0.5, frames1[0].Contacts[0].X, 3);
        Assert.Equal(0.5, frames1[0].Contacts[0].Y, 3);

        // Clamping check: rawX below minX clamped to 0, rawY above maxY clamped to 1
        var repOutOfBounds = new TouchpadParser.TestSerialReportPayload
        {
            IsTouchReport = 1,
            HasContactCount = 1,
            ContactCount = 1,
            Tip = 1,
            ContactId = 1,
            RawX = 50,   // Below 100 -> clamped to 0.0
            RawY = 1500  // Above 1200 -> clamped to 1.0
        };
        var frames2 = parser.ParseForTesting(dev, repOutOfBounds);
        Assert.Single(frames2);
        Assert.Equal(0.0, frames2[0].Contacts[0].X, 3);
        Assert.Equal(1.0, frames2[0].Contacts[0].Y, 3);
    }

    [Fact]
    public void Parse_ZeroContactCount_EmitsLiftFrame()
    {
        var parser = new TouchpadParser();
        var dev = new IntPtr(0x1005);
        parser.RegisterSerialDeviceForTesting(dev);

        // 1. Initial 1-finger frame
        var down = new TouchpadParser.TestSerialReportPayload
        {
            IsTouchReport = 1,
            HasContactCount = 1,
            ContactCount = 1,
            Tip = 1,
            ContactId = 1,
            RawX = 500,
            RawY = 500
        };
        var fDown = parser.ParseForTesting(dev, down);
        Assert.Single(fDown);
        Assert.Equal(1, fDown[0].DownCount);

        // 2. All fingers lifted (CC = 0)
        var lift = new TouchpadParser.TestSerialReportPayload
        {
            IsTouchReport = 1,
            HasContactCount = 1,
            ContactCount = 0,
            Tip = 0,
            ContactId = 0,
            RawX = 0,
            RawY = 0
        };
        var fLift = parser.ParseForTesting(dev, lift);
        Assert.Single(fLift);
        Assert.Equal(0, fLift[0].DownCount);
        Assert.Empty(fLift[0].Contacts);
    }

    [Fact]
    public void Parse_StationaryHold_ResumedInputAfterDelay_DoesNotEmitEmptyLiftFrame()
    {
        var parser = new TouchpadParser();
        var dev = new IntPtr(0x1006);
        parser.RegisterSerialDeviceForTesting(dev);

        // 1. User places two fingers down (2-report batch)
        var rep1 = new TouchpadParser.TestSerialReportPayload
        {
            IsTouchReport = 1,
            HasContactCount = 1,
            ContactCount = 2,
            Tip = 1,
            ContactId = 1,
            RawX = 200,
            RawY = 500
        };
        var rep2 = new TouchpadParser.TestSerialReportPayload
        {
            IsTouchReport = 1,
            HasContactCount = 1,
            ContactCount = 0,
            Tip = 1,
            ContactId = 2,
            RawX = 800,
            RawY = 500
        };
        var fInitial = parser.ParseForTesting(dev, rep1, rep2);
        Assert.Single(fInitial);
        Assert.Equal(2, fInitial[0].DownCount);

        // 2. User holds motionless (stationary hold).
        // Device is silent for 250ms (exceeding the 200ms gesture hold threshold).
        // Input then resumes with continuing hold/movement.
        var repResumed1 = new TouchpadParser.TestSerialReportPayload
        {
            IsTouchReport = 1,
            HasContactCount = 1,
            ContactCount = 2,
            Tip = 1,
            ContactId = 1,
            RawX = 205,
            RawY = 505
        };
        var repResumed2 = new TouchpadParser.TestSerialReportPayload
        {
            IsTouchReport = 1,
            HasContactCount = 1,
            ContactCount = 0,
            Tip = 1,
            ContactId = 2,
            RawX = 805,
            RawY = 505
        };

        var fResumed = parser.ParseForTesting(dev, repResumed1, repResumed2);

        // CRITICAL CHECK: Resumed valid input must NOT be preceded by a synthetic empty frame!
        Assert.Single(fResumed);
        Assert.Equal(2, fResumed[0].DownCount);
        Assert.Equal(2, fResumed[0].Contacts.Count);
        Assert.DoesNotContain(fResumed, f => f.DownCount == 0);
    }

    [Fact]
    public void Parse_OrphanedContinuation_AfterPendingTimeout_PreservesActiveContacts()
    {
        var parser = new TouchpadParser();
        var dev = new IntPtr(0x1007);
        parser.RegisterSerialDeviceForTesting(dev, minX: 0, maxX: 1000, minY: 0, maxY: 1000);

        // Set a short pending timeout for this test via the aggregator.
        var agg = parser.GetSerialAggregatorForTesting(dev)!;
        agg.PendingTimeoutMs = 50;

        // Step 1: Establish an active two-contact frame at t=1000.
        var frames1 = parser.ParseForTesting(dev,
            new TouchpadParser.TestSerialReportPayload
            {
                IsTouchReport = 1, HasContactCount = 1, ContactCount = 2,
                Tip = 1, ContactId = 1, RawX = 200, RawY = 500, TimestampMs = 1000
            },
            new TouchpadParser.TestSerialReportPayload
            {
                IsTouchReport = 1, HasContactCount = 1, ContactCount = 0,
                Tip = 1, ContactId = 2, RawX = 800, RawY = 500, TimestampMs = 1002
            });
        Assert.Single(frames1);
        Assert.Equal(2, frames1[0].DownCount);
        Assert.Equal(2, agg.ActiveContactCount);

        // Step 2: Start another two-contact frame at t=1100, but send only its first report.
        var frames2 = parser.ParseForTesting(dev,
            new TouchpadParser.TestSerialReportPayload
            {
                IsTouchReport = 1, HasContactCount = 1, ContactCount = 2,
                Tip = 1, ContactId = 1, RawX = 210, RawY = 510, TimestampMs = 1100
            });
        Assert.Empty(frames2); // Aggregating, waiting for contact 2
        Assert.True(agg.IsAggregating);

        // Step 3+4: Time advances beyond PendingTimeoutMs (50ms). The delayed
        // continuation arrives at t=1200 (100ms after the frame started) with
        // CC=0 and Tip=true. The pending frame should have timed out, and this
        // orphaned continuation must be silently ignored — it must NOT clear the
        // active contacts or emit an empty lift frame.
        var frames3 = parser.ParseForTesting(dev,
            new TouchpadParser.TestSerialReportPayload
            {
                IsTouchReport = 1, HasContactCount = 1, ContactCount = 0,
                Tip = 1, ContactId = 2, RawX = 810, RawY = 510, TimestampMs = 1200
            });

        // Step 5: Verify — no frame emitted, and both original active contacts preserved.
        Assert.Empty(frames3);
        Assert.Equal(2, agg.ActiveContactCount);

        // Step 6: Send a new complete two-contact frame and verify normal parsing continues.
        var frames4 = parser.ParseForTesting(dev,
            new TouchpadParser.TestSerialReportPayload
            {
                IsTouchReport = 1, HasContactCount = 1, ContactCount = 2,
                Tip = 1, ContactId = 1, RawX = 220, RawY = 520, TimestampMs = 1300
            },
            new TouchpadParser.TestSerialReportPayload
            {
                IsTouchReport = 1, HasContactCount = 1, ContactCount = 0,
                Tip = 1, ContactId = 2, RawX = 820, RawY = 520, TimestampMs = 1302
            });
        Assert.Single(frames4);
        Assert.Equal(2, frames4[0].DownCount);
        Assert.Contains(frames4[0].Contacts, c => c.Id == 1);
        Assert.Contains(frames4[0].Contacts, c => c.Id == 2);

        // Step 7: Send CC=0 with Tip=false (real lift) and verify the lift still works.
        var frames5 = parser.ParseForTesting(dev,
            new TouchpadParser.TestSerialReportPayload
            {
                IsTouchReport = 1, HasContactCount = 1, ContactCount = 0,
                Tip = 0, ContactId = 0, RawX = 0, RawY = 0, TimestampMs = 1400
            });
        Assert.Single(frames5);
        Assert.Equal(0, frames5[0].DownCount);
        Assert.Empty(frames5[0].Contacts);
        Assert.Equal(0, agg.ActiveContactCount);
    }

    #endregion

    #region SerialFrameAggregator Core Logic Tests

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

    #endregion
}
