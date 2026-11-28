using System.Text.Json;
using ParentalControl.Domain.Messages;
using ParentalControl.Domain.Sessions;

namespace ParentalControl.Domain.Tests.Sessions;

public class LockReasonsTests
{
    [Fact]
    public void Break_ended_at_the_Schedule_Window_end_gives_the_Schedule_Window()
    {
        // Issue #44: the Break started at 19:55 and ended at 20:00 with the Schedule Window. The session stays locked.
        Assert.Equal(LockReason.ScheduleWindow,
            LockReasons.Resolve(isLocked: true, outsideScheduleWindow: true, allowanceUsedUp: false, breakRunning: false));
    }

    [Fact]
    public void Running_Break_with_time_left_gives_the_Break()
    {
        Assert.Equal(LockReason.Break,
            LockReasons.Resolve(isLocked: true, outsideScheduleWindow: false, allowanceUsedUp: false, breakRunning: true));
    }

    [Fact]
    public void Used_up_Allowance_Time_during_a_Break_gives_the_Allowance_Time()
    {
        // The parent lowers the daily limit during a Break: the lock stays after the Break, for the Allowance Time.
        Assert.Equal(LockReason.Allowance,
            LockReasons.Resolve(isLocked: true, outsideScheduleWindow: false, allowanceUsedUp: true, breakRunning: true));
    }

    [Fact]
    public void Break_that_ends_while_the_Allowance_Time_locks_gives_the_Allowance_Time()
    {
        Assert.Equal(LockReason.Allowance,
            LockReasons.Resolve(isLocked: true, outsideScheduleWindow: false, allowanceUsedUp: true, breakRunning: false));
    }

    [Fact]
    public void Schedule_Window_comes_before_the_Allowance_Time_and_the_Break()
    {
        Assert.Equal(LockReason.ScheduleWindow,
            LockReasons.Resolve(isLocked: true, outsideScheduleWindow: true, allowanceUsedUp: true, breakRunning: true));
    }

    [Fact]
    public void Other_lock_without_a_limit_or_a_Break_gives_Other()
    {
        // The child locked the session, or a Category Limit, a Probe Lock or a Storage Lockdown locked it.
        Assert.Equal(LockReason.Other,
            LockReasons.Resolve(isLocked: true, outsideScheduleWindow: false, allowanceUsedUp: false, breakRunning: false));
    }

    [Theory]
    [InlineData(true, true, true)]
    [InlineData(false, false, false)]
    [InlineData(true, false, true)]
    public void Unlocked_session_has_no_reason(bool outside, bool usedUp, bool inBreak)
    {
        Assert.Equal(LockReason.None, LockReasons.Resolve(false, outside, usedUp, inBreak));
    }

    [Theory]
    [InlineData(LockReason.ScheduleWindow)]
    [InlineData(LockReason.Allowance)]
    [InlineData(LockReason.Break)]
    [InlineData(LockReason.Other)]
    public void Wire_name_round_trips(LockReason reason)
    {
        Assert.Equal(reason, LockReasons.FromWire(true, LockReasons.ToWire(reason)));
    }

    [Fact]
    public void Wire_name_of_None_is_null()
    {
        Assert.Null(LockReasons.ToWire(LockReason.None));
        Assert.Equal(LockReason.None, LockReasons.FromWire(false, "break"));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("curfew")]
    public void Locked_session_with_a_missing_or_unknown_reason_gives_Other(string? wire)
    {
        // An older service sends no lockReason. The agent then shows the text without "Pause".
        Assert.Equal(LockReason.Other, LockReasons.FromWire(true, wire));
    }

    [Fact]
    public void Status_answer_carries_the_reason_under_its_wire_name()
    {
        var status = new AgentStatus("Kid", true, 0, "08:00 – 20:00", true, false, null, null, null, false, 0, "de", null,
            [], 0, [], 0, LockReasons.ToWire(LockReason.ScheduleWindow));
        var json = JsonSerializer.Serialize(status);
        using var doc = JsonDocument.Parse(json);
        Assert.Equal("schedule", doc.RootElement.GetProperty("lockReason").GetString());
    }

    [Theory]
    [InlineData(LockReason.ScheduleWindow, "🌙 Gesperrt (keine Computerzeit)", "Parental Control: Gesperrt (keine Computerzeit)")]
    [InlineData(LockReason.Allowance, "⏸ Gesperrt (Zeit für heute um)", "Parental Control: Gesperrt (Zeit für heute um)")]
    [InlineData(LockReason.Break, "⏸ Gesperrt (Pause)", "Parental Control: Gesperrt (Pause)")]
    [InlineData(LockReason.Other, "⏸ Gesperrt (Zeit läuft nicht)", "Parental Control: Gesperrt (Zeit läuft nicht)")]
    public void German_texts_per_reason(LockReason reason, string widget, string tray)
    {
        var de = AgentTexts.For("de");
        Assert.Equal(widget, de[LockReasons.WidgetTextKey(reason)]);
        Assert.Equal(tray, de[LockReasons.TrayTextKey(reason)]);
    }

    [Fact]
    public void Only_the_Break_text_says_Pause()
    {
        var de = AgentTexts.For("de");
        foreach (var reason in new[] { LockReason.ScheduleWindow, LockReason.Allowance, LockReason.Other })
        {
            Assert.DoesNotContain("Pause", de[LockReasons.WidgetTextKey(reason)]);
            Assert.DoesNotContain("Pause", de[LockReasons.TrayTextKey(reason)]);
        }
    }

    [Fact]
    public void Each_preset_has_a_text_for_each_reason()
    {
        foreach (var preset in LanguagePreset.All)
        {
            var set = AgentTexts.For(preset);
            foreach (var reason in Enum.GetValues<LockReason>().Where(r => r != LockReason.None))
            {
                Assert.False(string.IsNullOrWhiteSpace(set[LockReasons.WidgetTextKey(reason)]), $"{preset}/{reason}");
                Assert.False(string.IsNullOrWhiteSpace(set[LockReasons.TrayTextKey(reason)]), $"{preset}/{reason}");
            }
        }
    }
}
