using ParentalControl.Domain.Sessions;

namespace ParentalControl.Domain.Tests;

public class SessionEndQueryTests
{
    [Theory]
    [InlineData(0x0L, SessionEndKind.Shutdown)]                 // shutdown
    [InlineData(0x80000000L, SessionEndKind.Logoff)]            // sign-out (VM result R1)
    [InlineData(0x40000000L, SessionEndKind.Shutdown)]          // restart with ENDSESSION_CRITICAL (VM result R4)
    [InlineData(0xC0000000L, SessionEndKind.Logoff)]            // critical sign-out
    [InlineData(0x1L, SessionEndKind.None)]                     // Restart Manager closes only the agent
    [InlineData(0x80000001L, SessionEndKind.None)]
    [InlineData(0x40000001L, SessionEndKind.None)]
    public void Classifies_the_lParam_of_the_query(long lParam, SessionEndKind expected) =>
        Assert.Equal(expected, SessionEndQuery.Classify(lParam));

    [Theory]
    [InlineData(null, 100_000L, false)]      // no query yet: wait for it
    [InlineData(99_990L, 100_000L, true)]    // the hidden window got the query just before WPF
    [InlineData(100_010L, 100_000L, true)]   // the hidden window got the query after WPF
    [InlineData(95_000L, 100_000L, true)]    // at the limit of the window
    [InlineData(94_999L, 100_000L, false)]   // an older query (CLOSEAPP or a fake query) does not count
    public void Counts_only_a_query_of_this_end_of_the_session(long? lastQueryMs, long sessionEndingMs, bool expected) =>
        Assert.Equal(expected, SessionEndQuery.IsQueryOfThisEnd(lastQueryMs, sessionEndingMs));
}
