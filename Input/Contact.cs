namespace Swoosh.Input;

/// <summary>A single finger contact on the touchpad, normalized to 0..1.</summary>
public readonly struct Contact
{
    public readonly int Id;
    public readonly double X; // 0..1 across pad width
    public readonly double Y; // 0..1 across pad height (0 = top)
    public readonly bool TipDown;

    public Contact(int id, double x, double y, bool tipDown)
    {
        Id = id; X = x; Y = y; TipDown = tipDown;
    }
}

/// <summary>One decoded frame from the touchpad: the set of active contacts.</summary>
public sealed class TouchFrame
{
    public readonly List<Contact> Contacts = new();
    public long TimestampMs;
    public int DownCount => Contacts.Count(c => c.TipDown);
}
