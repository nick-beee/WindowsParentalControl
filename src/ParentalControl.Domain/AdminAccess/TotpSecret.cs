using System.Security.Cryptography;
using System.Text;

namespace ParentalControl.Domain.AdminAccess;

/// <summary>
/// The Base32 TOTP secret of the Remote Admin Interface (RFC 4648 alphabet, no padding).
/// A valid secret has only Base32 characters after the removal of white space and <c>=</c>,
/// in any letter case, and decodes to at least <see cref="MinBytes"/> bytes.
/// The decoder is strict: one character outside the alphabet makes the secret not valid.
/// </summary>
public static class TotpSecret
{
    /// <summary>The smallest secret that is valid (80 bits). A new setup always makes <see cref="NewSecretBytes"/> bytes.</summary>
    public const int MinBytes = 10;
    public const int NewSecretBytes = 20;

    private const string Alphabet = "ABCDEFGHIJKLMNOPQRSTUVWXYZ234567";

    /// <summary>Makes a new random secret of <see cref="NewSecretBytes"/> bytes as Base32 text.</summary>
    public static string Generate() => Encode(RandomNumberGenerator.GetBytes(NewSecretBytes));

    /// <summary>Removes white space and <c>=</c>, and changes the letters to upper case.</summary>
    public static string Normalize(string? secret)
    {
        if (string.IsNullOrEmpty(secret))
            return string.Empty;
        var sb = new StringBuilder(secret.Length);
        foreach (var c in secret)
        {
            if (char.IsWhiteSpace(c) || c == '=')
                continue;
            sb.Append(char.ToUpperInvariant(c));
        }
        return sb.ToString();
    }

    public static bool IsValid(string? secret) => TryDecode(secret, out _);

    /// <summary>Decodes a valid secret. Returns false for a secret that is not valid.</summary>
    public static bool TryDecode(string? secret, out byte[] key)
    {
        key = Array.Empty<byte>();
        var text = Normalize(secret);
        if (text.Length == 0)
            return false;

        var output = new List<byte>(text.Length * 5 / 8);
        int buffer = 0, bits = 0;
        foreach (var c in text)
        {
            var value = Alphabet.IndexOf(c);
            if (value < 0)
                return false;
            buffer = (buffer << 5) | value;
            bits += 5;
            if (bits >= 8)
            {
                bits -= 8;
                output.Add((byte)((buffer >> bits) & 0xFF));
            }
        }

        if (output.Count < MinBytes)
            return false;
        key = output.ToArray();
        return true;
    }

    public static string Encode(ReadOnlySpan<byte> data)
    {
        var sb = new StringBuilder((data.Length * 8 + 4) / 5);
        int buffer = 0, bits = 0;
        foreach (var b in data)
        {
            buffer = (buffer << 8) | b;
            bits += 8;
            while (bits >= 5)
            {
                bits -= 5;
                sb.Append(Alphabet[(buffer >> bits) & 31]);
            }
        }
        if (bits > 0)
            sb.Append(Alphabet[(buffer << (5 - bits)) & 31]);
        return sb.ToString();
    }
}
