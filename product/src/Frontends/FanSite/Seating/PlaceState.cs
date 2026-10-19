namespace EventTicketing.FanSite.Seating;

/// <summary>How a seat looks to the fan right now.</summary>
public enum PlaceState
{
    Free,
    Taken,

    /// <summary>Clicked by this fan, not held yet.</summary>
    Selected,

    /// <summary>In this fan's hold.</summary>
    Mine,
}
