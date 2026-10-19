namespace EventTicketing.FanSite.Seating;

public enum SelectionChange
{
    Added,
    Removed,

    /// <summary>Not added: the fan already picked as many seats as one hold may have.</summary>
    Full,
}

/// <summary>The seats a fan has clicked on the map but not held yet. Nothing is reserved until they hold them.</summary>
public sealed class SeatSelection(int maximum)
{
    private readonly List<Guid> _seats = [];

    /// <summary>The most seats one hold may have (Inventory's MaxSeatsPerHold; PublicApi checks it too).</summary>
    public int Maximum { get; } = maximum;

    public IReadOnlyList<Guid> Seats => _seats;

    public bool Contains(Guid seatId) => _seats.Contains(seatId);

    public SelectionChange Toggle(Guid seatId)
    {
        if (_seats.Remove(seatId))
        {
            return SelectionChange.Removed;
        }

        if (_seats.Count >= Maximum)
        {
            return SelectionChange.Full;
        }

        _seats.Add(seatId);
        return SelectionChange.Added;
    }

    /// <summary>Drops the picked seats someone else has taken meanwhile.</summary>
    /// <returns>How many were dropped.</returns>
    public int KeepOnly(IReadOnlySet<Guid> available) => _seats.RemoveAll(seatId => !available.Contains(seatId));

    public void Clear() => _seats.Clear();
}
