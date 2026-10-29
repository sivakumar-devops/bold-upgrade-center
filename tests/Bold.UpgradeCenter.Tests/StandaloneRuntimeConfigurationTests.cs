using System.Data;
using System.Data.Common;
using System.Diagnostics.CodeAnalysis;
using Bold.UpgradeCenter.Services;
using Microsoft.Data.SqlClient;
using Xunit;

namespace Bold.UpgradeCenter.Tests;

public sealed class StandaloneRuntimeConfigurationTests
{
    [Theory]
    [InlineData(0, "[custom_schema].[CUSTOMTC_SystemSettings]", "@key")]
    [InlineData(1, "`custom_schema`.`CUSTOMTC_SystemSettings`", "@key")]
    [InlineData(3, "\"custom_schema\".\"CUSTOMTC_SystemSettings\"", ":key")]
    [InlineData(4, "\"custom_schema\".\"CUSTOMTC_SystemSettings\"", "@key")]
    public async Task RuntimeLookupUsesReadOnlyParameterizedQueriesAndConfiguredSchema(int type, string table, string parameter)
    {
        var connection = new Connection();
        var reader = new RuntimeReader(connection);
        Assert.Equal("{\"current\":true}", await reader.ReadAsync(Bootstrap(type), "test-only", default));
        Assert.Equal(2, connection.Commands.Count);
        Assert.All(connection.Commands, command => Assert.StartsWith("SELECT ", command.CommandText));
        Assert.Contains(table, connection.Commands[1].CommandText);
        Assert.Contains(parameter, connection.Commands[1].CommandText);
        Assert.Equal("RuntimeConfigurationJson", connection.Commands[1].Parameters[0].Value);
        Assert.Equal("custom_schema", connection.Commands[0].Parameters[0].Value);
        Assert.Equal("CUSTOMTC_SystemSettings", connection.Commands[0].Parameters[1].Value);
    }

    [Fact]
    public async Task LegacyDatabaseWithoutRuntimeRowUsesObjectConfiguration()
    {
        var connection = new Connection { RuntimeJson = null };
        Assert.Null(await new RuntimeReader(connection).ReadAsync(Bootstrap(4), "test-only", default));
    }

    [Theory]
    [InlineData("")]
    [InlineData(" ")]
    [InlineData("duplicate")]
    public async Task ExistingInvalidRuntimeRowDoesNotFallBack(string json)
    {
        var connection = new Connection { RuntimeJson = json, DuplicateRuntime = json == "duplicate" };
        await Assert.ThrowsAsync<InvalidOperationException>(() => new RuntimeReader(connection).ReadAsync(Bootstrap(4), "test-only", default));
    }

    [Fact]
    public async Task MissingOrAmbiguousSettingsTableIsRejected()
    {
        var connection = new Connection { MissingMetadata = true };
        await Assert.ThrowsAsync<InvalidOperationException>(() => new RuntimeReader(connection).ReadAsync(Bootstrap(4), "test-only", default));
        connection.MissingMetadata = false;
        connection.AmbiguousMetadata = true;
        await Assert.ThrowsAsync<InvalidOperationException>(() => new RuntimeReader(connection).ReadAsync(Bootstrap(4), "test-only", default));
    }

    [Fact]
    public async Task CatalogNamesAreQuotedInsteadOfExecutedAsSql()
    {
        var connection = new Connection { TableName = "CUSTOMTC_SystemSettings\";DROP TABLE users;--" };
        await new RuntimeReader(connection).ReadAsync(Bootstrap(4), "test-only", default);
        Assert.Contains("\"CUSTOMTC_SystemSettings\"\";DROP TABLE users;--\"", connection.Commands[1].CommandText);
        Assert.StartsWith("SELECT ", connection.Commands[1].CommandText);
    }

    [Fact]
    public async Task DatabaseErrorsAreSanitizedAndCancellationIsPreserved()
    {
        var connection = new Connection { Failure = new TestDbException("password=do-not-log") };
        var reader = new RuntimeReader(connection);
        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => reader.ReadAsync(Bootstrap(4), "test-only", default));
        Assert.DoesNotContain("do-not-log", error.ToString());
        Assert.Null(error.InnerException);
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => reader.ReadAsync(Bootstrap(4), "test-only", cancellation.Token));
    }

    private static UpgradeCenterRuntimeConfiguration Bootstrap(int type) => new(null, null, null,
        MasterDatabaseServerType: type, MasterDatabaseSchemaName: "custom_schema", MasterDatabaseTablePrefix: "CUSTOMTC_");

    private sealed class RuntimeReader(Connection connection) : StandaloneRuntimeConfigurationReader
    {
        protected override DbConnection CreateConnection(int type, string connectionString) => connection;
    }

    private sealed class Connection : DbConnection
    {
        public List<Command> Commands { get; } = [];
        public string? RuntimeJson { get; set; } = "{\"current\":true}";
        public string TableName { get; set; } = "CUSTOMTC_SystemSettings";
        public bool DuplicateRuntime { get; set; }
        public bool MissingMetadata { get; set; }
        public bool AmbiguousMetadata { get; set; }
        public Exception? Failure { get; set; }
        [AllowNull] public override string ConnectionString { get; set; } = "";
        public override string Database => "master-test";
        public override string DataSource => "test";
        public override string ServerVersion => "test";
        public override ConnectionState State => ConnectionState.Open;
        public override void Open() { }
        public override void Close() { }
        public override void ChangeDatabase(string databaseName) => throw new NotSupportedException();
        protected override DbTransaction BeginDbTransaction(IsolationLevel isolationLevel) => throw new NotSupportedException();
        public override Task OpenAsync(CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (Failure is not null) throw Failure;
            return Task.CompletedTask;
        }
        protected override DbCommand CreateDbCommand()
        {
            var command = new Command(this);
            Commands.Add(command);
            return command;
        }
    }

    private sealed class Command(Connection connection) : DbCommand
    {
        private readonly SqlCommand parameters = new();
        [AllowNull] public override string CommandText { get; set; } = "";
        public override int CommandTimeout { get; set; }
        public override CommandType CommandType { get; set; }
        public override bool DesignTimeVisible { get; set; }
        public override UpdateRowSource UpdatedRowSource { get; set; }
        protected override DbConnection? DbConnection { get; set; } = connection;
        protected override DbTransaction? DbTransaction { get; set; }
        protected override DbParameterCollection DbParameterCollection => parameters.Parameters;
        protected override DbParameter CreateDbParameter() => new SqlParameter();
        public override void Cancel() { }
        public override void Prepare() { }
        public override int ExecuteNonQuery() => throw new NotSupportedException("Writes are forbidden.");
        public override object ExecuteScalar() => throw new NotSupportedException();
        protected override DbDataReader ExecuteDbDataReader(CommandBehavior behavior)
        {
            var data = new DataTable();
            if (CommandText.Contains("information_schema.columns") || CommandText.Contains("all_tab_columns"))
            {
                data.Columns.Add("schema", typeof(string));
                data.Columns.Add("table", typeof(string));
                data.Columns.Add("column", typeof(string));
                if (!connection.MissingMetadata)
                {
                    data.Rows.Add("custom_schema", connection.TableName, "SystemKey");
                    data.Rows.Add("custom_schema", connection.TableName, "SystemValue");
                    if (connection.AmbiguousMetadata) data.Rows.Add("custom_schema", "OtherTable", "SystemKey");
                }
            }
            else
            {
                data.Columns.Add("value", typeof(string));
                if (connection.RuntimeJson is not null)
                {
                    data.Rows.Add(connection.RuntimeJson);
                    if (connection.DuplicateRuntime) data.Rows.Add(connection.RuntimeJson);
                }
            }
            return data.CreateDataReader();
        }
    }

    private sealed class TestDbException(string message) : DbException(message);
}
