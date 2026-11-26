using ParentalControl.Domain.DataFolder;

namespace ParentalControl.Domain.Tests.DataFolder;

public class DataFolderLocationTests
{
    private const string Root = @"C:\ProgramData";
    private const string Hex = "0123456789abcdef0123456789abcdef";

    [Fact]
    public void Missing_value_gives_the_default_folder()
    {
        var reading = DataFolderLocation.Resolve(keyError: null, value: null, Root);
        Assert.True(reading.IsUsable);
        Assert.Equal(@"C:\ProgramData\ParentalControl", reading.Folder);
        Assert.Equal(DataFolderLocationSource.Default, reading.Source);
    }

    [Fact]
    public void Value_with_a_new_folder_of_the_service_is_used()
    {
        var reading = DataFolderLocation.Resolve(null, @"C:\ProgramData\ParentalControl-" + Hex, Root);
        Assert.True(reading.IsUsable);
        Assert.Equal(@"C:\ProgramData\ParentalControl-" + Hex, reading.Folder);
        Assert.Equal(DataFolderLocationSource.Registry, reading.Source);
    }

    [Theory]
    [InlineData(@"C:\Users\Kid\ParentalControl")]
    [InlineData(@"C:\ProgramData\Other")]
    [InlineData(@"C:\ProgramData\ParentalControl-1234")]
    [InlineData(@"C:\ProgramData\ParentalControl-0123456789abcdef0123456789abcdeg")]
    [InlineData(@"C:\ProgramData\ParentalControl.untrusted-0123456789abcdef0123456789abcdef")]
    [InlineData(@"C:\ProgramData\x\..\ParentalControl")]
    [InlineData(@"C:\ProgramData\ParentalControl\sub")]
    [InlineData(@"\\server\share\ParentalControl")]
    [InlineData("")]
    public void Other_value_is_not_usable_and_never_falls_back_to_the_default(string value)
    {
        var reading = DataFolderLocation.Resolve(null, value, Root);
        Assert.False(reading.IsUsable);
        Assert.Null(reading.Folder);
        Assert.NotNull(reading.Error);
    }

    [Fact]
    public void Untrusted_key_is_not_usable_also_without_a_value()
    {
        var reading = DataFolderLocation.Resolve("S-1-5-32-545 has write rights (0x2)", null, Root);
        Assert.False(reading.IsUsable);
        Assert.Contains("not trusted", reading.Error);
    }

    [Fact]
    public void New_folder_name_has_128_random_bits_in_hex()
    {
        var bytes = Enumerable.Range(0, 16).Select(i => (byte)(i * 17)).ToArray();
        var name = DataFolderLocation.FallbackName(bytes);
        Assert.Equal("ParentalControl-00112233445566778899aabbccddeeff", name);
        Assert.True(DataFolderLocation.IsFallbackName(name));
        Assert.True(DataFolderLocation.IsAccepted(DataFolderLocation.FallbackFolder(Root, bytes), Root));
        Assert.Throws<ArgumentException>(() => DataFolderLocation.FallbackName(new byte[15]));
    }

    [Fact]
    public void Root_with_a_trailing_separator_gives_the_same_folder()
    {
        Assert.Equal(@"C:\ProgramData\ParentalControl", DataFolderLocation.DefaultFolder(@"C:\ProgramData\"));
        Assert.True(DataFolderLocation.IsAccepted(@"c:\programdata\parentalcontrol", @"C:\ProgramData\"));
    }
}

public class DataFolderRecoveryTests
{
    [Fact]
    public void Secured_folder_is_used()
    {
        Assert.Equal(DataFolderMode.UseConfigured, DataFolderRecovery.Decide(ConfiguredFolderResult.Secured));
        Assert.False(DataFolderRecovery.RetryConfigured(ConfiguredFolderResult.Secured, 1));
    }

    [Fact]
    public void Failed_folder_is_tried_again_before_a_new_folder()
    {
        Assert.True(DataFolderRecovery.RetryConfigured(ConfiguredFolderResult.Failed, 1));
        Assert.True(DataFolderRecovery.RetryConfigured(ConfiguredFolderResult.Failed, DataFolderRecovery.ConfiguredTries - 1));
        Assert.False(DataFolderRecovery.RetryConfigured(ConfiguredFolderResult.Failed, DataFolderRecovery.ConfiguredTries));
        Assert.Equal(DataFolderMode.CreateNewFolder, DataFolderRecovery.Decide(ConfiguredFolderResult.Failed));
    }

    [Fact]
    public void Trusted_folder_with_a_failed_step_keeps_its_settings()
    {
        Assert.Equal(DataFolderMode.UseConfiguredWithWarning, DataFolderRecovery.Decide(ConfiguredFolderResult.FailedButTrusted));
        Assert.False(DataFolderRecovery.RetryConfigured(ConfiguredFolderResult.FailedButTrusted, 1));
    }

    [Fact]
    public void Invalid_location_gives_a_new_folder_at_once()
    {
        Assert.False(DataFolderRecovery.RetryConfigured(ConfiguredFolderResult.InvalidLocation, 1));
        Assert.Equal(DataFolderMode.CreateNewFolder, DataFolderRecovery.Decide(ConfiguredFolderResult.InvalidLocation));
    }

    [Fact]
    public void Existing_name_or_error_gives_another_name_until_the_limit()
    {
        Assert.True(DataFolderRecovery.TryAnotherName(NewFolderResult.NameExists, 1));
        Assert.True(DataFolderRecovery.TryAnotherName(NewFolderResult.Failed, 1));
        Assert.False(DataFolderRecovery.TryAnotherName(NewFolderResult.Created, 1));
        Assert.False(DataFolderRecovery.TryAnotherName(NewFolderResult.NameExists, DataFolderRecovery.NewFolderTries));
    }

    [Fact]
    public void Lockdown_locks_often_and_retries_each_minute()
    {
        Assert.True(DataFolderRecovery.LockInterval <= TimeSpan.FromSeconds(10));
        Assert.Equal(TimeSpan.FromMinutes(1), DataFolderRecovery.RetryInterval);
    }
}

public class LockdownPolicyTests
{
    [Fact]
    public void Locks_each_active_session_that_is_not_an_administrator()
    {
        var sessions = new[]
        {
            new LockdownSession(1, IsActive: true, SessionAdminStatus.Administrator),
            new LockdownSession(2, IsActive: true, SessionAdminStatus.NotAdministrator),
            new LockdownSession(3, IsActive: false, SessionAdminStatus.NotAdministrator),
            new LockdownSession(4, IsActive: true, SessionAdminStatus.Unknown),
        };
        Assert.Equal(new[] { 2, 4 }, LockdownPolicy.SessionsToLock(sessions));
    }

    [Fact]
    public void No_session_gives_no_lock()
    {
        Assert.Empty(LockdownPolicy.SessionsToLock(Array.Empty<LockdownSession>()));
    }
}
