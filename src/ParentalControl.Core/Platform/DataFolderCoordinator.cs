using System.Security.Cryptography;
using ParentalControl.Domain.DataFolder;

namespace ParentalControl.Core.Platform;

/// <summary>The result of the data folder step of the service (require-admin-sign-in, D14).</summary>
/// <param name="Folder">The data folder that the service uses, or null: then no folder works (lockdown).</param>
/// <param name="ConfiguredFolder">The folder from the registry or the default, or a text if the location is not usable.</param>
/// <param name="Result">The repairs and the move of the configured folder, if <see cref="DataFolderGuard.Secure"/> succeeded.</param>
/// <param name="Warning">The error of a step on a configured folder that is still trusted, or null.</param>
/// <param name="RelocationReason">Why the configured folder was not usable, if the service made a new folder; else null.</param>
/// <param name="Error">For a lockdown: all errors. Else null.</param>
/// <param name="RegistryError">The error if the service could not store the location of a new folder, else null.</param>
public sealed record DataFolderPreparation(
    string? Folder,
    string ConfiguredFolder,
    DataFolderResult? Result,
    string? Warning,
    string? RelocationReason,
    string? Error,
    string? RegistryError)
{
    public bool IsLockdown => Folder is null;
    public bool Relocated => Folder is not null && RelocationReason is not null;
}

/// <summary>
/// The data folder step of the service (require-admin-sign-in, D14). The service never stops the enforcement of the limits because of the
/// data folder. The decisions are in <see cref="DataFolderRecovery"/>; this class runs them:
/// <list type="number">
/// <item>Read the location from the registry (<see cref="DataFolderRegistry"/>), else the default.</item>
/// <item>Secure that folder (<see cref="DataFolderGuard.Secure"/>), with a few tries.</item>
/// <item>If it stays unsafe but is still trusted, use it with a warning (the settings stay).</item>
/// <item>Else leave it alone, create a new folder with a random name, and store its location in the registry.</item>
/// <item>If that fails too, return a lockdown result.</item>
/// </list>
/// </summary>
public static class DataFolderCoordinator
{
    /// <param name="random">The source of the random name bytes. Tests replace it to force a name that exists.</param>
    /// <param name="sleep">The wait between two tries. Tests replace it.</param>
    /// <param name="budget">The time limit of all walks of this step (CB1 c). Tests give a small budget.</param>
    /// <param name="lastSecuredFolder">The folder of the last Secured state without a warning, for the fast path (CB1 b).</param>
    public static DataFolderPreparation Prepare(DataFolderRegistry registry, string root,
        Func<byte[]>? random = null, Action<TimeSpan>? sleep = null, WalkBudget? budget = null, string? lastSecuredFolder = null)
    {
        random ??= () => RandomNumberGenerator.GetBytes(DataFolderLocation.RandomBytes);
        sleep ??= Thread.Sleep;
        budget ??= WalkBudget.Default();

        var reading = registry.ReadLocation(root);
        var configured = reading.Folder ?? $"(the location in HKLM\\{registry.KeyPath})";
        ConfiguredFolderResult outcome;
        string? configuredError = null;

        if (!reading.IsUsable)
        {
            outcome = ConfiguredFolderResult.InvalidLocation;
            configuredError = reading.Error;
        }
        else
        {
            var tries = 0;
            var timedOut = false;
            while (true)
            {
                tries++;
                try
                {
                    var result = DataFolderGuard.Secure(reading.Folder!, budget, lastSecuredFolder);
                    return new DataFolderPreparation(reading.Folder, configured, result, null, null, null, null);
                }
                catch (DataFolderWalkTimeoutException ex)
                {
                    configuredError = ex.Message;
                    timedOut = true;
                    break;
                }
                catch (DataFolderUnsafeException ex)
                {
                    configuredError = ex.Message;
                }
                if (!DataFolderRecovery.RetryConfigured(ConfiguredFolderResult.Failed, tries))
                    break;
                sleep(DataFolderRecovery.ConfiguredRetryDelay);
            }

            if (!timedOut)
            {
                try
                {
                    outcome = DataFolderGuard.FindReasonNotTrustedForUse(reading.Folder!, budget) is null
                        ? ConfiguredFolderResult.FailedButTrusted
                        : ConfiguredFolderResult.Failed;
                }
                catch (DataFolderWalkTimeoutException ex)
                {
                    configuredError += " " + ex.Message;
                    timedOut = true;
                    outcome = ConfiguredFolderResult.TimedOut;
                }
            }
            else
            {
                outcome = ConfiguredFolderResult.TimedOut;
            }

            if (timedOut)
            {
                // CB1 c: no new folder because of a slow walk. The quick check of the folder itself and of the items
                // that the service opens decides between the warning mode and the lockdown.
                outcome = DataFolderGuard.FindReasonNotTrustedForUse(reading.Folder!, shallow: true) is null
                    ? ConfiguredFolderResult.TimedOutButTrusted
                    : ConfiguredFolderResult.TimedOut;
            }
        }

        switch (DataFolderRecovery.Decide(outcome))
        {
            case DataFolderMode.UseConfiguredWithWarning:
                return new DataFolderPreparation(reading.Folder, configured, null, configuredError, null, null, null);
            case DataFolderMode.Lockdown:
                return new DataFolderPreparation(null, configured, null, null, null,
                    configuredError ?? "The check of the data folder reached its time limit.", null);
            case DataFolderMode.CreateNewFolder:
                return CreateNewFolder(registry, root, configured, configuredError ?? "unknown error", random);
            default:
                throw new InvalidOperationException("Secured is returned in the loop above.");
        }
    }

    private static DataFolderPreparation CreateNewFolder(DataFolderRegistry registry, string root, string configured,
        string configuredError, Func<byte[]> random)
    {
        var errors = new List<string> { configuredError };
        var tries = 0;
        while (true)
        {
            tries++;
            var folder = DataFolderLocation.FallbackFolder(root, random());
            NewFolderResult result;
            try
            {
                result = DataFolderGuard.CreateNewFolder(folder);
            }
            catch (DataFolderUnsafeException ex)
            {
                result = NewFolderResult.Failed;
                errors.Add(ex.Message);
            }

            if (result == NewFolderResult.Created)
            {
                string? registryError = null;
                try
                {
                    registry.WriteLocation(folder);
                }
                catch (Exception ex)
                {
                    // The service still uses the new folder at this start, so the limits stay on. The event log and
                    // the Admin app show the error.
                    registryError = $"The service could not store the location {folder} in HKLM\\{registry.KeyPath}: {ex.Message}";
                }
                return new DataFolderPreparation(folder, configured, null, null, configuredError, null, registryError);
            }
            if (result == NewFolderResult.NameExists)
                errors.Add($"{folder} exists already.");
            if (!DataFolderRecovery.TryAnotherName(result, tries))
                return new DataFolderPreparation(null, configured, null, null, null, string.Join(" ", errors), null);
        }
    }
}
