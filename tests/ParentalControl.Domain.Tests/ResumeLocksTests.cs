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

    // The CPU time of a process when the service suspends it, and after the child resumed it and played (design D10).
    private static readonly TimeSpan SuspendedCpu = TimeSpan.FromSeconds(4);
    private static readonly TimeSpan PlayedCpu = SuspendedCpu + TimeSpan.FromSeconds(2);

    /// <summary>A recorded process that runs again. By default it used 2 s of CPU time after its suspension.</summary>
    private static ResumedProcess P(int pid, string exe = Game, int user = Kid, TimeSpan? cpu = null) =>
        new(pid, Start.AddSeconds(pid), user, exe, cpu ?? PlayedCpu);

    private static ResumeLocks Watching(params ResumedProcess[] processes)
    {
        var locks = new ResumeLocks();
        foreach (var p in processes)
            locks.MarkSuspended(p.Pid, p.StartTimeUtc, SuspendedCpu);
        return locks;
    }

    /// <summary>The service suspends the process again (Suspend = Ok) and the child resumes it at <paramref name="at"/>.</summary>
    private static ResumeCheck ResumeAgain(ResumeLocks locks, ResumedProcess p, DateTime at, Func<int, int>? bonus = null)
    {
        locks.MarkSuspended(p.Pid, p.StartTimeUtc, SuspendedCpu);
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
            locks.MarkSuspended(game.Pid, game.StartTimeUtc, SuspendedCpu); // suspended again after the hold or the guard
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
    // Evidence of the run: the CPU time (design D10).

    [Fact]
    public void Process_that_used_no_CPU_time_since_its_suspension_does_not_count_and_stays_watched()
    {
        // VM 102, 2026-11-28: the thread check saw the re-suspended TestGame running at 13:38:52, but no tool ran.
        var idle = P(10, cpu: SuspendedCpu);
        var locks = Watching(idle);

        var check = locks.Check([idle], T0, Today, NoBonus);

        Assert.Empty(check.Counted);
        var unproven = Assert.Single(check.Unproven);
        Assert.Equal(ResumeEvidence.NoGrowth, unproven.Evidence);
        Assert.Equal(TimeSpan.Zero, unproven.CpuGrowth);
        Assert.True(locks.IsWatched(idle.Pid, idle.StartTimeUtc));
    }

    [Fact]
    public void Failed_CPU_read_does_not_count()
    {
        var game = P(10) with { CpuTime = null };
        var locks = Watching(game);

        var check = locks.Check([game], T0, Today, NoBonus);

        Assert.Empty(check.Counted);
        Assert.Equal(ResumeEvidence.ReadFailed, Assert.Single(check.Unproven).Evidence);
        Assert.True(locks.IsWatched(game.Pid, game.StartTimeUtc));
    }

    [Fact]
    public void Unknown_CPU_time_at_the_suspension_does_not_count()
    {
        var game = P(10);
        var locks = new ResumeLocks();
        locks.MarkSuspended(game.Pid, game.StartTimeUtc, null);

        var check = locks.Check([game], T0, Today, NoBonus);

        Assert.Empty(check.Counted);
        Assert.Equal(ResumeEvidence.NoBaseline, Assert.Single(check.Unproven).Evidence);
    }

    [Fact]
    public void Failed_read_at_a_later_suspended_check_keeps_the_known_CPU_time()
    {
        var game = P(10);
        var locks = Watching(game);
        locks.MarkSuspended(game.Pid, game.StartTimeUtc, null); // the next tick sees it suspended, but the read fails

        Assert.Equal([Kid], locks.Check([game], T0, Today, NoBonus).Counted);
    }

    [Fact]
    public void Suspended_check_moves_the_CPU_time_forward()
    {
        // The service sees the process suspended with more CPU time (for example the delayed suspension of a thread).
        var game = P(10, cpu: PlayedCpu + TimeSpan.FromMilliseconds(10));
        var locks = Watching(game);
        locks.MarkSuspended(game.Pid, game.StartTimeUtc, PlayedCpu);

        Assert.Empty(locks.Check([game], T0, Today, NoBonus).Counted);
    }

    [Fact]
    public void No_growth_check_does_not_use_the_30_second_gap()
    {
        var idle = P(10, cpu: SuspendedCpu);
        var game = P(11);
        var locks = Watching(idle, game);

        Assert.Empty(locks.Check([idle], T0, Today, NoBonus).Counted);
        Assert.Equal([Kid], locks.Check([game], T0.AddSeconds(10), Today, NoBonus).Counted);
    }

    [Fact]
    public void Process_without_growth_counts_later_when_it_really_runs()
    {
        var game = P(10);
        var locks = Watching(game);

        Assert.Empty(locks.Check([game with { CpuTime = SuspendedCpu }], T0, Today, NoBonus).Counted);
        // The child resumes it before the Pending Suspension ends; the next check sees the growth.
        Assert.Equal([Kid], locks.Check([game], T0.AddSeconds(10), Today, NoBonus).Counted);
    }

    [Fact]
    public void Restarted_process_is_not_a_resume_of_the_old_record()
    {
        // The game ended and a new process got the same pid: another start time, so another key.
        var old = P(10);
        var restarted = old with { StartTimeUtc = old.StartTimeUtc.AddMinutes(5) };
        var locks = Watching(old);

        var check = locks.Check([restarted], T0, Today, NoBonus);

        Assert.Empty(check.Counted);
        Assert.Empty(check.Unproven);
        Assert.True(locks.IsWatched(old.Pid, old.StartTimeUtc));
    }

    [Fact]
    public void Ten_processes_count_one_if_only_one_of_them_ran()
    {
        var chrome = Enumerable.Range(20, 10).Select(pid => P(pid, Chrome, cpu: pid == 25 ? PlayedCpu : SuspendedCpu)).ToArray();
        var locks = Watching(chrome);

        var check = locks.Check(chrome, T0, Today, NoBonus);

        Assert.Equal([Kid], check.Counted);
        Assert.Equal(9, check.Unproven.Count);
    }

    [Fact]
    public void Three_checks_without_growth_start_no_Resume_Lock()
    {
        var game = P(10, cpu: SuspendedCpu);
        var locks = Watching(game);

        for (var i = 0; i < 3; i++)
        {
            locks.MarkSuspended(game.Pid, game.StartTimeUtc, SuspendedCpu); // suspended again (Suspend = Ok)
            Assert.Empty(locks.Check([game], T0.AddMinutes(i), Today, NoBonus).Counted);
        }
        Assert.False(locks.IsLocked(Kid));
        Assert.Equal(0, locks.ChildResumes(Kid));
    }

    // --- A script that suspends the game before each check and resumes it after (design D11) ---

    /// <summary>The game as a check sees it suspended, with <paramref name="cpu"/> as its user-mode CPU time.</summary>
    private static ResumedProcess Seen(ResumedProcess p, TimeSpan? cpu) => p with { CpuTime = cpu };

    [Fact]
    public void Self_suspending_script_counts_on_each_check_and_starts_a_Resume_Lock()
    {
        // The service suspended the game (Suspend = Ok). The script resumes it about 2 s after each check and suspends it
        // about 2 s before the next one, so each check sees it suspended, but it played about 56 s in between.
        var game = P(10);
        var locks = Watching(game);
        var cpu = SuspendedCpu;

        for (var i = 0; i < 3; i++)
        {
            cpu += TimeSpan.FromSeconds(20);
            var check = locks.Check([], [Seen(game, cpu)], T0.AddMinutes(i + 1), Today, NoBonus);
            Assert.Equal([Kid], check.Counted);
            Assert.Equal(i == 2, check.Started.Count == 1);
            Assert.True(locks.IsWatched(game.Pid, game.StartTimeUtc));
        }

        Assert.True(locks.IsLocked(Kid));
        Assert.Equal([Game], locks.LockedExes(Kid));
    }

    [Fact]
    public void Truly_suspended_process_seen_suspended_does_not_count()
    {
        var game = P(10);
        var locks = Watching(game);

        for (var i = 1; i <= 10; i++)
            Assert.Empty(locks.Check([], [Seen(game, SuspendedCpu)], T0.AddMinutes(i), Today, NoBonus).Counted);
        Assert.Equal(0, locks.ChildResumes(Kid));
    }

    [Fact]
    public void Small_growth_while_suspended_moves_the_baseline_and_never_adds_up_to_a_count()
    {
        // One clock tick (15.625 ms) per check, for 10 checks: 156 ms in total, but never 50 ms since the last baseline.
        var game = P(10);
        var locks = Watching(game);
        var cpu = SuspendedCpu;

        for (var i = 1; i <= 10; i++)
        {
            cpu += TimeSpan.FromMilliseconds(15.625);
            Assert.Empty(locks.Check([], [Seen(game, cpu)], T0.AddMinutes(i), Today, NoBonus).Counted);
        }
    }

    [Fact]
    public void Unwatched_process_seen_suspended_is_watched_without_a_count()
    {
        var game = P(10);
        var locks = new ResumeLocks();

        var check = locks.Check([], [Seen(game, PlayedCpu)], T0, Today, NoBonus);

        Assert.Empty(check.Counted);
        Assert.True(locks.IsWatched(game.Pid, game.StartTimeUtc));
        // The next check sees no growth since then.
        Assert.Empty(locks.Check([], [Seen(game, PlayedCpu)], T0.AddMinutes(1), Today, NoBonus).Counted);
    }

    [Fact]
    public void Failed_read_of_a_process_seen_suspended_does_not_count_and_keeps_the_baseline()
    {
        var game = P(10);
        var locks = Watching(game);

        Assert.Empty(locks.Check([], [Seen(game, null)], T0, Today, NoBonus).Counted);
        Assert.Equal([Kid], locks.Check([], [Seen(game, PlayedCpu)], T0.AddMinutes(1), Today, NoBonus).Counted);
    }

    [Fact]
    public void Running_and_seen_suspended_processes_of_one_user_count_once_in_one_check()
    {
        var game = P(10);
        var video = P(11, Video);
        var locks = Watching(game, video);

        var check = locks.Check([game], [Seen(video, PlayedCpu)], T0, Today, NoBonus);

        Assert.Equal([Kid], check.Counted);
        Assert.Equal(1, locks.ChildResumes(Kid));
    }

    [Fact]
    public void The_30_second_gap_applies_across_both_paths_and_keeps_the_evidence()
    {
        var game = P(10);
        var video = P(11, Video);
        var locks = Watching(game, video);

        Assert.Equal([Kid], locks.Check([game], T0, Today, NoBonus).Counted);
        // 15 s later the video shows evidence while suspended: no count, but its old baseline stays.
        Assert.Empty(locks.Check([], [Seen(video, PlayedCpu)], T0.AddSeconds(15), Today, NoBonus).Counted);
        // At the next regular check the same evidence counts.
        Assert.Equal([Kid], locks.Check([], [Seen(video, PlayedCpu)], T0.AddSeconds(60), Today, NoBonus).Counted);
        Assert.Equal(2, locks.ChildResumes(Kid));
    }

    [Fact]
    public void Process_resumed_by_the_service_is_not_counted_when_seen_suspended_later()
    {
        // A hold: the service resumes the game for the end of the session; after it, the service suspends it again.
        var game = P(10);
        var locks = Watching(game);
        locks.MarkResumedByService(game.Pid, game.StartTimeUtc);

        Assert.Empty(locks.Check([], [Seen(game, PlayedCpu)], T0, Today, NoBonus).Counted);
    }

    // --- Release clears the gap (review of PR #62) ---

    [Fact]
    public void Release_clears_the_30_second_gap()
    {
        var game = P(10);
        var locks = Watching(game);
        for (var i = 0; i < 3; i++)
            ResumeAgain(locks, game, T0.AddMinutes(i));
        Assert.True(locks.IsLocked(Kid));

        locks.Release(Today, _ => 15, AllDue);
        var check = ResumeAgain(locks, game, T0.AddMinutes(2).AddSeconds(10), _ => 15);

        Assert.Equal([Kid], check.Counted);
        Assert.Equal(1, locks.ChildResumes(Kid));
    }

    // --- The stored Resume Lock (design D6) ---

    [Fact]
    public void LockState_gives_the_stored_form_of_a_running_lock()
    {
        var game = P(10);
        var locks = Watching(game);
        for (var i = 0; i < 3; i++)
            ResumeAgain(locks, game, T0.AddMinutes(i), _ => 5);

        Assert.Equal([Game], locks.LockState(Kid)!.ExePaths);
        Assert.Equal(Today, locks.LockState(Kid)!.Day);
        Assert.Equal(5, locks.LockState(Kid)!.BonusAtStart);
        Assert.Null(locks.LockState(OtherKid));
    }

    [Fact]
    public void Restored_lock_runs_without_a_start_and_keeps_its_exes_due()
    {
        var locks = new ResumeLocks();

        Assert.True(locks.Restore(new ResumeLockState(Kid, Today, 0, [Game])));

        Assert.True(locks.IsLocked(Kid));
        Assert.Empty(locks.Release(Today, NoBonus, AllDue));
        Assert.True(locks.IsLocked(Kid));
    }

    [Fact]
    public void Restored_lock_ends_on_a_new_day()
    {
        var locks = new ResumeLocks();
        locks.Restore(new ResumeLockState(Kid, Today.AddDays(-1), 0, [Game]));

        Assert.Equal([new ResumeLockEnd(Kid, ResumeLockEndReason.NewDay)], locks.Release(Today, NoBonus, AllDue));
        Assert.Null(locks.LockState(Kid));
    }

    [Fact]
    public void Restored_lock_ends_after_new_Bonus_Minutes()
    {
        var locks = new ResumeLocks();
        locks.Restore(new ResumeLockState(Kid, Today, 5, [Game]));

        Assert.Empty(locks.Release(Today, _ => 5, AllDue));
        Assert.Equal([new ResumeLockEnd(Kid, ResumeLockEndReason.BonusMinutes)], locks.Release(Today, _ => 20, AllDue));
    }

    [Fact]
    public void Restored_lock_ends_when_no_exe_is_still_due()
    {
        var locks = new ResumeLocks();
        locks.Restore(new ResumeLockState(Kid, Today, 0, [Game]));

        Assert.Equal([new ResumeLockEnd(Kid, ResumeLockEndReason.NoReasonLeft)], locks.Release(Today, NoBonus, (_, _) => false));
    }

    [Fact]
    public void Restore_keeps_a_running_lock_and_refuses_a_lock_without_exes()
    {
        var locks = new ResumeLocks();
        Assert.True(locks.Restore(new ResumeLockState(Kid, Today, 0, [Game])));

        Assert.False(locks.Restore(new ResumeLockState(Kid, Today, 9, [Video])));
        Assert.False(locks.Restore(new ResumeLockState(OtherKid, Today, 0, [])));
        Assert.Equal([Game], locks.LockedExes(Kid));
        Assert.Equal(0, locks.LockState(Kid)!.BonusAtStart);
        Assert.False(locks.IsLocked(OtherKid));
    }

    [Fact]
    public void Child_Resume_during_a_restored_lock_adds_its_exe_and_reports_it()
    {
        var video = P(11, Video);
        var game = P(10);
        var locks = Watching(video, game);
        locks.Restore(new ResumeLockState(Kid, Today, 0, [Game]));

        var check = locks.Check([video], T0, Today, NoBonus);

        Assert.Empty(check.Started);
        Assert.Equal([Kid], check.Extended);
        Assert.Equal([Game, Video], locks.LockedExes(Kid));
        // The same exe again adds nothing new.
        Assert.Empty(locks.Check([game], T0.AddMinutes(1), Today, NoBonus).Extended);
    }
}
