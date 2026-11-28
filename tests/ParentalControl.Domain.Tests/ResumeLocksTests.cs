using ParentalControl.Domain.Categories;

namespace ParentalControl.Domain.Tests;

/// <summary>
/// Spec: category-limits "Count a Child Resume", "Count many resumed processes as one Child Resume",
/// "Lock the session after repeated resumes" and "End of a Resume Lock" (issue #24).
/// </summary>
public class ResumeLocksTests
{
    private const int Kid = 2;
    private const int OtherKid = 3;
    private const string Game = @"c:\games\testgame.exe";
    private const string Video = @"c:\video\player.exe";
    private const string Chrome = @"c:\program files\google\chrome\application\chrome.exe";
    private static readonly DateOnly Today = new(2026, 11, 28);
    private static readonly DateTime T0 = DateTime.MinValue.AddHours(5); // a monotonic time
    private static readonly DateTime Start = new(2026, 11, 28, 9, 0, 0, DateTimeKind.Utc);
    private static readonly Func<int, int> NoBonus = _ => 0;
    private static readonly Func<int, string, bool> AllDue = (_, _) => true;

    private static ResumedProcess P(int pid, string exe = Game, int user = Kid) => new(pid, Start.AddSeconds(pid), user, exe);

    private static ResumeLocks Watching(params ResumedProcess[] processes)
    {
        var locks = new ResumeLocks();
        foreach (var p in processes)
            locks.MarkSuspended(p.Pid, p.StartTimeUtc);
        return locks;
    }

    /// <summary>The service suspends the process again (Suspend = Ok) and the child resumes it at <paramref name="at"/>.</summary>
    private static ResumeCheck ResumeAgain(ResumeLocks locks, ResumedProcess p, DateTime at, Func<int, int>? bonus = null)
    {
        locks.MarkSuspended(p.Pid, p.StartTimeUtc);
        return locks.Check([p], at, Today, bonus ?? NoBonus);
    }

    [Fact]
    public void Watched_process_that_runs_again_counts_one_Child_Resume()
    {
        var game = P(10);
        var locks = Watching(game);

        var check = locks.Check([game], T0, Today, NoBonus);

        Assert.Equal([Kid], check.Counted);
        Assert.Empty(check.Started);
        Assert.False(locks.IsWatched(game.Pid, game.StartTimeUtc));
    }

    [Fact]
    public void Process_that_the_service_never_suspended_does_not_count()
    {
        var locks = new ResumeLocks();
        Assert.Empty(locks.Check([P(10)], T0, Today, NoBonus).Counted);
    }

    [Fact]
    public void Ten_processes_resumed_in_one_check_count_one()
    {
        var chrome = Enumerable.Range(20, 10).Select(pid => P(pid, Chrome)).ToArray();
        var locks = Watching(chrome);

        var check = locks.Check(chrome, T0, Today, NoBonus);

        Assert.Equal([Kid], check.Counted);
        Assert.Empty(check.Started);
        Assert.False(locks.IsLocked(Kid));
    }

    [Fact]
    public void Same_process_does_not_count_again_during_its_Pending_Suspension()
    {
        var game = P(10);
        var locks = Watching(game);
        locks.Check([game], T0, Today, NoBonus);

        // The next checks still see the process running while its Pending Suspension runs.
        Assert.Empty(locks.Check([game], T0.AddSeconds(60), Today, NoBonus).Counted);
        Assert.Empty(locks.Check([game], T0.AddSeconds(120), Today, NoBonus).Counted);
    }

    [Fact]
    public void Check_less_than_30_seconds_after_a_counted_Child_Resume_does_not_count()
    {
        var game = P(10);
        var locks = Watching(game);
        locks.Check([game], T0, Today, NoBonus);

        Assert.Empty(ResumeAgain(locks, game, T0.AddSeconds(15)).Counted);
        // The resume of that check stays unwatched: the planner suspends it again without a count.
        Assert.False(locks.IsWatched(game.Pid, game.StartTimeUtc));
        Assert.Equal([Kid], ResumeAgain(locks, game, T0.AddSeconds(45)).Counted);
    }

    [Fact]
    public void Check_30_seconds_after_a_counted_Child_Resume_counts()
    {
        var game = P(10);
        var locks = Watching(game);
        locks.Check([game], T0, Today, NoBonus);

        Assert.Equal([Kid], ResumeAgain(locks, game, T0 + ResumeLocks.MinGap).Counted);
    }

    [Fact]
    public void Three_Child_Resumes_in_10_minutes_start_one_lock()
    {
        var game = P(10);
        var locks = new ResumeLocks();

        Assert.Empty(ResumeAgain(locks, game, T0).Started);
        Assert.Empty(ResumeAgain(locks, game, T0.AddMinutes(1)).Started);
        var third = ResumeAgain(locks, game, T0.AddMinutes(2));

        var start = Assert.Single(third.Started);
        Assert.Equal(Kid, start.UserId);
        Assert.Equal([Game], start.ExePaths);
        Assert.True(locks.IsLocked(Kid));
        Assert.Equal([Kid], locks.LockedUsers);
    }

    [Fact]
    public void Two_Child_Resumes_do_not_lock()
    {
        var game = P(10);
        var locks = new ResumeLocks();
        ResumeAgain(locks, game, T0);
        ResumeAgain(locks, game, T0.AddMinutes(5));

        Assert.False(locks.IsLocked(Kid));
    }

    [Fact]
    public void Three_Child_Resumes_over_10_minutes_do_not_lock()
    {
        var game = P(10);
        var locks = new ResumeLocks();
        ResumeAgain(locks, game, T0);
        ResumeAgain(locks, game, T0.AddMinutes(5));
        var third = ResumeAgain(locks, game, T0.AddMinutes(10));

        Assert.Empty(third.Started);
        Assert.False(locks.IsLocked(Kid));
        // The window slides: the resume at 10 min is the second one of a new window.
        Assert.Single(ResumeAgain(locks, game, T0.AddMinutes(11)).Started);
    }

    [Fact]
    public void Different_exes_of_one_user_count_together()
    {
        var game = P(10);
        var video = P(11, Video);
        var locks = new ResumeLocks();
        ResumeAgain(locks, game, T0);
        ResumeAgain(locks, video, T0.AddMinutes(3));
        var third = ResumeAgain(locks, game, T0.AddMinutes(6));

        var start = Assert.Single(third.Started);
        Assert.Equal(new[] { Game, Video }.Order(StringComparer.OrdinalIgnoreCase), start.ExePaths);
        Assert.Equal(start.ExePaths, locks.LockedExes(Kid));
    }

    [Fact]
    public void Two_users_count_apart()
    {
        var kidGame = P(10);
        var otherGame = P(11, Game, OtherKid);
        var locks = new ResumeLocks();
        ResumeAgain(locks, kidGame, T0);
        ResumeAgain(locks, otherGame, T0.AddMinutes(1));
        ResumeAgain(locks, kidGame, T0.AddMinutes(2));
        ResumeAgain(locks, otherGame, T0.AddMinutes(3));

        Assert.False(locks.IsLocked(Kid));
        Assert.False(locks.IsLocked(OtherKid));
    }

    [Fact]
    public void Gap_is_per_user()
    {
        var kidGame = P(10);
        var otherGame = P(11, Game, OtherKid);
        var locks = Watching(kidGame, otherGame);

        locks.Check([kidGame], T0, Today, NoBonus);
        var check = locks.Check([otherGame], T0.AddSeconds(5), Today, NoBonus);

        Assert.Equal([OtherKid], check.Counted);
    }

    [Fact]
    public void Resume_for_a_Session_End_Hold_does_not_count()
    {
        // The service resumed the game for a sign-out; the hold ended without the end of the session.
        var game = P(10);
        var locks = Watching(game);
        locks.MarkResumedByService(game.Pid, game.StartTimeUtc);

        Assert.Empty(locks.Check([game], T0, Today, NoBonus).Counted);

        // The service suspends it again after the hold; a later resume of the child counts.
        Assert.Equal([Kid], ResumeAgain(locks, game, T0.AddMinutes(1)).Counted);
    }

    [Fact]
    public void Cancelled_sign_outs_do_not_lock()
    {
        var game = P(10);
        var locks = new ResumeLocks();
        for (var i = 0; i < 3; i++)
        {
            locks.MarkSuspended(game.Pid, game.StartTimeUtc); // suspended again after the hold or the guard
            locks.MarkResumedByService(game.Pid, game.StartTimeUtc); // resumed for the next sign-out
            Assert.Empty(locks.Check([game], T0.AddMinutes(3 * i), Today, NoBonus).Counted);
        }
        Assert.False(locks.IsLocked(Kid));
    }

    [Fact]
    public void Process_covered_by_a_Session_End_Guard_does_not_count()
    {
        // The Session Agent resumed the game for a sign-out after a refused hold; the guard covered it.
        var game = P(10);
        var pad = P(11, @"c:\windows\notepad.exe");
        var locks = Watching(game, pad);
        locks.MarkResumedByService(game.Pid, game.StartTimeUtc);
        locks.MarkResumedByService(pad.Pid, pad.StartTimeUtc);

        Assert.Empty(locks.Check([game, pad], T0, Today, NoBonus).Counted);
    }

    [Fact]
    public void Retain_drops_a_process_without_a_record()
    {
        var game = P(10);
        var video = P(11, Video);
        var locks = Watching(game, video);

        locks.Retain(new HashSet<(int, DateTime)> { (video.Pid, video.StartTimeUtc) });

        Assert.False(locks.IsWatched(game.Pid, game.StartTimeUtc));
        Assert.True(locks.IsWatched(video.Pid, video.StartTimeUtc));
    }

    private static ResumeLocks Locked(Func<int, int>? bonus = null)
    {
        var game = P(10);
        var locks = new ResumeLocks();
        ResumeAgain(locks, game, T0, bonus);
        ResumeAgain(locks, game, T0.AddMinutes(1), bonus);
        ResumeAgain(locks, game, T0.AddMinutes(2), bonus);
        Assert.True(locks.IsLocked(Kid));
        return locks;
    }

    [Fact]
    public void Lock_stays_while_a_resumed_exe_is_still_due()
    {
        var locks = Locked();
        Assert.Empty(locks.Release(Today, NoBonus, AllDue));
        Assert.True(locks.IsLocked(Kid));
    }

    [Fact]
    public void Next_day_ends_the_lock()
    {
        var locks = Locked();
        var end = Assert.Single(locks.Release(Today.AddDays(1), NoBonus, AllDue));
        Assert.Equal(new ResumeLockEnd(Kid, ResumeLockEndReason.NewDay), end);
        Assert.False(locks.IsLocked(Kid));
    }

    [Fact]
    public void More_Bonus_Minutes_end_the_lock()
    {
        // Bonus Minutes from before the lock do not end it; new ones do.
        var locks = Locked(_ => 10);
        Assert.Empty(locks.Release(Today, _ => 10, AllDue));

        var end = Assert.Single(locks.Release(Today, _ => 25, AllDue));
        Assert.Equal(ResumeLockEndReason.BonusMinutes, end.Reason);
    }

    [Fact]
    public void Lock_ends_when_no_resumed_exe_is_due_any_more()
    {
        var game = P(10);
        var video = P(11, Video);
        var locks = new ResumeLocks();
        ResumeAgain(locks, game, T0);
        ResumeAgain(locks, video, T0.AddMinutes(1));
        ResumeAgain(locks, game, T0.AddMinutes(2));

        // The parent raised the Games limit: the video player is still exhausted.
        Assert.Empty(locks.Release(Today, NoBonus, (_, path) => path == Video));
        var end = Assert.Single(locks.Release(Today, NoBonus, (_, _) => false));
        Assert.Equal(ResumeLockEndReason.NoReasonLeft, end.Reason);
    }

    [Fact]
    public void After_the_end_a_new_lock_needs_three_new_Child_Resumes()
    {
        var game = P(10);
        var locks = Locked();
        locks.Release(Today, _ => 5, AllDue);

        Assert.Empty(ResumeAgain(locks, game, T0.AddMinutes(3)).Started);
        Assert.Empty(ResumeAgain(locks, game, T0.AddMinutes(4)).Started);
        Assert.Single(ResumeAgain(locks, game, T0.AddMinutes(5)).Started);
    }

    [Fact]
    public void Child_Resume_during_a_lock_adds_its_exe_and_starts_no_second_lock()
    {
        var video = P(11, Video);
        var locks = Locked();

        var check = ResumeAgain(locks, video, T0.AddMinutes(3));

        Assert.Equal([Kid], check.Counted);
        Assert.Empty(check.Started);
        Assert.Contains(Video, locks.LockedExes(Kid));
        // The lock stays while the video player is still due.
        Assert.Empty(locks.Release(Today, NoBonus, (_, path) => path == Video));
    }
}
