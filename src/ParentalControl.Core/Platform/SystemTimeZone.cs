using System.Runtime.InteropServices;
using ParentalControl.Domain.Time;

namespace ParentalControl.Core.Platform;

/// <summary>
/// The current system time zone from Windows (control-clock, design D5). <c>TimeZoneInfo.Local</c> is cached for the
/// life of the process, so the service reads the zone with <c>GetDynamicTimeZoneInformation</c>.
/// </summary>
public static class SystemTimeZone
{
    private const uint TimeZoneIdInvalid = 0xFFFFFFFF;

    /// <summary>The key name of the zone and the daylight saving flag, or null if Windows gives no key name.</summary>
    public static SystemZoneSetting? Read()
    {
        try
        {
            var info = new DynamicTimeZoneInformation();
            if (GetDynamicTimeZoneInformation(ref info) == TimeZoneIdInvalid || string.IsNullOrWhiteSpace(info.TimeZoneKeyName))
                return null;
            return new SystemZoneSetting(info.TimeZoneKeyName.Trim(), info.DynamicDaylightTimeDisabled != 0);
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException)
        {
            return null;
        }
    }

    /// <summary>The current system time zone. Without a key name: <c>TimeZoneInfo.Local</c>.</summary>
    public static TimeZoneInfo CurrentZone() =>
        Read() is { } setting && Find(setting.KeyName) is { } zone ? zone : TimeZoneInfo.Local;

    /// <summary>The Windows time zone with this ID, or null if Windows does not know it.</summary>
    public static TimeZoneInfo? Find(string id)
    {
        if (string.IsNullOrWhiteSpace(id)) return null;
        try
        {
            return TimeZoneInfo.FindSystemTimeZoneById(id.Trim());
        }
        catch (Exception ex) when (ex is TimeZoneNotFoundException or InvalidTimeZoneException or System.Security.SecurityException)
        {
            return null;
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct SystemTime
    {
        public ushort Year, Month, DayOfWeek, Day, Hour, Minute, Second, Milliseconds;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct DynamicTimeZoneInformation
    {
        public int Bias;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string StandardName;
        public SystemTime StandardDate;
        public int StandardBias;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string DaylightName;
        public SystemTime DaylightDate;
        public int DaylightBias;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string TimeZoneKeyName;
        public byte DynamicDaylightTimeDisabled; // BOOLEAN
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern uint GetDynamicTimeZoneInformation(ref DynamicTimeZoneInformation info);
}

/// <summary>The free space of a volume (storage-failure, design D8).</summary>
public static class DiskSpace
{
    /// <summary>The bytes that the caller can use on the volume of <paramref name="folder"/>, or null on an error.</summary>
    public static long? FreeBytes(string folder)
    {
        try
        {
            if (!GetDiskFreeSpaceExW(folder, out var available, out _, out _))
                return null;
            return available > long.MaxValue ? long.MaxValue : (long)available;
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException)
        {
            return null;
        }
    }

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetDiskFreeSpaceExW(string directoryName, out ulong freeBytesAvailable, out ulong totalBytes, out ulong totalFreeBytes);
}
