using System.Security.Cryptography;
using System.Text;

namespace Badger.SqlMigrate.Core.Hashing;

/// <summary>
/// SHA-256 hashing over normalized content. Normalization is intentionally minimal (BOM removed,
/// CRLF/CR to LF) so that Windows and macOS/Linux checkouts of the same commit hash identically while
/// any meaningful edit, including whitespace, still changes the hash.
/// </summary>
public static class ContentHasher
{
    private static readonly UTF8Encoding Utf8NoBom = new(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true);

    public static string Normalize(string text)
    {
        if (text.Length > 0 && text[0] == '﻿')
        {
            text = text[1..];
        }

        return text.Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n');
    }

    /// <summary>Reads a script file as UTF-8 (honoring a BOM) and normalizes it.</summary>
    public static string ReadNormalized(string path) => Normalize(File.ReadAllText(path, Encoding.UTF8));

    /// <summary>
    /// Hashes a sequence of named parts with length-prefixed framing so that moving text between
    /// parts (e.g. from deploy to revert) can never produce the same hash.
    /// </summary>
    public static string HashParts(params (string Label, string? Value)[] parts)
    {
        using var sha = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        foreach (var (label, value) in parts)
        {
            Append(sha, label);
            if (value is null)
            {
                sha.AppendData([0]);
            }
            else
            {
                sha.AppendData([1]);
                Append(sha, Normalize(value));
            }
        }

        return Convert.ToHexStringLower(sha.GetHashAndReset());
    }

    public static string Hash(string text) => HashParts(("text", text));

    private static void Append(IncrementalHash sha, string value)
    {
        var bytes = Utf8NoBom.GetBytes(value);
        Span<byte> length = stackalloc byte[8];
        BitConverter.TryWriteBytes(length, (long)bytes.Length);
        if (!BitConverter.IsLittleEndian)
        {
            length.Reverse();
        }

        sha.AppendData(length);
        sha.AppendData(bytes);
    }
}
