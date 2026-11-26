using System.Collections.ObjectModel;
using System.Windows;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using ParentalControl.Admin.Services;
using ParentalControl.Core.Data;
using ParentalControl.Core.Models;

namespace ParentalControl.Admin.ViewModels;

public partial class DashboardViewModel : ObservableObject
{
    private readonly Action<UserRow> _navigateToDetail;

    [ObservableProperty]
    private ObservableCollection<UserRow> _standardUsers = [];

    [ObservableProperty]
    private ObservableCollection<UserRow> _adminUsers = [];

    [ObservableProperty]
    private ObservableCollection<EventRecord> _eventRecords = [];

    public Dictionary<string, string> SidToUsername { get; private set; } = new();

    /// <summary>True while the web admin does not work (sign-in not set up or data folder not secured). The dashboard then shows a banner.</summary>
    [ObservableProperty]
    private bool _isWebAdminLocked;

    /// <summary>The second line of the banner: what the parent must do.</summary>
    [ObservableProperty]
    private string _webAdminLockedHint = string.Empty;

    /// <summary>
    /// True while the setup lock after a move of the data folder is on (require-admin-sign-in S2): the service locks
    /// each session of a user who is not an administrator until a Restricted User exists or the parent finishes the setup.
    /// </summary>
    [ObservableProperty]
    private bool _isSetupLockActive;

    public DashboardViewModel(Action<UserRow> navigateToDetail)
    {
        _navigateToDetail = navigateToDetail;
    }

    public void LoadAll()
    {
        var credentialSetUp = AdminAccessRepository.IsCredentialSetUp();
        var folder = ParentalControl.Core.Platform.DataFolderStatusStore.Read();
        var serviceRunning = ServiceState.IsRunning();
        IsWebAdminLocked = !ParentalControl.Domain.DataFolder.AdminSignInStatus.IsWebAdminUsable(credentialSetUp, folder, serviceRunning);
        // SF3: the warnings about a stopped service and a program on port 5050 show first, also before the setup.
        WebAdminLockedHint = !serviceRunning
            ? "Do not sign in on this PC now: " + ParentalControl.Domain.DataFolder.AdminSignInStatus.ServiceStoppedWarning
            : ParentalControl.Domain.DataFolder.AdminSignInStatus.PortBlockedWarning(folder, TimeZoneInfo.Local) is { } portWarning
            ? "Do not sign in on this PC now: " + portWarning
            : credentialSetUp
                ? ParentalControl.Domain.DataFolder.AdminSignInStatus.Describe(credentialSetUp, null, folder, TimeZoneInfo.Local)
                : "Open Settings and select \"Set up sign-in\" under \"Web admin sign-in\".";
        RefreshUsers();
        RefreshEvents();
        RefreshSetupLock();
    }

    private void RefreshSetupLock()
    {
        try
        {
            IsSetupLockActive = !string.IsNullOrEmpty(SettingsRepository.Get(SettingsRepository.KeyDataFolderSetupLock))
                                && !UserRepository.GetAll().Any(u => u.IsRestricted);
        }
        catch
        {
            IsSetupLockActive = false;
        }
    }

    /// <summary>Ends the setup lock without a Restricted User (S2). The service reads the setting every 2 seconds.</summary>
    [RelayCommand]
    private void FinishSetup()
    {
        var answer = MessageBox.Show(
            "No user is marked as restricted. If you finish the setup now, Parental Control enforces no limit until you mark the child as restricted. Finish the setup?",
            "Finish setup", MessageBoxButton.YesNo, MessageBoxImage.Warning);
        if (answer != MessageBoxResult.Yes)
            return;
        // SF1: delete the registry value too, so the next start of the service does not set the lock again.
        ParentalControl.Core.Platform.DataFolderRegistry.Default.TryClearSetupLockPending();
        SettingsRepository.Set(SettingsRepository.KeyDataFolderSetupLock, string.Empty);
        RefreshSetupLock();
    }

    [RelayCommand]
    private void RefreshUsers()
    {
        try
        {
            var systemUsers = UserDiscovery.GetAllLocalUsers();

            foreach (var (sid, username, isAdmin) in systemUsers)
            {
                if (isAdmin) continue;
                var existing = UserRepository.GetBySid(sid);
                UserRepository.Upsert(sid, username, existing?.IsRestricted ?? false);
            }

            // Remove users who have been promoted to admin from the DB
            foreach (var (sid, username, isAdmin) in systemUsers)
            {
                if (!isAdmin) continue;
                UserRepository.DeleteBySid(sid);
            }

            var dbUsers = UserRepository.GetAll();
            var standard = dbUsers.Select(UserRow.FromDatabase).ToList();
            var admins = systemUsers
                .Where(u => u.IsAdmin)
                .Select(u => UserRow.ForAdmin(u.Sid, u.Username))
                .ToList();

            StandardUsers = new ObservableCollection<UserRow>(standard);
            AdminUsers = new ObservableCollection<UserRow>(admins);

            SidToUsername = standard.Concat(admins).ToDictionary(u => u.Sid, u => u.Username);
        }
        catch (Exception ex)
        {
            MessageBox.Show($"Error loading users: {ex.Message}", "Error",
                MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    [RelayCommand]
    private void RefreshEvents()
    {
        var events = EventRepository.GetEvents();
        foreach (var evt in events)
        {
            evt.Username = SidToUsername.TryGetValue(evt.UserSid, out var name) ? name : evt.UserSid;
        }
        EventRecords = new ObservableCollection<EventRecord>(events);
    }

    [RelayCommand]
    private void NavigateToUser(UserRow user)
    {
        _navigateToDetail(user);
    }
}
