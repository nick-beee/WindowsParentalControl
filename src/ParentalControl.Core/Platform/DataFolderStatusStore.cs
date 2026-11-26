using System.Globalization;
using Microsoft.Win32;
using ParentalControl.Domain.DataFolder;

namespace ParentalControl.Core.Platform;

/// <summary>
/// Keeps the result of the data folder step of the service in <c>HKLM\SOFTWARE\ParentalControl</c>
/// (require-admin-sign-in, finding S3). The registry, not the database, because the database is not usable when the
/// step fails. The key has the rules of <see cref="DataFolderRegistry.KeySddl"/>: only an administrator or SYSTEM can
/// write it, a standard user can only read it. The values contain no secret.
/// </summary>
public static class DataFolderStatusStore
{
    private static string KeyPath => DataFolderRegistry.Default.KeyPath;
    private const string StateValue = "DataFolderState";
    private const string DetailValue = "DataFolderDetail";
    private const string CheckedAtValue = "DataFolderCheckedAtUtc";
    private const string QuarantinedToValue = "DataFolderQuarantinedTo";
    private const string QuarantinedAtValue = "DataFolderQuarantinedAtUtc";
    private const string RelocatedFromValue = "DataFolderRelocatedFrom";
    private const string RelocatedToValue = "DataFolderRelocatedTo";
    private const string RelocatedAtValue = "DataFolderRelocatedAtUtc";
    private const string RelocationReasonValue = "DataFolderRelocationReason";
    private const string FolderValue = "DataFolderInUse";

    /// <param name="From">The folder that the service could not use and left alone.</param>
    /// <param name="To">The new data folder with a random name.</param>
    public sealed record Relocation(string From, string To, string Reason);

    /// <summary>
    /// Writes the result of one start. A new move of an untrusted folder or a new data folder replaces the old record; a
    /// start without one keeps the old record, so the Admin app still shows it. Returns false if the write failed.
    /// </summary>
    /// <param name="folder">The data folder that the service uses at this start, or null for a lockdown.</param>
    public static bool TryWrite(DataFolderState state, string? detail, DateTimeOffset checkedAtUtc, string? newQuarantine,
        Relocation? newRelocation = null, string? folder = null)
    {
        try
        {
            using var key = DataFolderRegistry.Default.OpenForWrite();
            key.SetValue(StateValue, state.ToString(), RegistryValueKind.String);
            key.SetValue(FolderValue, folder ?? string.Empty, RegistryValueKind.String);
            key.SetValue(DetailValue, detail ?? string.Empty, RegistryValueKind.String);
            key.SetValue(CheckedAtValue, checkedAtUtc.ToString("O", CultureInfo.InvariantCulture), RegistryValueKind.String);
            if (!string.IsNullOrEmpty(newQuarantine))
            {
                key.SetValue(QuarantinedToValue, newQuarantine, RegistryValueKind.String);
                key.SetValue(QuarantinedAtValue, checkedAtUtc.ToString("O", CultureInfo.InvariantCulture), RegistryValueKind.String);
            }
            if (newRelocation is not null)
            {
                key.SetValue(RelocatedFromValue, newRelocation.From, RegistryValueKind.String);
                key.SetValue(RelocatedToValue, newRelocation.To, RegistryValueKind.String);
                key.SetValue(RelocationReasonValue, newRelocation.Reason, RegistryValueKind.String);
                key.SetValue(RelocatedAtValue, checkedAtUtc.ToString("O", CultureInfo.InvariantCulture), RegistryValueKind.String);
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
                ParseTime(key.GetValue(QuarantinedAtValue) as string))
            {
                RelocatedFrom = NullIfEmpty(key.GetValue(RelocatedFromValue) as string),
                RelocatedTo = NullIfEmpty(key.GetValue(RelocatedToValue) as string),
                RelocatedAtUtc = ParseTime(key.GetValue(RelocatedAtValue) as string),
                RelocationReason = NullIfEmpty(key.GetValue(RelocationReasonValue) as string),
                Folder = NullIfEmpty(key.GetValue(FolderValue) as string),
            };
        }
        catch
        {
            return DataFolderReport.None;
        }
    }

    private static string? NullIfEmpty(string? text) => string.IsNullOrEmpty(text) ? null : text;

    private static DateTimeOffset? ParseTime(string? text) =>
        DateTimeOffset.TryParse(text, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var value) ? value : null;
}
