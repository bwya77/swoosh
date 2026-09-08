using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;
using Microsoft.Win32;
using Swoosh.Native;
using Swoosh.Settings;
using Swoosh.Snapping;
using Color = System.Windows.Media.Color;
using Brushes = System.Windows.Media.Brushes;
using FontWeights = System.Windows.FontWeights;
using HorizontalAlignment = System.Windows.HorizontalAlignment;
using VerticalAlignment = System.Windows.VerticalAlignment;
using Button = System.Windows.Controls.Button;
using Orientation = System.Windows.Controls.Orientation;
using Image = System.Windows.Controls.Image;
using Point = System.Windows.Point;

namespace Swoosh.UI;

/// <summary>
/// Windows 11-style "Snap Assist": right after a window snaps into a zone, opens one small
/// overlay window per remaining empty zone (not a single window spanning the whole work area -
/// that would blur over the zone you *just* filled too), each tiled with cards for the user's
/// other open windows so one click fills that slot, the same idea as the OS's built-in Snap
/// layouts overlay. Each card is a live DWM thumbnail (<c>DwmRegisterThumbnail</c>) of the actual
/// window content, the same GPU-composited preview mechanism behind Alt+Tab and the real Snap
/// Assist, rather than a flat icon; windows DWM can't thumbnail (registration fails) fall back to
/// a big icon. Dismisses on Esc, on losing focus (click elsewhere), on picking a window, or after
/// a short idle timeout.
/// </summary>
public sealed class SnapAssistOverlay
{
    private const int ThumbW = 220, ThumbH = 130;
    private const long ShowGraceMs = 600;

    private readonly DispatcherTimer _timeout = new() { Interval = TimeSpan.FromSeconds(8) };
    // Polls GetForegroundWindow() instead of relying on WM_ACTIVATE/Window.Deactivated: with
    // several sibling topmost windows (quarters = up to 3) plus Win32.ForceForeground being
    // called on every picked candidate, activation churn between our own windows and the ones we
    // just placed made Deactivated fire unpredictably (sometimes for a window we deliberately
    // moved focus to ourselves), which is what kept incorrectly closing every remaining slot.
    // Polling what's *actually* in the foreground and comparing against an explicit allowlist
    // sidesteps that race entirely.
    private readonly DispatcherTimer _focusPoll = new() { Interval = TimeSpan.FromMilliseconds(200) };
    private readonly HashSet<IntPtr> _acceptableForeground = new();
    private Action<IntPtr, SnapZone>? _onPick;
    private long _shownAtTick;
    private bool _settingUp;

    // Follows the same HudTheme setting as the gesture HUD/preview (Dark, Light, or System -
    // read from Windows' own light/dark app theme). The raw setting is stored, not a cached
    // resolved bool: when it's System, the Windows theme can change at any time without Swoosh's
    // own settings changing (ApplyAppearance only re-runs when *our* settings.json changes), so
    // it must be re-read fresh each time the overlay is actually shown, not cached.
    private HudTheme _hudTheme = HudTheme.Dark;

    /// <summary>One live-thumbnail card: the window it previews, the transparent placeholder
    /// DWM paints into, and the DWM thumbnail handle (null if registration failed).</summary>
    private sealed class ThumbCard
    {
        public Border Placeholder = null!;
        public IntPtr? Thumb;
    }

    /// <summary>One empty-zone overlay: its own small topmost window sized exactly to that
    /// zone's rect (so its blur never covers a zone the user already filled), plus the
    /// thumbnail cards inside it.</summary>
    private sealed class SlotWindow
    {
        public Win32.RECT Rect;
        public Window Win = null!;
        public readonly List<ThumbCard> Cards = new();
    }

    private readonly List<SlotWindow> _slotWindows = new();
    // Resolved light/dark for the slot windows currently being built - set once per Show(), read
    // by CreateSlotWindow/BuildCandidateCard, so every window in one Snap Assist session matches
    // even if the system theme somehow changed mid-session.
    private bool _light;

    public SnapAssistOverlay()
    {
        _timeout.Tick += (_, _) => Hide();
        _focusPoll.Tick += (_, _) => CheckFocusLost();
    }

    private void CheckFocusLost()
    {
        if (_slotWindows.Count == 0) { _focusPoll.Stop(); return; }
        if ((Environment.TickCount64 - _shownAtTick) < ShowGraceMs) return;
        IntPtr fg = Win32.GetForegroundWindow();
        // IntPtr.Zero is a transient "nothing has focus yet" state (e.g. mid-transition between
        // windows) - don't treat that as "clicked away".
        if (fg == IntPtr.Zero || _acceptableForeground.Contains(fg)) return;
        Hide();
    }

    /// <summary>Match the overlay's blur tint and card colors to the gesture HUD's backdrop
    /// theme: Dark, Light, or System (read from Windows' own light/dark app theme).</summary>
    public void ApplyAppearance(HudTheme hudTheme)
    {
        _hudTheme = hudTheme;
    }

    private bool ResolveLightTheme() => _hudTheme switch
    {
        HudTheme.Light => true,
        HudTheme.Dark => false,
        _ => SystemUsesLightTheme(),
    };

    private static bool SystemUsesLightTheme()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(
                @"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");
            if (key?.GetValue("AppsUseLightTheme") is int v) return v != 0;
        }
        catch { /* default to dark on any failure */ }
        return false;
    }

    /// <summary>
    /// Open one overlay per complementary zone, each offering all of the caller's other open
    /// windows. <paramref name="work"/> is the monitor work area in physical pixels;
    /// <paramref name="zones"/> are the empty zones left to fill (from
    /// <see cref="SnapAssistZones.ComplementOf"/>); <paramref name="candidates"/> are pick-able
    /// windows (the caller should already exclude the window that was just snapped). Picking any
    /// candidate hands it and its target zone to <paramref name="onPick"/> and dismisses every
    /// slot's overlay.
    /// </summary>
    public void Show(Win32.RECT work, IReadOnlyList<SnapZone> zones, IReadOnlyList<AppWindow> candidates,
        Action<IntPtr, SnapZone> onPick)
    {
        if (zones.Count == 0 || candidates.Count == 0 || work.Width <= 0 || work.Height <= 0) return;

        // Defer to the next dispatcher pass: the caller (SwooshController) still runs
        // Win32.ForceForeground(justSnappedWindow) synchronously right after this call returns,
        // to restore focus to the snapped window as the gesture ends. Showing/activating our
        // overlay immediately would just get its focus stolen back a moment later. Running
        // after that settles means our Activate() below actually sticks.
        Dispatcher.CurrentDispatcher.BeginInvoke(DispatcherPriority.Background, new Action(() =>
            ShowCore(work, zones, candidates, onPick)));
    }

    private void ShowCore(Win32.RECT work, IReadOnlyList<SnapZone> zones, IReadOnlyList<AppWindow> candidates,
        Action<IntPtr, SnapZone> onPick)
    {
        Swoosh.Log.Write($"SnapAssist ShowCore: zones=[{string.Join(",", zones)}] candidates={candidates.Count} fgAtStart=0x{Win32.GetForegroundWindow().ToInt64():X}");
        // Defensive: keeps Hide() from doing anything if it's ever triggered while this method
        // is still building/showing windows (e.g. from a stray key/mouse event).
        _settingUp = true;
        try
        {
            TeardownSlotWindows();
            _onPick = onPick;
            _light = ResolveLightTheme();
            _acceptableForeground.Clear();

            foreach (var zone in zones)
            {
                var rect = WindowSnapper.ZoneRect(work, zone);
                if (rect.Width <= 0 || rect.Height <= 0) continue;
                // Only the first (primary) slot window is a normal, genuinely-activatable window
                // (so Esc/keyboard input has somewhere to land); the rest are NOACTIVATE so they
                // don't fight it for foreground. Dismiss-on-click-away no longer depends on which
                // one is "active" - see CheckFocusLost.
                _slotWindows.Add(CreateSlotWindow(rect, zone, candidates, primary: _slotWindows.Count == 0));
            }
            if (_slotWindows.Count == 0) { Swoosh.Log.Write("SnapAssist ShowCore: no valid zones, aborting"); return; }

            foreach (var slot in _slotWindows)
            {
                IntPtr h = new WindowInteropHelper(slot.Win).Handle;
                _acceptableForeground.Add(h);
                Win32.SetWindowPos(h, Win32.HWND_TOPMOST,
                    slot.Rect.Left, slot.Rect.Top, slot.Rect.Width, slot.Rect.Height, Win32.SWP_NOACTIVATE);
                slot.Win.Show();
                slot.Win.UpdateLayout();
                UpdateThumbnails(slot);
            }
            // Plain Window.Activate() silently fails here (Windows' foreground-lock timeout -
            // the same thing Win32.ForceForeground exists to defeat elsewhere in this codebase):
            // logging showed GetForegroundWindow() staying on the previously-foreground window
            // even right after calling Activate(), which meant the focus poll saw "foreground
            // isn't ours" immediately and closed everything a moment later - it wasn't wrong,
            // our window genuinely never became foreground in the first place. Even
            // ForceForeground's AttachThreadInput trick alone proved not always enough on rapid
            // re-triggers, so it now also defeats the SPI foreground-lock timeout directly - but
            // retry once more here as a last-resort safety net in case some other heuristic still
            // blocks it, rather than let the focus poll dismiss the overlay it never even showed.
            IntPtr primaryHwnd = new WindowInteropHelper(_slotWindows[0].Win).Handle;
            Win32.ForceForeground(primaryHwnd);
            if (Win32.GetForegroundWindow() != primaryHwnd)
                Win32.ForceForeground(primaryHwnd);
            Swoosh.Log.Write($"SnapAssist ShowCore: shown {_slotWindows.Count} slot(s), fg=0x{Win32.GetForegroundWindow().ToInt64():X}, primaryHwnd=0x{primaryHwnd.ToInt64():X}");

            // Set right before the poll starts, not before setup began: building windows and
            // registering DWM thumbnails (a cross-process call per candidate, per slot) can take
            // long enough on its own to eat most of the grace period, leaving CheckFocusLost's
            // very first tick almost unprotected right when activation is still settling.
            _shownAtTick = Environment.TickCount64;
            _timeout.Stop();
            _timeout.Start();
            _focusPoll.Start();
        }
        finally
        {
            _settingUp = false;
        }
    }

    private SlotWindow CreateSlotWindow(Win32.RECT rect, SnapZone zone, IReadOnlyList<AppWindow> candidates, bool primary)
    {
        var wrap = new WrapPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Center };

        var win = new Window
        {
            WindowStyle = WindowStyle.None,
            // Needs per-pixel transparency for the blur-behind call below to have any effect;
            // a normal opaque window would need DWMWA_SYSTEMBACKDROP_TYPE instead (that path
            // painted the whole overlay solid black - wrong technique for a transparent window).
            AllowsTransparency = true,
            // Alpha=1, not 0: with AllowsTransparency, Windows uses the *actual rendered alpha*
            // to decide hit-testing at the OS level - truly zero-alpha (Brushes.Transparent)
            // pixels don't just look invisible, they let clicks fall straight through to
            // whatever's behind before our own click-to-cancel handler ever sees them. Alpha=1
            // is visually indistinguishable but keeps the whole window clickable.
            Background = new SolidColorBrush(Color.FromArgb(1, 0, 0, 0)),
            ShowInTaskbar = false,
            Topmost = true,
            ResizeMode = ResizeMode.NoResize,
            ShowActivated = true,
            Content = new ScrollViewer
            {
                VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
                Content = new Grid { Children = { wrap }, VerticalAlignment = VerticalAlignment.Center },
            },
            Width = 10,
            Height = 10,
            Left = -10000,
            Top = -10000,
        };
        win.SourceInitialized += (_, _) =>
        {
            IntPtr h = new WindowInteropHelper(win).Handle;
            long ex = Win32.GetWindowLong(h, Win32.GWL_EXSTYLE);
            ex |= Win32.WS_EX_TOOLWINDOW | Win32.WS_EX_LAYERED;
            // Only non-primary slots get NOACTIVATE (so they're clickable without stealing
            // foreground from the primary slot). The primary slot stays genuinely activatable
            // purely so Esc/keyboard input has somewhere to land - dismiss-on-click-away is
            // handled separately by polling GetForegroundWindow (see CheckFocusLost), not by
            // this window's activation state.
            if (!primary) ex |= Win32.WS_EX_NOACTIVATE;
            Win32.SetWindowLongPtr(h, Win32.GWL_EXSTYLE, new IntPtr(ex));
            // Blur whatever's behind this slot (desktop, other windows), tinted to match the
            // gesture HUD's own light/dark theme, same undocumented accent-policy call several
            // WPF acrylic implementations use for a transparent window - unlike
            // DWMWA_SYSTEMBACKDROP_TYPE, this one actually works together with
            // AllowsTransparency instead of requiring an opaque window.
            if (_light)
                Win32.EnableAcrylicBlurBehind(h, a: 150, r: 245, g: 245, b: 248);
            else
                Win32.EnableAcrylicBlurBehind(h, a: 130, r: 12, g: 12, b: 16);
        };
        var slot = new SlotWindow { Rect = rect, Win = win };
        win.PreviewKeyDown += (_, e) => { if (e.Key == Key.Escape) Hide(); };
        // Clicking anything that isn't one of the candidate cards (i.e. the blurred background
        // of this slot) means "I'm done with Snap Assist" - dismiss every remaining slot, same
        // as Esc or clicking a genuinely different app. Only *picking* a candidate closes just
        // that one zone (see OnCandidateClick), leaving the other empty zones open.
        win.PreviewMouseDown += (_, e) => { if (!IsWithinButton(e.OriginalSource as DependencyObject)) Hide(); };

        win.SizeChanged += (_, _) => { win.UpdateLayout(); UpdateThumbnails(slot); };

        // Force the hwnd to exist (SourceInitialized above) before registering thumbnails
        // against it, without actually showing it on screen yet.
        win.Show();
        win.Hide();
        IntPtr destHwnd = new WindowInteropHelper(win).Handle;

        foreach (var app in candidates)
            wrap.Children.Add(BuildCandidateCard(app, zone, destHwnd, slot));

        return slot;
    }

    private Button BuildCandidateCard(AppWindow app, SnapZone zone, IntPtr destHwnd, SlotWindow slot)
    {
        // Transparent by default: DWM paints the live thumbnail directly over this rect once
        // registered, so nothing WPF draws here must occlude it.
        var placeholder = new Border { Background = Brushes.Transparent };

        var card = new ThumbCard { Placeholder = placeholder };
        if (Win32.DwmRegisterThumbnail(destHwnd, app.Hwnd, out IntPtr thumb) == 0)
            card.Thumb = thumb;
        else if (app.Icon != null)
            // DWM couldn't thumbnail this window (e.g. UWP host quirks) - fall back to a big icon
            // so the slot isn't left blank.
            placeholder.Child = new Image
            {
                Source = app.Icon,
                Width = 56,
                Height = 56,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
            };
        slot.Cards.Add(card);

        var labelStack = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(8, 4, 8, 4) };
        if (app.Icon != null)
            labelStack.Children.Add(new Image { Source = app.Icon, Width = 16, Height = 16, Margin = new Thickness(0, 0, 6, 0) });
        labelStack.Children.Add(new TextBlock
        {
            Text = app.Title,
            Foreground = _light ? new SolidColorBrush(Color.FromRgb(20, 20, 22)) : Brushes.White,
            FontSize = 12,
            FontWeight = FontWeights.SemiBold,
            TextTrimming = TextTrimming.CharacterEllipsis,
            TextWrapping = TextWrapping.NoWrap,
            MaxWidth = ThumbW - 40,
            VerticalAlignment = VerticalAlignment.Center,
        });
        var label = new Border
        {
            Background = _light
                ? new SolidColorBrush(Color.FromArgb(200, 245, 245, 248))
                : new SolidColorBrush(Color.FromArgb(190, 20, 20, 24)),
            VerticalAlignment = VerticalAlignment.Bottom,
            HorizontalAlignment = HorizontalAlignment.Stretch,
            Child = labelStack,
        };

        var grid = new Grid { Width = ThumbW, Height = ThumbH };
        grid.Children.Add(placeholder);
        grid.Children.Add(label);

        var button = new Button
        {
            Content = grid,
            Background = _light
                ? new SolidColorBrush(Color.FromArgb(25, 0, 0, 0))
                : new SolidColorBrush(Color.FromArgb(20, 255, 255, 255)),
            BorderThickness = new Thickness(1),
            BorderBrush = _light
                ? new SolidColorBrush(Color.FromArgb(45, 0, 0, 0))
                : new SolidColorBrush(Color.FromArgb(35, 255, 255, 255)),
            Padding = new Thickness(0),
            Margin = new Thickness(8),
            Cursor = System.Windows.Input.Cursors.Hand,
            Tag = (app.Hwnd, zone, slot),
        };
        button.Click += OnCandidateClick;
        return button;
    }

    private void OnCandidateClick(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: (IntPtr hwnd, SnapZone zone, SlotWindow slot) }) return;
        var callback = _onPick;
        // Only this zone's slot closes - picking a candidate for one quarter shouldn't dismiss
        // the other quarters' still-empty overlays.
        CloseSlot(slot);
        // The callback (SwooshController) calls Win32.ForceForeground on the picked window right
        // after this, making it the foreground window - explicitly allow that so the focus poll
        // (CheckFocusLost) doesn't treat "the app the user just placed became foreground" as
        // "the user clicked away" and close every other still-open slot.
        _acceptableForeground.Add(hwnd);
        callback?.Invoke(hwnd, zone);
    }

    /// <summary>True if <paramref name="d"/> is a candidate <see cref="Button"/> or one of its
    /// visual children (icon/label inside it), used to tell a real card click apart from a
    /// click on the slot's blurred background, which should cancel Snap Assist instead.</summary>
    private static bool IsWithinButton(DependencyObject? d)
    {
        while (d != null)
        {
            if (d is Button) return true;
            d = VisualTreeHelper.GetParent(d);
        }
        return false;
    }

    /// <summary>Move every registered DWM thumbnail in this slot to sit exactly under its
    /// (now laid-out) placeholder, fit letterboxed to the source window's real aspect ratio,
    /// and hide any thumbnail whose card has scrolled outside the slot window's bounds.</summary>
    private void UpdateThumbnails(SlotWindow slot)
    {
        double aw = slot.Win.ActualWidth, ah = slot.Win.ActualHeight;
        if (aw <= 0 || ah <= 0 || slot.Rect.Width <= 0) return;
        double scale = slot.Rect.Width / aw;

        foreach (var card in slot.Cards)
        {
            if (card.Thumb is not { } thumb) continue;

            Point topLeft;
            try { topLeft = card.Placeholder.TransformToAncestor(slot.Win).Transform(new Point(0, 0)); }
            catch (InvalidOperationException) { continue; } // not connected to the visual tree yet

            double w = card.Placeholder.ActualWidth, h = card.Placeholder.ActualHeight;
            var destRect = new Win32.RECT
            {
                Left = (int)Math.Round(topLeft.X * scale),
                Top = (int)Math.Round(topLeft.Y * scale),
                Right = (int)Math.Round((topLeft.X + w) * scale),
                Bottom = (int)Math.Round((topLeft.Y + h) * scale),
            };

            // Hide (rather than let float outside) a card that's scrolled fully out of this
            // slot window's own bounds.
            bool onScreen = w > 0 && h > 0 && destRect.Right > 0 && destRect.Bottom > 0 &&
                destRect.Left < slot.Rect.Width && destRect.Top < slot.Rect.Height;

            var fitted = onScreen ? FitAspect(thumb, destRect) : destRect;
            var props = new Win32.DWM_THUMBNAIL_PROPERTIES
            {
                dwFlags = Win32.DWM_TNP_RECTDESTINATION | Win32.DWM_TNP_VISIBLE | Win32.DWM_TNP_OPACITY | Win32.DWM_TNP_SOURCECLIENTAREAONLY,
                rcDestination = fitted,
                opacity = 255,
                fVisible = onScreen,
                fSourceClientAreaOnly = true,
            };
            Win32.DwmUpdateThumbnailProperties(thumb, ref props);
        }
    }

    /// <summary>Letterbox <paramref name="dest"/> to the thumbnail source's real aspect ratio
    /// (centered), so windows aren't stretched/squashed to fill the card - same as the OS's own
    /// live previews.</summary>
    private static Win32.RECT FitAspect(IntPtr thumb, Win32.RECT dest)
    {
        if (Win32.DwmQueryThumbnailSourceSize(thumb, out var size) != 0 || size.cx <= 0 || size.cy <= 0)
            return dest;

        double destW = dest.Width, destH = dest.Height;
        double srcAspect = (double)size.cx / size.cy;
        double destAspect = destW / destH;

        double w, h;
        if (srcAspect > destAspect) { w = destW; h = destW / srcAspect; }
        else { h = destH; w = destH * srcAspect; }

        int x = dest.Left + (int)Math.Round((destW - w) / 2);
        int y = dest.Top + (int)Math.Round((destH - h) / 2);
        return new Win32.RECT { Left = x, Top = y, Right = x + (int)Math.Round(w), Bottom = y + (int)Math.Round(h) };
    }

    private void TeardownSlotWindows()
    {
        if (_slotWindows.Count == 0) return;
        // Snapshot and clear first: slot.Win.Close() below could in principle re-enter Hide() ->
        // TeardownSlotWindows() through some other path; clearing before iterating makes that a
        // safe no-op instead of colliding with this enumeration.
        var snapshot = _slotWindows.ToList();
        _slotWindows.Clear();
        _acceptableForeground.Clear();
        _focusPoll.Stop();

        foreach (var slot in snapshot)
        {
            foreach (var card in slot.Cards)
                if (card.Thumb is { } thumb)
                    Win32.DwmUnregisterThumbnail(thumb);
            slot.Win.Close();
        }
    }

    /// <summary>Dismiss a single zone's overlay (picked a candidate, or clicked its background) -
    /// without touching any other still-open slot, so filling one quarter doesn't also close the
    /// Snap Assist offer for the other empty quarters.</summary>
    private void CloseSlot(SlotWindow slot)
    {
        if (!_slotWindows.Remove(slot)) return; // already closed (e.g. a duplicate event)

        foreach (var card in slot.Cards)
            if (card.Thumb is { } thumb)
                Win32.DwmUnregisterThumbnail(thumb);
        _acceptableForeground.Remove(new WindowInteropHelper(slot.Win).Handle);
        slot.Win.Close();

        if (_slotWindows.Count == 0)
        {
            // That was the last one - full teardown (timer, pick callback).
            _timeout.Stop();
            _focusPoll.Stop();
            _onPick = null;
            return;
        }

        // If the slot we just closed was the primary (the one real, activatable window), promote
        // the next remaining one so Esc/keyboard input still has somewhere to land. Dismiss-on-
        // click-away no longer depends on which slot is "active" (see CheckFocusLost), so this is
        // purely a keyboard-focus concern now.
        var newPrimary = _slotWindows[0];
        IntPtr h = new WindowInteropHelper(newPrimary.Win).Handle;
        long ex = Win32.GetWindowLong(h, Win32.GWL_EXSTYLE);
        if ((ex & Win32.WS_EX_NOACTIVATE) != 0)
        {
            ex &= ~Win32.WS_EX_NOACTIVATE;
            Win32.SetWindowLongPtr(h, Win32.GWL_EXSTYLE, new IntPtr(ex));
            Win32.ForceForeground(h);
        }
    }

    public void Hide()
    {
        // Defensive no-op while ShowCore is still creating/showing/activating slot windows.
        if (_settingUp) return;
        _timeout.Stop();
        _focusPoll.Stop();
        _onPick = null;
        TeardownSlotWindows();
    }

    public void Close()
    {
        _timeout.Stop();
        TeardownSlotWindows();
    }
}
