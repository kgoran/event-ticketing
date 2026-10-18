namespace EventTicketing.Shared.Infrastructure.Persistence;

/// <summary>A module's database: its DbContext type, schema, and the namespace its code lives in.</summary>
public sealed record ModuleDatabase(string Schema, string Namespace, Type ContextType);

/// <summary>
/// The module databases registered in this host. Commands, consumers and scheduled-message
/// handlers find their module's database through the namespace they are declared in.
/// </summary>
public sealed class ModuleRegistry
{
    private readonly List<ModuleDatabase> _modules = [];

    public IReadOnlyList<ModuleDatabase> Modules => _modules;

    public ModuleDatabase Resolve(Type type)
    {
        ArgumentNullException.ThrowIfNull(type);

        var typeNamespace = type.Namespace ?? string.Empty;
        return _modules
            .Where(module => typeNamespace == module.Namespace
                || typeNamespace.StartsWith(module.Namespace + ".", StringComparison.Ordinal))
            .MaxBy(module => module.Namespace.Length)
            ?? throw new InvalidOperationException(
                $"{type.FullName} belongs to no registered module database. Register the module's database with AddModuleDatabase.");
    }

    public ModuleDatabase BySchema(string schema) =>
        _modules.SingleOrDefault(module => module.Schema == schema)
        ?? throw new InvalidOperationException($"No module database uses schema '{schema}'.");

    internal void Add(ModuleDatabase module)
    {
        if (_modules.Any(existing => existing.Schema == module.Schema || existing.ContextType == module.ContextType))
        {
            throw new InvalidOperationException($"A module database for schema '{module.Schema}' or context {module.ContextType.Name} is already registered.");
        }

        _modules.Add(module);
    }
}
