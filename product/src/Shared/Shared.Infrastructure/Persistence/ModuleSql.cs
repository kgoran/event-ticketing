using System.Data.Common;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;

namespace EventTicketing.Shared.Infrastructure.Persistence;

/// <summary>Hand-written SQL on a module context's connection, inside its transaction if one is open.</summary>
/// <remarks>
/// Schema names in the SQL come only from <see cref="ModuleDatabase.Schema"/>, which
/// AddModuleDatabase validates as an identifier; every value is passed as a parameter.
/// </remarks>
internal static class ModuleSql
{
    public static Task<int> ExecuteAsync(ModuleDbContext context, string sql, object[] parameters, CancellationToken cancellationToken) =>
        context.Database.ExecuteSqlRawAsync(sql, parameters, cancellationToken);

    /// <summary>A short error description for the last_error columns.</summary>
    public static string ErrorText(Exception exception)
    {
        var text = $"{exception.GetType().Name}: {exception.Message}";
        return text.Length <= 2000 ? text : text[..2000];
    }

    public static async Task<List<T>> QueryAsync<T>(
        ModuleDbContext context,
        string sql,
        Func<DbDataReader, T> map,
        CancellationToken cancellationToken,
        params (string Name, object Value)[] parameters)
    {
        await context.Database.OpenConnectionAsync(cancellationToken);
        try
        {
            await using var command = context.Database.GetDbConnection().CreateCommand();
            command.CommandText = sql;
            command.Transaction = context.Database.CurrentTransaction?.GetDbTransaction();

            foreach (var (name, value) in parameters)
            {
                var parameter = command.CreateParameter();
                parameter.ParameterName = name;
                parameter.Value = value;
                command.Parameters.Add(parameter);
            }

            var rows = new List<T>();
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
            {
                rows.Add(map(reader));
            }

            return rows;
        }
        finally
        {
            await context.Database.CloseConnectionAsync();
        }
    }
}
