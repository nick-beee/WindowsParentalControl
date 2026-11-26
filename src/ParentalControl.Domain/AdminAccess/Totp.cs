using System.Security.Cryptography;
using System.Text;

namespace ParentalControl.Domain.AdminAccess;

/// <summary>
/// RFC 6238 TOTP with HMAC-SHA1, 6 digits and a time step of 30 seconds.
/// The check accepts the time steps -1, 0 and +1, but only a step above the last used step,
/// so each code works for one login only.
/// </summary>
public static class Totp
{
    public const int StepSeconds = 30;
    public const int Digits = 6;
    public const int AllowedDrift = 1;

    /// <summary>The value for "no code used yet".</summary>
    public const long NoStepUsed = long.MinValue;

    public static long StepAt(DateTimeOffset time) => time.ToUnixTimeSeconds() / StepSeconds;

    public static string ComputeCode(ReadOnlySpan<byte> key, long step)
    {
        Span<byte> counter = stackalloc byte[8];
        for (var i = 7; i >= 0; i--)
        {
            counter[i] = (byte)(step & 0xFF);
            step >>= 8;
        }

        Span<byte> hash = stackalloc byte[20];
        HMACSHA1.HashData(key, counter, hash);

        var offset = hash[^1] & 0x0F;
        var binary = ((hash[offset] & 0x7F) << 24)
                   | (hash[offset + 1] << 16)
                   | (hash[offset + 2] << 8)
                   | hash[offset + 3];
        return (binary % 1_000_000).ToString("D6");
    }

    /// <summary>
    /// Checks a code against a Base32 secret. A secret that is not valid (see <see cref="TotpSecret"/>) refuses each code.
    /// </summary>
    public static bool TryVerify(string? secret, string? code, DateTimeOffset nowUtc, long lastUsedStep, out long matchedStep)
    {
        matchedStep = NoStepUsed;
        return TotpSecret.TryDecode(secret, out var key)
               && TryVerify(key, code, nowUtc, lastUsedStep, out matchedStep);
    }

    /// <summary>Checks a code against a raw key. An empty key refuses each code.</summary>
    public static bool TryVerify(byte[] key, string? code, DateTimeOffset nowUtc, long lastUsedStep, out long matchedStep)
    {
        matchedStep = NoStepUsed;
        if (key.Length == 0 || !IsWellFormed(code, out var digits))
            return false;

        var given = Encoding.ASCII.GetBytes(digits);
        var current = StepAt(nowUtc);
        for (var drift = -AllowedDrift; drift <= AllowedDrift; drift++)
        {
            var step = current + drift;
            if (step <= lastUsedStep)
                continue;
            var expected = Encoding.ASCII.GetBytes(ComputeCode(key, step));
            if (CryptographicOperations.FixedTimeEquals(expected, given))
            {
                matchedStep = step;
                return true;
            }
        }
        return false;
    }

    private static bool IsWellFormed(string? code, out string digits)
    {
        digits = code?.Trim() ?? string.Empty;
        if (digits.Length != Digits)
            return false;
        foreach (var c in digits)
        {
            if (c < '0' || c > '9')
                return false;
        }
        return true;
    }
}
