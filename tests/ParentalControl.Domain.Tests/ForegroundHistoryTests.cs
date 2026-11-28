using ParentalControl.Domain.Categories;

namespace ParentalControl.Domain.Tests;

public class ForegroundHistoryTests
{
    private static readonly DateOnly Today = new(2026, 11, 25);

    [Fact]
    public void Recorded_path_was_foreground_with_any_letter_case_and_form()
    {
        var history = new ForegroundHistory();
        Assert.Equal(ForegroundRecord.Recorded, history.Record(1, Today, @"C:\Games\x.exe"));
        Assert.True(history.WasForeground(1, Today, @"c:\games\X.EXE"));
        Assert.True(history.WasForeground(1, Today, @"C:\Games\sub\..\x.exe"));
    }

    [Fact]
    public void Other_user_and_other_day_do_not_see_the_path()
    {
        var history = new ForegroundHistory();
        history.Record(1, Today, @"C:\Games\x.exe");
        Assert.False(history.WasForeground(2, Today, @"C:\Games\x.exe"));
        Assert.False(history.WasForeground(1, Today.AddDays(1), @"C:\Games\x.exe"));
    }

    [Fact]
    public void New_day_starts_an_empty_set()
    {
        var history = new ForegroundHistory();
        history.Record(1, Today, @"C:\Games\x.exe");
        history.Record(1, Today.AddDays(1), @"C:\Games\y.exe");
        Assert.False(history.WasForeground(1, Today.AddDays(1), @"C:\Games\x.exe"));
        Assert.True(history.WasForeground(1, Today.AddDays(1), @"C:\Games\y.exe"));
    }

    [Fact]
    public void Path_that_is_not_absolute_is_ignored()
    {
        var history = new ForegroundHistory();
        Assert.Equal(ForegroundRecord.InvalidPath, history.Record(1, Today, "x.exe"));
        Assert.Equal(ForegroundRecord.InvalidPath, history.Record(1, Today, null));
        Assert.Equal(ForegroundRecord.InvalidPath, history.Record(1, Today, @"\\?\GLOBALROOT\Device\HarddiskVolumeShadowCopy1\game.exe"));
        Assert.False(history.WasForeground(1, Today, "x.exe"));
    }

    [Fact]
    public void Invalid_path_at_a_full_set_is_still_InvalidPath_not_Full()
    {
        var history = new ForegroundHistory();
        for (var i = 0; i < ForegroundHistory.MaxPathsPerUser; i++)
            history.Record(1, Today, $@"C:\Fake\{i}.exe");
        Assert.Equal(ForegroundRecord.InvalidPath, history.Record(1, Today, "game.exe"));
    }

    [Fact]
    public void Set_has_a_size_limit_per_user_and_day()
    {
        var history = new ForegroundHistory();
        for (var i = 0; i < ForegroundHistory.MaxPathsPerUser; i++)
            Assert.Equal(ForegroundRecord.Recorded, history.Record(1, Today, $@"C:\Fake\{i}.exe"));
        Assert.Equal(ForegroundRecord.Full, history.Record(1, Today, @"C:\Fake\one-more.exe"));
        Assert.Equal(ForegroundRecord.Recorded, history.Record(1, Today, @"C:\Fake\0.exe"));
        Assert.Equal(ForegroundRecord.Recorded, history.Record(2, Today, @"C:\Fake\one-more.exe"));
    }

    [Fact]
    public void Cap_is_60000_paths_and_Record_returns_Full_at_the_cap()
    {
        Assert.Equal(60_000, ForegroundHistory.MaxPathsPerUser);
        var history = new ForegroundHistory();
        for (var i = 0; i < ForegroundHistory.MaxPathsPerUser; i++)
            history.Record(1, Today, $@"C:\Probe\{i}.exe");
        Assert.Equal(ForegroundRecord.Full, history.Record(1, Today, @"C:\Probe\extra.exe"));
        Assert.False(history.WasForeground(1, Today, @"C:\Probe\extra.exe"));
    }
}
