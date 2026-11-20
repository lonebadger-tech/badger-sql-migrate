namespace Badger.Trek.Conversion.IntegrationTests;

internal static class TestFiles
{
    /// <summary>
    /// A YAML single-quoted scalar: no escape sequences, so Windows paths (C:\Users\...) and connection strings with
    /// backslashes ((localdb)\trek2016) stay as they are. Double quotes would read \U or \t as escapes.
    /// </summary>
    public static string YamlQuote(string value) => "'" + value.Replace("'", "''", StringComparison.Ordinal) + "'";

    /// <summary>
    /// A sample script with LF line endings, however git checked it out: a line break inside a string literal is
    /// part of the seeded data, which the tests compare with "\n".
    /// </summary>
    public static string Sample(string name) =>
        File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Samples", name)).Replace("\r\n", "\n", StringComparison.Ordinal);
}
