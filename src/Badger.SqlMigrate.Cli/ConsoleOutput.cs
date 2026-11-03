using System.Text.Json;
using Badger.SqlMigrate.Core.Engine;

namespace Badger.SqlMigrate.Cli;

/// <summary>Console rendering. Colors honor NO_COLOR and are off when output is redirected; --json prints machine output only.</summary>
public sealed class ConsoleOutput(bool json, bool verbose, bool noColor) : IMigrationLog
{
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true, PropertyNamingPolicy = JsonNamingPolicy.CamelCase };

    private readonly bool color = !noColor && Environment.GetEnvironmentVariable("NO_COLOR") is null && !Console.IsOutputRedirected;

    public bool Json => json;

    public void Info(string message)
    {
        if (!json)
        {
            Console.WriteLine(message);
        }
    }

    public void Success(string message) => Write(message, "\u001b[32m");

    public void Warn(string message)
    {
        // Warnings go to stderr so --json stdout stays parseable.
        Console.Error.WriteLine(color ? $"\u001b[33mwarning:\u001b[0m {message}" : $"warning: {message}");
    }

    public void Error(string message) =>
        Console.Error.WriteLine(color ? $"\u001b[31merror:\u001b[0m {message}" : $"error: {message}");

    public void Detail(string message)
    {
        if (verbose && !json)
        {
            Write(message, "\u001b[90m");
        }
    }

    public void Highlight(string message) => Write(message, "\u001b[1m");

    public void Muted(string message) => Write(message, "\u001b[90m");

    public void Line(string label, string value, string? ansi = null)
    {
        if (json)
        {
            return;
        }

        var text = $"{label,-14} {value}";
        Console.WriteLine(color && ansi is not null ? $"{ansi}{text}\u001b[0m" : text);
    }

    public string Paint(string text, string ansi) => color ? $"{ansi}{text}\u001b[0m" : text;

    public void WriteJson(object value) => Console.WriteLine(JsonSerializer.Serialize(value, JsonOptions));

    private void Write(string message, string ansi)
    {
        if (!json)
        {
            Console.WriteLine(color ? $"{ansi}{message}\u001b[0m" : message);
        }
    }
}
