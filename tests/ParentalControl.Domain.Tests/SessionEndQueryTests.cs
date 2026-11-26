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
}
