using System.Windows;
using ParentalControl.Core.Data;
using ParentalControl.Core.Platform;
using ParentalControl.Domain.DataFolder;

namespace ParentalControl.Admin;

public partial class App : Application
{
    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        try
        {
            DatabaseManager.Initialize();
        }
        catch (DataFolderUnsafeException ex)
        {
            // Fail closed: the app does not open a database that another account could read or change.
            var service = DataFolderStatusStore.Read();
            var serviceLine = service.State == DataFolderState.Lockdown
                ? $"\n\nThe service has no usable data folder either. It locks each session of a user who is not an administrator and tries again every minute. Last error: {service.Detail}"
                : string.Empty;
            MessageBox.Show(
                $"Parental Control Admin cannot open the data folder, because it is not safe:\n\n{ex.Message}{serviceLine}\n\n" +
                "The service repairs the folder, or makes a new one, at its next start. Restart the PC (or the service \"ParentalControl.Service\"), then open this app again.",
                "Parental Control Admin", MessageBoxButton.OK, MessageBoxImage.Error);
            StartupUri = null; // no main window
            Shutdown(1);
        }
    }
}
