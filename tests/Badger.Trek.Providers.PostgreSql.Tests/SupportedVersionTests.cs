namespace Badger.Trek.Providers.PostgreSql.Tests;

/// <summary>Badger Trek supports the PostgreSQL versions the PostgreSQL project still supports; 'trek doctor' says so about older ones.</summary>
public sealed class SupportedVersionTests
{
    [Theory]
    [InlineData(120022, true)]
    [InlineData(130021, true)]
    [InlineData(140025, true)]
    [InlineData(150020, false)]
    [InlineData(180007, false)]
    [InlineData(190000, false)]
    public void Doctor_flags_postgresql_past_its_end_of_life(int serverVersionNum, bool flagged) =>
        Assert.Equal(flagged, PostgreSqlProvider.UnsupportedVersionNote(serverVersionNum).Contains("not supported", StringComparison.Ordinal));
}
