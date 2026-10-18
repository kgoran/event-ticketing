namespace EventTicketing.Shared.Contracts;

/// <summary>
/// Something that happened in one module that other modules may react to. Integration events are
/// public, versioned contracts (ADR-0002): add fields, never change or remove them; a breaking
/// change becomes a new version of the event.
/// </summary>
/// <remarks>
/// <see cref="MessageId"/> identifies the message itself; consumers use it to drop duplicates. It is
/// deliberately not called EventId: in this codebase "event" means a ticketed event.
/// </remarks>
public interface IIntegrationEvent
{
    Guid MessageId { get; }

    DateTimeOffset OccurredAt { get; }
}

/// <summary>The stable name and version of an integration event, independent of its .NET type name.</summary>
[AttributeUsage(AttributeTargets.Class, Inherited = false)]
public sealed class IntegrationEventAttribute(string name, int version) : Attribute
{
    public string Name { get; } = name;

    public int Version { get; } = version;
}
