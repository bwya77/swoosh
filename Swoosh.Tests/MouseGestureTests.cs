using Swoosh.Input;
using Swoosh.Native;
using Xunit;

namespace Swoosh.Tests;

public class MouseGestureTests
{
    [Fact]
    public void Captured_Left_Button_Does_Not_End_When_Async_Button_State_Is_Up()
    {
        using var listener = new MouseGestureListener(vk => vk == Win32.VK_CONTROL)
        {
            Enabled = true,
            ModifierVk = Win32.VK_CONTROL,
        };
        int moves = 0, releases = 0;
        listener.Pressed += _ => true;
        listener.Moved += _ => moves++;
        listener.Released += _ => releases++;

        Assert.True(listener.ProcessMessage(Win32.WM_LBUTTONDOWN, At(0, 0)));
        Thread.Sleep(200);
        listener.RecoverMissedRelease(At(120, 0).pt);
        Assert.False(listener.ProcessMessage(Win32.WM_MOUSEMOVE, At(120, 0)));
        Assert.Equal(0, releases);
        Assert.Equal(1, moves);
        Assert.True(listener.ProcessMessage(Win32.WM_LBUTTONUP, At(120, 0)));
        Assert.Equal(1, releases);
    }

    [Fact]
    public void Middle_Button_Still_Recovers_A_Missed_Release()
    {
        using var listener = new MouseGestureListener(_ => false) { Enabled = true };
        int releases = 0;
        listener.Pressed += _ => true;
        listener.Released += _ => releases++;

        Assert.False(listener.ProcessMessage(Win32.WM_MBUTTONDOWN, At(0, 0)));
        Thread.Sleep(200);
        listener.RecoverMissedRelease(At(120, 0).pt);
        Assert.Equal(1, releases);
        Assert.False(listener.ProcessMessage(Win32.WM_MBUTTONUP, At(120, 0)));
        Assert.Equal(1, releases);
    }

    [Theory]
    [InlineData(Win32.VK_CONTROL)]
    [InlineData(Win32.VK_SHIFT)]
    [InlineData(Win32.VK_MENU)]
    public void Modifier_Left_Click_Uses_The_Same_Gesture_Events_Until_Release(int modifier)
    {
        bool modifierHeld = true;
        using var listener = new MouseGestureListener(vk => vk != modifier || modifierHeld)
        {
            Enabled = true,
            ModifierVk = modifier,
        };
        int presses = 0, releases = 0;
        Win32.POINT moved = default, released = default;
        listener.Pressed += _ => { presses++; return true; };
        listener.Moved += pt => moved = pt;
        listener.Released += pt => { releases++; released = pt; };

        Assert.True(listener.ProcessMessage(Win32.WM_LBUTTONDOWN, At(10, 20)));
        Assert.Equal(modifier, listener.ActiveModifierVk);
        modifierHeld = false;
        Assert.False(listener.ProcessMessage(Win32.WM_MOUSEMOVE, At(110, 40)));
        Assert.True(listener.ProcessMessage(Win32.WM_LBUTTONUP, At(130, 50)));
        Assert.Equal(modifier, listener.ActiveModifierVk);
        Assert.Equal(1, presses);
        Assert.Equal(1, releases);
        Assert.Equal((110, 40), (moved.X, moved.Y));
        Assert.Equal((130, 50), (released.X, released.Y));
        Assert.False(listener.ProcessMessage(Win32.WM_LBUTTONUP, At(130, 50)));
    }

    [Theory]
    [InlineData(false, true, true, Win32.WM_LBUTTONDOWN, 0u)]
    [InlineData(true, false, true, Win32.WM_LBUTTONDOWN, 0u)]
    [InlineData(true, true, false, Win32.WM_LBUTTONDOWN, 0u)]
    [InlineData(true, true, true, Win32.WM_RBUTTONDOWN, 0u)]
    [InlineData(true, true, true, Win32.WM_LBUTTONDOWN, Win32.LLMHF_INJECTED)]
    public void Unmatched_Or_Rejected_Presses_Pass_Through(
        bool enabled, bool modifierHeld, bool canArm, int message, uint flags)
    {
        using var listener = new MouseGestureListener(_ => modifierHeld)
        {
            Enabled = enabled,
            ModifierVk = Win32.VK_CONTROL,
        };
        int moves = 0, releases = 0;
        listener.Pressed += _ => canArm;
        listener.Moved += _ => moves++;
        listener.Released += _ => releases++;
        var data = At(10, 20);
        data.flags = flags;

        Assert.False(listener.ProcessMessage(message, data));
        Assert.False(listener.ProcessMessage(Win32.WM_MOUSEMOVE, At(110, 20)));
        Assert.False(listener.ProcessMessage(Win32.WM_LBUTTONUP, At(110, 20)));
        Assert.Equal(0, moves);
        Assert.Equal(0, releases);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Middle_Button_Mode_Remains_Unchanged(bool modifierHeld)
    {
        using var listener = new MouseGestureListener(vk => vk != Win32.VK_CONTROL || modifierHeld)
        {
            Enabled = true,
        };
        int releases = 0, moves = 0;
        listener.Pressed += _ => true;
        listener.Moved += _ => moves++;
        listener.Released += _ => releases++;

        Assert.False(listener.ProcessMessage(Win32.WM_MBUTTONDOWN, At(0, 0)));
        Assert.Equal(0, listener.ActiveModifierVk);
        Assert.False(listener.ProcessMessage(Win32.WM_MOUSEMOVE, At(120, 0)));
        Assert.False(listener.ProcessMessage(Win32.WM_MBUTTONUP, At(120, 0)));
        Assert.Equal(1, moves);
        Assert.Equal(1, releases);
    }

    [Theory]
    [InlineData(Win32.VK_SHIFT)]
    [InlineData(Win32.VK_CONTROL)]
    [InlineData(Win32.VK_MENU)]
    public void Modifier_Mode_Does_Not_Also_Activate_With_Middle_Button(int modifier)
    {
        using var listener = new MouseGestureListener(_ => true)
        {
            Enabled = true,
            ModifierVk = modifier,
        };
        int presses = 0;
        listener.Pressed += _ => { presses++; return true; };

        Assert.False(listener.ProcessMessage(Win32.WM_MBUTTONDOWN, At(0, 0)));
        Assert.False(listener.ProcessMessage(Win32.WM_MBUTTONUP, At(120, 0)));
        Assert.Equal(0, presses);
    }

    [Fact]
    public void Modifier_Trigger_Can_Be_Used_Repeatedly_While_Modifier_Stays_Held()
    {
        using var listener = new MouseGestureListener(vk => vk == Win32.VK_SHIFT)
        {
            Enabled = true,
            ModifierVk = Win32.VK_SHIFT,
        };
        int presses = 0, releases = 0;
        listener.Pressed += _ => { presses++; return true; };
        listener.Released += _ => releases++;

        for (int i = 0; i < 3; i++)
        {
            Assert.True(listener.ProcessMessage(Win32.WM_LBUTTONDOWN, At(0, 0)));
            Assert.False(listener.ProcessMessage(Win32.WM_MOUSEMOVE, At(120, 0)));
            listener.RecoverMissedRelease(At(120, 0).pt);
            Assert.Equal(i, releases);
            Assert.True(listener.ProcessMessage(Win32.WM_LBUTTONUP, At(120, 0)));
        }

        Assert.Equal(3, presses);
        Assert.Equal(3, releases);
    }

    [Fact]
    public void Middle_Button_Mode_Does_Not_Also_Activate_With_Left_Click()
    {
        using var listener = new MouseGestureListener(_ => true) { Enabled = true };
        int presses = 0;
        listener.Pressed += _ => { presses++; return true; };

        Assert.False(listener.ProcessMessage(Win32.WM_LBUTTONDOWN, At(0, 0)));
        Assert.False(listener.ProcessMessage(Win32.WM_LBUTTONUP, At(120, 0)));
        Assert.Equal(0, presses);
    }

    [Fact]
    public void Cancel_And_Reconfigure_Still_Consume_Original_Release_Without_Committing()
    {
        using var listener = new MouseGestureListener(_ => true)
        {
            Enabled = true,
            ModifierVk = Win32.VK_CONTROL,
        };
        int presses = 0, releases = 0, moves = 0;
        listener.Pressed += _ => { presses++; return true; };
        listener.Released += _ => releases++;
        listener.Moved += _ => moves++;

        Assert.True(listener.ProcessMessage(Win32.WM_LBUTTONDOWN, At(0, 0)));
        listener.CancelTracking();
        listener.ModifierVk = Win32.VK_MENU;
        Assert.False(listener.ProcessMessage(Win32.WM_LBUTTONDOWN, At(0, 0)));
        Assert.False(listener.ProcessMessage(Win32.WM_MOUSEMOVE, At(120, 0)));
        listener.Enabled = false;
        Assert.True(listener.ProcessMessage(Win32.WM_LBUTTONUP, At(120, 0)));
        Assert.Equal(1, presses);
        Assert.Equal(0, moves);
        Assert.Equal(0, releases);

        listener.Enabled = true;
        Assert.True(listener.ProcessMessage(Win32.WM_LBUTTONDOWN, At(0, 0)));
        Assert.Equal(Win32.VK_MENU, listener.ActiveModifierVk);
        Assert.True(listener.ProcessMessage(Win32.WM_LBUTTONUP, At(120, 0)));
        Assert.Equal(2, presses);
        Assert.Equal(1, releases);
    }

    [Fact]
    public void Injected_And_Unrelated_Releases_Do_Not_End_An_Active_Gesture()
    {
        using var listener = new MouseGestureListener(_ => true)
        {
            Enabled = true,
            ModifierVk = Win32.VK_CONTROL,
        };
        int releases = 0;
        listener.Pressed += _ => true;
        listener.Released += _ => releases++;
        Assert.True(listener.ProcessMessage(Win32.WM_LBUTTONDOWN, At(0, 0)));

        var injected = At(120, 0);
        injected.flags = Win32.LLMHF_INJECTED;
        Assert.False(listener.ProcessMessage(Win32.WM_LBUTTONUP, injected));
        Assert.False(listener.ProcessMessage(Win32.WM_MBUTTONUP, At(120, 0)));
        Assert.Equal(0, releases);
        Assert.True(listener.ProcessMessage(Win32.WM_LBUTTONUP, At(120, 0)));
        Assert.Equal(1, releases);
    }

    private static Win32.MSLLHOOKSTRUCT At(int x, int y) => new()
    {
        pt = new Win32.POINT { X = x, Y = y },
    };
}
