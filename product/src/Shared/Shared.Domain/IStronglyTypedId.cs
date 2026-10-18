namespace EventTicketing.Shared.Domain;

/// <summary>
/// Marker for strongly typed IDs, so an <c>OrderId</c> can never be passed where an <c>EventId</c>
/// is expected. Each module declares its own, for example:
/// <code>
/// public readonly record struct OrderId(Guid Value) : IStronglyTypedId&lt;Guid&gt;
/// {
///     public static OrderId New() => new(Guid.CreateVersion7());
/// }
/// </code>
/// Version 7 GUIDs sort by creation time, which keeps database indexes compact.
/// </summary>
public interface IStronglyTypedId<out TValue>
{
    TValue Value { get; }
}
