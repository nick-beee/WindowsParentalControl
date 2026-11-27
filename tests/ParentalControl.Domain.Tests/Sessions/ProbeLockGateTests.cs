using ParentalControl.Domain.Categories;
using ParentalControl.Domain.Sessions;
using Xunit;

namespace ParentalControl.Domain.Tests.Sessions;

public class ProbeLockGateTests
{
    private static readonly DateOnly Day = new(2026, 11, 27);

    private static TimeSpan S(double seconds) => TimeSpan.FromSeconds(seconds);

    [Fact]
    public void Off_gives_would_lock_and_on_gives_lock()
    {
        var gate = new ProbeLockGate(enforced: false);
        Assert.Equal(ProbeLockAction.WouldLock, gate.Decide());
        Assert.True(gate.SetEnforced(true));
        Assert.Equal(ProbeLockAction.Lock, gate.Decide());
        Assert.False(gate.SetEnforced(true));
    }

    [Fact]
    public void At_most_one_event_per_user_and_minute_with_the_number_of_decisions()
    {
        var gate = new ProbeLockGate(false);
        var first = gate.WouldLock(1, Day, ProbeLockKind.Strike, null, S(100));
        Assert.True(first.WriteEvent);
        Assert.Equal(1, first.Decisions);

        Assert.False(gate.WouldLock(1, Day, ProbeLockKind.Strike, null, S(105)).WriteEvent);
        Assert.False(gate.WouldLock(1, Day, ProbeLockKind.Strike, null, S(130)).WriteEvent);
        // Another user has its own minute.
        Assert.True(gate.WouldLock(2, Day, ProbeLockKind.Strike, null, S(131)).WriteEvent);

        var next = gate.WouldLock(1, Day, ProbeLockKind.Strike, null, S(161));
        Assert.True(next.WriteEvent);
        Assert.Equal(3, next.Decisions); // the two skipped decisions and this one
    }

    [Fact]
    public void One_alert_per_user_day_and_reason()
    {
        var gate = new ProbeLockGate(false);
        Assert.True(gate.WouldLock(1, Day, ProbeLockKind.Budget, null, S(0)).Alert);
        Assert.False(gate.WouldLock(1, Day, ProbeLockKind.Budget, null, S(100)).Alert);
        Assert.True(gate.WouldLock(1, Day, ProbeLockKind.Strike, null, S(200)).Alert);
        Assert.True(gate.WouldLock(1, Day, ProbeLockKind.FullSet, null, S(300)).Alert);
        Assert.True(gate.WouldLock(1, Day.AddDays(1), ProbeLockKind.Budget, null, S(400)).Alert);
    }

    [Fact]
    public void Programs_that_are_never_suspended_get_one_alert_per_exe_and_day()
    {
        // Spec scenario "Programs that are never suspended in shadow mode": one alert for each of the two exe paths.
        var gate = new ProbeLockGate(false);
        Assert.True(gate.WouldLock(1, Day, ProbeLockKind.NeverSuspended, @"C:\Windows\notepad.exe", S(0)).Alert);
        Assert.False(gate.WouldLock(1, Day, ProbeLockKind.NeverSuspended, @"C:\WINDOWS\NOTEPAD.EXE", S(70)).Alert);
        Assert.True(gate.WouldLock(1, Day, ProbeLockKind.NeverSuspended, @"C:\Windows\System32\mspaint.exe", S(140)).Alert);
    }

    [Fact]
    public void Extra_category_log_names_entries_that_add_a_category_of_their_own_once_per_hour()
    {
        var log = new ExtraCategoryLog();
        var game = new CountedProcess(new ProcessIdentity(10, DateTime.UnixEpoch), @"C:\Games\game.exe", AppCategory.Games, ProbeRole.Child, false);
        var study = new CountedProcess(new ProcessIdentity(11, DateTime.UnixEpoch), @"C:\Study\app.exe", AppCategory.Study, ProbeRole.Foreground, false);
        var sameAsFg = new CountedProcess(new ProcessIdentity(12, DateTime.UnixEpoch), @"C:\Study\helper.exe", AppCategory.Study, ProbeRole.Focus, false);

        var due = log.Due([study, game, sameAsFg], S(0));
        Assert.Equal([game], due);
        Assert.Empty(log.Due([study, game], S(1800)));
        Assert.Equal([game], log.Due([study, game], S(3600)));
    }

    [Fact]
    public void Daily_once_gives_true_once_per_key_and_day()
    {
        var once = new DailyOnce();
        Assert.True(once.Take("gap:1", Day));
        Assert.False(once.Take("GAP:1", Day));
        Assert.True(once.Take("gap:2", Day));
        Assert.True(once.Take("gap:1", Day.AddDays(1)));
    }
}
