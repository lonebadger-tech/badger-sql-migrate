namespace Badger.Trek.Providers.SqlServer.Tests;

/// <summary>Badger Trek supports SQL Server 2019 and later; 'trek doctor' says so about older servers.</summary>
public sealed class SupportedVersionTests
{
    [Theory]
    [InlineData("SQL Server", "13.0.7037.1", true)]
    [InlineData("SQL Server", "14.0.3456.2", true)]
    [InlineData("SQL Server", "15.0.4385.2", false)]
    [InlineData("SQL Server", "16.0.4135.4", false)]
    [InlineData("SQL Server", "17.0.1000.7", false)]
    [InlineData("Azure SQL Database", "12.0.2000.8", false)]
    [InlineData("Azure SQL Managed Instance", "12.0.2000.8", false)]
    [InlineData("SQL Server", "", false)]
    public void Doctor_flags_sql_server_older_than_2019(string engine, string version, bool flagged) =>
        Assert.Equal(flagged, SqlServerProvider.UnsupportedVersionNote(engine, version).Contains("not supported", StringComparison.Ordinal));
}
