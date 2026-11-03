namespace Badger.SqlMigrate.Core.Hashing;

/// <summary>CRC-32 (IEEE 802.3, as java.util.zip.CRC32), used to compare scripts with Flyway's recorded checksums.</summary>
public static class Crc32
{
    private static readonly uint[] Table = Build();

    public static uint Update(uint crc, ReadOnlySpan<byte> bytes)
    {
        crc = ~crc;
        foreach (var b in bytes)
        {
            crc = Table[(crc ^ b) & 0xFF] ^ (crc >> 8);
        }

        return ~crc;
    }

    /// <summary>
    /// Flyway's checksum of a script: CRC-32 over the UTF-8 bytes of each line without its line break
    /// (\n, \r\n or \r), with a byte-order mark removed from the first line, as a signed 32-bit integer.
    /// </summary>
    public static int Flyway(string text)
    {
        if (text.Length > 0 && text[0] == '﻿')
        {
            text = text[1..];
        }

        uint crc = 0;
        var start = 0;
        for (var i = 0; i <= text.Length; i++)
        {
            if (i < text.Length && text[i] != '\n' && text[i] != '\r')
            {
                continue;
            }

            if (i == text.Length && start == text.Length)
            {
                break;
            }

            crc = Update(crc, System.Text.Encoding.UTF8.GetBytes(text[start..i]));
            if (i < text.Length && text[i] == '\r' && i + 1 < text.Length && text[i + 1] == '\n')
            {
                i++;
            }

            start = i + 1;
        }

        return unchecked((int)crc);
    }

    private static uint[] Build()
    {
        var table = new uint[256];
        for (uint n = 0; n < 256; n++)
        {
            var c = n;
            for (var k = 0; k < 8; k++)
            {
                c = (c & 1) != 0 ? 0xEDB88320 ^ (c >> 1) : c >> 1;
            }

            table[n] = c;
        }

        return table;
    }
}
