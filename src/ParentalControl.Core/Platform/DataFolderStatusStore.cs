using System.Globalization;
using Microsoft.Win32;
using ParentalControl.Domain.DataFolder;

namespace ParentalControl.Core.Platform;

/// <summary>
/// Keeps the result of the data folder step of the service in <c>HKLM\SOFTWARE\ParentalControl</c>
/// (require-admin-sign-in, finding S3). The registry, not the database, because the database is not usable when the
/// step fails. Only an administrator or SYSTEM can write below <c>HKLM\SOFTWARE</c>; a standard user can only read it.
/// The values contain no secret.
/// </summary>
public static class DataFolderStatusStore
{
    private const string KeyPath = @"SOFTWARE\ParentalControl";
    private const string StateValue = "DataFolderState";
    private const string DetailValue = "DataFolderDetail";
    private const string CheckedAtValue = "DataFolderCheckedAtUtc";
    private const string QuarantinedToValue = "DataFolderQuarantinedTo";
    private const string QuarantinedAtValue = "DataFolderQuarantinedAtUtc";

    /// <summary>
    /// Writes the result of one start. A new move of an untrusted folder replaces the old one; a start without a move
    /// keeps the old one, so the Admin app still shows it. Returns false if the write failed.
    /// </summary>
    public static bool TryWrite(DataFolderState state, string? detail, DateTimeOffset checkedAtUtc, string? newQuarantine)
    {
        try
        {
            using var hklm = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64);
            using var key = hklm.CreateSubKey(KeyPath, writable: true);
            key.SetValue(StateValue, state.ToString(), RegistryValueKind.String);
            key.SetValue(DetailValue, detail ?? string.Empty, RegistryValueKind.String);
            key.SetValue(CheckedAtValue, checkedAtUtc.ToString("O", CultureInfo.InvariantCulture), RegistryValueKind.String);
            if (!string.IsNullOrEmpty(newQuarantine))
            {
                key.SetValue(QuarantinedToValue, newQuarantine, RegistryValueKind.String);
                key.SetValue(QuarantinedAtValue, checkedAtUtc.ToString("O", CultureInfo.InvariantCulture), RegistryValueKind.String);
            }
            return true;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>Reads the last result. Without a key or with an unknown value, the state is <see cref="DataFolderState.Unknown"/>.</summary>
    public static DataFolderReport Read()
    {
        try
        {
            using var hklm = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64);
            using var key = hklm.OpenSubKey(KeyPath, writable: false);
            if (key is null)
                return DataFolderReport.None;

            var state = Enum.TryParse<DataFolderState>(key.GetValue(StateValue) as string, out var parsed) ? parsed : DataFolderState.Unknown;
            var detail = key.GetValue(DetailValue) as string;
            var quarantinedTo = key.GetValue(QuarantinedToValue) as string;
            return new DataFolderReport(
                state,
                string.IsNullOrEmpty(detail) ? null : detail,
                ParseTime(key.GetValue(CheckedAtValue) as string),
                string.IsNullOrEmpty(quarantinedTo) ? null : quarantinedTo,
                ParseTime(key.GetValue(QuarantinedAtValue) as string));
        }
        catch
        {
            return DataFolderReport.None;
        }
    }

    private static DateTimeOffset? ParseTime(string? text) =>
        DateTimeOffset.TryParse(text, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var value) ? value : null;
}
