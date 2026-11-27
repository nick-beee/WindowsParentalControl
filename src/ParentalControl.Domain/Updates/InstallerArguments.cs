namespace ParentalControl.Domain.Updates;

/// <summary>
/// The command line of the setup that the updater runs (request-window, design D7). The setup writes its log to the
/// logs folder of the data folder, where the parent also finds the service log. A setup that SYSTEM starts would else
/// log to the temp folder of SYSTEM.
/// </summary>
public static class InstallerArguments
{
    public const string SilentArguments = "/VERYSILENT /SUPPRESSMSGBOXES /NORESTART";

    /// <summary>The log file name for a setup that starts at <paramref name="localTime"/>.</summary>
    public static string LogFileName(DateTime localTime) => $"setup-{localTime:yyyyMMdd-HHmmss}.log";

    /// <summary>
    /// The arguments: the silent switches (if <paramref name="silent"/>) and <c>/LOG="&lt;logsDirectory&gt;\setup-….log"</c>.
    /// Without a usable logs folder (null, empty, or with a quote that would break the command line), the arguments
    /// have no <c>/LOG</c>. The caller creates the folder before the start.
    /// </summary>
    public static string Build(bool silent, string? logsDirectory, DateTime localTime)
    {
        var parts = new List<string>();
        if (silent) parts.Add(SilentArguments);
        if (!string.IsNullOrWhiteSpace(logsDirectory) && logsDirectory.IndexOf('"') < 0)
            parts.Add($"/LOG=\"{logsDirectory.TrimEnd('\\')}\\{LogFileName(localTime)}\"");
        return string.Join(' ', parts);
    }
}
