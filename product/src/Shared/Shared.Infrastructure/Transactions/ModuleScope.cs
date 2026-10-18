using EventTicketing.Shared.Infrastructure.Persistence;

namespace EventTicketing.Shared.Infrastructure.Transactions;

/// <summary>
/// The module transactions open in the current DI scope, innermost last. Publishing an event,
/// scheduling a message and writing an audit entry all go into the innermost one.
/// </summary>
internal sealed class ModuleScope
{
    private readonly Stack<(ModuleDatabase Module, ModuleDbContext Context)> _open = new();

    public ModuleDatabase? CurrentModule => _open.Count > 0 ? _open.Peek().Module : null;

    public bool IsOpen(ModuleDatabase module) => _open.Any(entry => entry.Module == module);

    public ModuleDbContext Require(string operation) =>
        _open.Count > 0
            ? _open.Peek().Context
            : throw new InvalidOperationException($"{operation} is only possible inside a command's or consumer's transaction.");

    public IDisposable Enter(ModuleDatabase module, ModuleDbContext context)
    {
        _open.Push((module, context));
        return new Exit(this);
    }

    private sealed class Exit(ModuleScope scope) : IDisposable
    {
        public void Dispose() => scope._open.Pop();
    }
}
