namespace ParentalControl.Core.Models;

public sealed class SignInNotificationPayload
{
    public string Username { get; set; } = string.Empty;
    public string MachineName { get; set; } = string.Empty;
    public int RemainingMinutes { get; set; }
    public TimeOnly? Start { get; set; }
    public TimeOnly? End { get; set; }
}

public sealed class SignOutNotificationPayload
{
    public string Username { get; set; } = string.Empty;
    public string MachineName { get; set; } = string.Empty;
    public int MinutesUsedToday { get; set; }
}

public sealed class AdminSignInPayload
{
    public int SessionId { get; set; }
    public string Username { get; set; } = string.Empty;
    public string MachineName { get; set; } = string.Empty;
}

public sealed class PromptResponsePayload
{
    public int PromptId { get; set; }
    public int? SessionId { get; set; }
}

public sealed class AdminLoginBlockedPayload
{
    public string MachineName { get; set; } = string.Empty;
    public string Source { get; set; } = string.Empty;
    public bool AllSources { get; set; }
    public DateTime BlockedUntilLocal { get; set; }
}

public sealed class AdminSetupNeededPayload
{
    public string MachineName { get; set; } = string.Empty;
}

public sealed class DataFolderRelocatedPayload
{
    public string MachineName { get; set; } = string.Empty;
    public string From { get; set; } = string.Empty;
    public string To { get; set; } = string.Empty;
    public DateTime WhenLocal { get; set; }
}

public sealed class DataFolderQuarantinedPayload
{
    public string MachineName { get; set; } = string.Empty;
    public string MovedTo { get; set; } = string.Empty;
    public DateTime WhenLocal { get; set; }
}

/// <summary>The alert about a program that holds port 5050 of the web server (require-admin-sign-in, third review SF3).</summary>
public sealed class WebPortBlockedPayload
{
    public string MachineName { get; set; } = string.Empty;
    public string Owner { get; set; } = string.Empty;
    public DateTime WhenLocal { get; set; }
}
