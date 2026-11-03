using Microsoft.Extensions.Configuration;
using Npgsql;

namespace CodeForge.Infrastructure.Data;

public static class DatabaseConnection
{
    public static string GetConnectionString(IConfiguration configuration)
    {
        var configuredConnectionString = configuration.GetConnectionString("DefaultConnection");
        if (!string.IsNullOrWhiteSpace(configuredConnectionString))
        {
            return configuredConnectionString;
        }

        var portValue = GetRequiredValue(configuration, "DB_PORT");
        if (!int.TryParse(portValue, out var port) || port is < 1 or > 65535)
        {
            throw new InvalidOperationException("DB_PORT must be an integer between 1 and 65535.");
        }

        return new NpgsqlConnectionStringBuilder
        {
            Host = GetRequiredValue(configuration, "DB_HOST"),
            Port = port,
            Database = GetRequiredValue(configuration, "DB_NAME"),
            Username = GetRequiredValue(configuration, "DB_USER"),
            Password = GetRequiredValue(configuration, "PGPASSWORD"),
            SslMode = SslMode.VerifyFull
        }.ConnectionString;
    }

    private static string GetRequiredValue(IConfiguration configuration, string key)
    {
        var value = configuration[key];
        return !string.IsNullOrWhiteSpace(value)
            ? value
            : throw new InvalidOperationException($"{key} is required when ConnectionStrings:DefaultConnection is not set.");
    }
}
