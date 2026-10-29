using System.Data.Common;
using Microsoft.Data.SqlClient;
using MySqlConnector;
using Npgsql;
using Oracle.ManagedDataAccess.Client;

namespace Bold.UpgradeCenter.Services;

public interface IStandaloneRuntimeConfigurationReader
{
    Task<string?> ReadAsync(UpgradeCenterRuntimeConfiguration bootstrap, string connectionString, CancellationToken cancellationToken);
}

public class StandaloneRuntimeConfigurationReader : IStandaloneRuntimeConfigurationReader
{
    public async Task<string?> ReadAsync(UpgradeCenterRuntimeConfiguration bootstrap, string connectionString, CancellationToken cancellationToken)
    {
        try
        {
            var type = bootstrap.MasterDatabaseServerType!.Value;
            await using var connection = CreateConnection(type, connectionString);
            await connection.OpenAsync(cancellationToken);
            var schema = bootstrap.MasterDatabaseSchemaName;
            if (string.IsNullOrWhiteSpace(schema))
            {
                schema = type switch
                {
                    0 => "dbo",
                    1 => connection.Database,
                    3 => new OracleConnectionStringBuilder(connectionString).UserID.ToUpperInvariant(),
                    _ => "public"
                };
            }

            var table = (bootstrap.MasterDatabaseTablePrefix ?? "BOLDTC_") + "SystemSettings";
            await using var metadata = connection.CreateCommand();
            metadata.CommandTimeout = 15;
            metadata.CommandText = type == 3
                ? "SELECT owner, table_name, column_name FROM all_tab_columns WHERE upper(owner) = upper(:schema) AND lower(table_name) = lower(:tableName)"
                : "SELECT table_schema, table_name, column_name FROM information_schema.columns WHERE table_schema = @schema AND lower(table_name) = lower(@tableName)";
            AddParameter(metadata, "schema", schema);
            AddParameter(metadata, "tableName", table);
            string? actualSchema = null;
            string? actualTable = null;
            string? keyColumn = null;
            string? valueColumn = null;
            await using (var columns = await metadata.ExecuteReaderAsync(cancellationToken))
            {
                while (await columns.ReadAsync(cancellationToken))
                {
                    var foundSchema = columns.GetString(0);
                    var foundTable = columns.GetString(1);
                    if (actualTable is not null && (actualTable != foundTable || actualSchema != foundSchema))
                    {
                        throw new InvalidOperationException("Standalone runtime configuration table is ambiguous. Verify the configured master schema and prefix.");
                    }

                    actualSchema = foundSchema;
                    actualTable = foundTable;
                    var column = columns.GetString(2);
                    if (column.Equals("SystemKey", StringComparison.OrdinalIgnoreCase)) keyColumn = column;
                    if (column.Equals("SystemValue", StringComparison.OrdinalIgnoreCase)) valueColumn = column;
                }
            }

            if (actualTable is null || keyColumn is null || valueColumn is null)
            {
                throw new InvalidOperationException("Standalone could not verify the master system-settings table. Check database read permissions, schema, and table prefix.");
            }

            await using var command = connection.CreateCommand();
            command.CommandTimeout = 15;
            var parameter = type == 3 ? ":key" : "@key";
            command.CommandText = $"SELECT {Quote(type, valueColumn)} FROM {Quote(type, actualSchema!)}.{Quote(type, actualTable)} WHERE {Quote(type, keyColumn)} = {parameter}";
            AddParameter(command, "key", "RuntimeConfigurationJson");
            await using var rows = await command.ExecuteReaderAsync(cancellationToken);
            if (!await rows.ReadAsync(cancellationToken))
            {
                // Older products have the settings table but no DB-backed runtime configuration.
                return null;
            }

            var json = rows.IsDBNull(0) ? null : rows.GetString(0);
            if (string.IsNullOrWhiteSpace(json) || json.Length > 4 * 1024 * 1024 || await rows.ReadAsync(cancellationToken))
            {
                throw new InvalidOperationException("Standalone database runtime configuration is empty, oversized, or ambiguous.");
            }

            return json;
        }
        catch (DbException)
        {
            throw new InvalidOperationException("Standalone could not read master database runtime configuration. Check database connectivity and read permissions; stored configuration will not be used as a fallback for this error.");
        }
    }

    protected virtual DbConnection CreateConnection(int type, string connectionString) => type switch
    {
        0 => new SqlConnection(connectionString),
        1 => new MySqlConnection(connectionString),
        3 => new OracleConnection(connectionString),
        4 => new NpgsqlConnection(connectionString),
        _ => throw new InvalidOperationException("Unsupported standalone master database type.")
    };

    private static void AddParameter(DbCommand command, string name, string value)
    {
        var parameter = command.CreateParameter();
        parameter.ParameterName = name;
        parameter.Value = value;
        command.Parameters.Add(parameter);
    }

    private static string Quote(int type, string name) => type switch
    {
        0 => "[" + name.Replace("]", "]]", StringComparison.Ordinal) + "]",
        1 => "`" + name.Replace("`", "``", StringComparison.Ordinal) + "`",
        _ => "\"" + name.Replace("\"", "\"\"", StringComparison.Ordinal) + "\""
    };
}
