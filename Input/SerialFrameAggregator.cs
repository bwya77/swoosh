namespace Swoosh.Input;

/// <summary>
/// A decoded report payload from a serial/hybrid Precision Touchpad.
/// </summary>
public readonly record struct SerialReport(
    bool HasContactCount,
    uint ContactCount,
    bool Tip,
    int ContactId,
    double Nx,
    double Ny,
    long TimestampMs,
    uint RawX = 0,
    uint RawY = 0);

/// <summary>
/// Aggregates multi-touch contacts for Precision Touchpads using the Serial or Hybrid
/// reporting mode (e.g. ELAN and other PTP vendors where a single LinkCollection reports
/// contacts sequentially, differentiated by Contact ID).
///
/// In Hybrid mode, the first report of a logical frame declares the total number of contacts
/// via Contact Count (&gt; 0). Subsequent reports in that frame have Contact Count = 0.
/// This aggregator collects contacts across multiple HID reports and WM_INPUT payloads,
/// emitting a TouchFrame only once the complete logical frame has been assembled.
/// </summary>
public sealed class SerialFrameAggregator
{
    public sealed class SerialContact
    {
        public int Id { get; init; }
        public double Nx { get; init; }
        public double Ny { get; init; }
        public uint RawX { get; init; }
        public uint RawY { get; init; }
        public bool TipDown { get; init; }
    }

    /// <summary>Timeout in ms to assemble a pending logical frame before discarding partial aggregation.</summary>
    public int PendingTimeoutMs { get; set; } = 100;

    private readonly Dictionary<int, SerialContact> _activeContacts = new();
    private readonly Dictionary<int, SerialContact> _pendingContacts = new();
    private int _expectedCount;
    private bool _isAggregating;
    private long _pendingStartMs;
    private int _lastEmittedCount;

    /// <summary>Number of currently active (touching) contacts.</summary>
    public int ActiveContactCount => _activeContacts.Count;

    /// <summary>Whether a logical frame aggregation is currently in progress.</summary>
    public bool IsAggregating => _isAggregating;

    /// <summary>Expected number of contacts for the frame currently aggregating.</summary>
    public int ExpectedCount => _expectedCount;

    /// <summary>Number of contacts collected so far for the frame currently aggregating.</summary>
    public int PendingCount => _pendingContacts.Count;

    /// <summary>
    /// Process an incoming serial report. Emits a <see cref="TouchFrame"/> when a complete logical
    /// frame is assembled, or when contacts are removed / lift off. Returns null if more reports
    /// are needed to complete the frame.
    /// </summary>
    public TouchFrame? ProcessReport(SerialReport report) =>
        ProcessReport(
            report.HasContactCount,
            report.ContactCount,
            report.Tip,
            report.ContactId,
            report.Nx,
            report.Ny,
            report.TimestampMs,
            report.RawX,
            report.RawY);

    /// <summary>
    /// Process an incoming serial report with individual arguments.
    /// </summary>
    public TouchFrame? ProcessReport(
        bool hasContactCount,
        uint contactCount,
        bool tip,
        int contactId,
        double nx,
        double ny,
        long timestampMs,
        uint rawX = 0,
        uint rawY = 0)
    {
        // 1. Check if the in-progress pending frame timed out
        if (_isAggregating && timestampMs - _pendingStartMs > PendingTimeoutMs)
        {
            _isAggregating = false;
            _pendingContacts.Clear();
            _expectedCount = 0;
        }

        // 2. Start a new logical frame when Contact Count > 0
        int declaredCount = hasContactCount ? (int)contactCount : 1;
        if (declaredCount > 0)
        {
            _isAggregating = true;
            _expectedCount = declaredCount;
            _pendingStartMs = timestampMs;
            _pendingContacts.Clear();
        }

        // 3. If currently aggregating, collect this report's contact
        if (_isAggregating)
        {
            _pendingContacts[contactId] = new SerialContact
            {
                Id = contactId,
                Nx = nx,
                Ny = ny,
                RawX = rawX,
                RawY = rawY,
                TipDown = tip
            };

            // Have we collected the declared number of contacts?
            if (_pendingContacts.Count >= _expectedCount)
            {
                return CompleteLogicalFrame(timestampMs);
            }

            return null;
        }

        // 4. Not currently aggregating (e.g. lift-off reports, tip-up outside frame, or CC=0)
        if (hasContactCount && contactCount == 0)
        {
            if (_activeContacts.Count > 0)
            {
                _activeContacts.Clear();
                return EmitActiveFrame(timestampMs);
            }
        }
        else if (!tip)
        {
            if (_activeContacts.Remove(contactId))
            {
                return EmitActiveFrame(timestampMs);
            }
        }

        return null;
    }

    /// <summary>Resets all internal aggregation and active contact tracking state.</summary>
    public void Reset()
    {
        _activeContacts.Clear();
        _pendingContacts.Clear();
        _isAggregating = false;
        _expectedCount = 0;
        _pendingStartMs = 0;
        _lastEmittedCount = 0;
    }

    private TouchFrame? CompleteLogicalFrame(long timestampMs)
    {
        _isAggregating = false;
        _expectedCount = 0;

        // The assembled frame represents the current contact state.
        // Update active contacts with the newly collected set.
        _activeContacts.Clear();
        foreach (var kvp in _pendingContacts)
        {
            if (kvp.Value.TipDown)
            {
                _activeContacts[kvp.Key] = kvp.Value;
            }
        }
        _pendingContacts.Clear();

        return EmitActiveFrame(timestampMs);
    }

    private TouchFrame? EmitActiveFrame(long timestampMs)
    {
        if (_activeContacts.Count == 0 && _lastEmittedCount == 0)
            return null;

        var frame = new TouchFrame { TimestampMs = timestampMs };
        foreach (var contact in _activeContacts.Values)
        {
            frame.Contacts.Add(new Contact(contact.Id, contact.Nx, contact.Ny, true));
        }

        _lastEmittedCount = _activeContacts.Count;

        if (Swoosh.Log.Verbose)
        {
            string rawDetail = string.Join(" ", _activeContacts.Values.Select(c =>
                $"id{c.Id}@{c.RawX},{c.RawY}({c.Nx:F2},{c.Ny:F2})"));
            Swoosh.Log.Write($"[SerialFrame] n={_activeContacts.Count} [{rawDetail}]");
        }

        return frame;
    }
}
