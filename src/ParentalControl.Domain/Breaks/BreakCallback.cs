using System.Globalization;

namespace ParentalControl.Domain.Breaks;

/// <summary>
/// The callback data of the Telegram button that ends a Break: <c>brk_end:&lt;userId&gt;:&lt;breakStartUnixSeconds&gt;</c>.
/// The start time binds the button to one Break, so that an old button does not end a later Break.
/// </summary>
public static class BreakCallback
{
    public const string Action = "brk_end";

    /// <summary>The limit of Telegram for <c>callback_data</c>, in bytes.</summary>
    public const int MaxBytes = 64;

    public static string Format(int userId, DateTime breakStartUtc) =>
        string.Create(CultureInfo.InvariantCulture, $"{Action}:{userId}:{BreakPlanner.ToUnixSeconds(breakStartUtc)}");

    public static bool TryParse(string? data, out int userId, out long breakStartUnixSeconds)
    {
        userId = 0;
        breakStartUnixSeconds = 0;
        if (string.IsNullOrEmpty(data)) return false;

        var parts = data.Split(':');
        return parts.Length == 3
            && parts[0] == Action
            && int.TryParse(parts[1], NumberStyles.None, CultureInfo.InvariantCulture, out userId)
            && long.TryParse(parts[2], NumberStyles.None, CultureInfo.InvariantCulture, out breakStartUnixSeconds);
    }
}
