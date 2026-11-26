using System.Text;
using ParentalControl.Domain.AdminAccess;

namespace ParentalControl.Domain.Tests.AdminAccess;

public class TotpTests
{
    // RFC 6238 appendix B, SHA-1: the key is the ASCII text "12345678901234567890".
    private static readonly byte[] RfcKey = Encoding.ASCII.GetBytes("12345678901234567890");

    [Theory]
    [InlineData(59L, "287082")]
    [InlineData(1111111109L, "081804")]
    [InlineData(1111111111L, "050471")]
    [InlineData(1234567890L, "005924")]
    [InlineData(2000000000L, "279037")]
    [InlineData(20000000000L, "353130")]
    public void Codes_match_the_RFC_6238_test_vectors(long unixSeconds, string expected)
    {
        var step = Totp.StepAt(DateTimeOffset.FromUnixTimeSeconds(unixSeconds));
        Assert.Equal(expected, Totp.ComputeCode(RfcKey, step));
    }

    [Fact]
    public void Rfc_vector_verifies_at_its_time()
    {
        var now = DateTimeOffset.FromUnixTimeSeconds(59);
        Assert.True(Totp.TryVerify(RfcKey, "287082", now, Totp.NoStepUsed, out var step));
        Assert.Equal(1, step);
    }

    [Fact]
    public void Base32_secret_gives_the_same_codes_as_its_key()
    {
        var secret = TotpSecret.Encode(RfcKey);
        var now = DateTimeOffset.FromUnixTimeSeconds(59);
        Assert.True(Totp.TryVerify(secret, "287082", now, Totp.NoStepUsed, out _));
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(0)]
    [InlineData(1)]
    public void Codes_of_the_steps_minus_1_to_plus_1_are_accepted(int drift)
    {
        var now = DateTimeOffset.FromUnixTimeSeconds(1_700_000_000);
        var step = Totp.StepAt(now) + drift;
        var code = Totp.ComputeCode(RfcKey, step);
        Assert.True(Totp.TryVerify(RfcKey, code, now, Totp.NoStepUsed, out var matched));
        Assert.Equal(step, matched);
    }

    [Theory]
    [InlineData(-2)]
    [InlineData(2)]
    public void Codes_of_the_steps_minus_2_and_plus_2_are_refused(int drift)
    {
        var now = DateTimeOffset.FromUnixTimeSeconds(1_700_000_000);
        var code = Totp.ComputeCode(RfcKey, Totp.StepAt(now) + drift);
        Assert.False(Totp.TryVerify(RfcKey, code, now, Totp.NoStepUsed, out _));
    }

    [Fact]
    public void Code_of_the_last_used_step_is_refused()
    {
        var now = DateTimeOffset.FromUnixTimeSeconds(1_700_000_000);
        var step = Totp.StepAt(now);
        var code = Totp.ComputeCode(RfcKey, step);
        Assert.False(Totp.TryVerify(RfcKey, code, now, step, out _));
        Assert.False(Totp.TryVerify(RfcKey, Totp.ComputeCode(RfcKey, step - 1), now, step, out _));
        Assert.True(Totp.TryVerify(RfcKey, Totp.ComputeCode(RfcKey, step + 1), now, step, out var next));
        Assert.Equal(step + 1, next);
    }

    [Fact]
    public void Same_code_works_one_time_only_when_the_caller_saves_the_step()
    {
        var secret = TotpSecret.Generate();
        Assert.True(TotpSecret.TryDecode(secret, out var key));
        var now = DateTimeOffset.UtcNow;
        var code = Totp.ComputeCode(key, Totp.StepAt(now));
        Assert.True(Totp.TryVerify(secret, code, now, Totp.NoStepUsed, out var used));
        Assert.False(Totp.TryVerify(secret, code, now, used, out _));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("TOOSHORT")]
    public void Secret_that_is_not_valid_refuses_each_code(string? secret)
    {
        var now = DateTimeOffset.FromUnixTimeSeconds(59);
        Assert.False(Totp.TryVerify(secret, "287082", now, Totp.NoStepUsed, out _));
        Assert.False(Totp.TryVerify(secret, "000000", now, Totp.NoStepUsed, out _));
    }

    [Fact]
    public void Empty_key_refuses_each_code()
    {
        Assert.False(Totp.TryVerify(Array.Empty<byte>(), "000000", DateTimeOffset.UtcNow, Totp.NoStepUsed, out _));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("28708")]
    [InlineData("2870821")]
    [InlineData("28708a")]
    [InlineData("+87082")]
    [InlineData("２８７０８２")]
    public void Code_that_is_not_six_digits_is_refused(string? code)
    {
        var now = DateTimeOffset.FromUnixTimeSeconds(59);
        Assert.False(Totp.TryVerify(RfcKey, code, now, Totp.NoStepUsed, out _));
    }

    [Fact]
    public void Spaces_around_the_code_are_ignored()
    {
        var now = DateTimeOffset.FromUnixTimeSeconds(59);
        Assert.True(Totp.TryVerify(RfcKey, " 287082 ", now, Totp.NoStepUsed, out _));
    }
}
