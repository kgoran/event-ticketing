namespace EventTicketing.Shared.Domain;

/// <summary>The "no value" type, for commands that only change state: <c>Result&lt;Unit&gt;</c>.</summary>
public readonly record struct Unit
{
    public static readonly Unit Value;
}
