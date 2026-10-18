using Npgsql;
using Testcontainers.PostgreSql;

[assembly: AssemblyFixture(typeof(EventTicketing.Shared.IntegrationTests.PostgresFixture))]

namespace EventTicketing.Shared.IntegrationTests;

/// <summary>One PostgreSQL container for the whole test run; every test gets a fresh database in it.</summary>
public sealed class PostgresFixture : IAsyncLifetime
{
    private readonly PostgreSqlContainer _container = new PostgreSqlBuilder("postgres:17-alpine")
        .WithCommand("-c", "max_connections=500")
        .Build();

    public async ValueTask InitializeAsync() => await _container.StartAsync();

    public ValueTask DisposeAsync() => _container.DisposeAsync();

    public async Task<string> CreateDatabaseAsync()
    {
        var name = "test_" + Guid.NewGuid().ToString("N");

        await using (var connection = new NpgsqlConnection(_container.GetConnectionString()))
        {
            await connection.OpenAsync();
            await using var command = new NpgsqlCommand($"CREATE DATABASE {name}", connection);
            await command.ExecuteNonQueryAsync();
        }

        return new NpgsqlConnectionStringBuilder(_container.GetConnectionString()) { Database = name }.ConnectionString;
    }
}
