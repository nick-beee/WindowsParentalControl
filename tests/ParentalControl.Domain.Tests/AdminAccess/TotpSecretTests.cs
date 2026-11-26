using ParentalControl.Domain.AdminAccess;

namespace ParentalControl.Domain.Tests.AdminAccess;

public class TotpSecretTests
{
    [Fact]
    public void Secret_of_20_bytes_is_valid()
    {
        var secret = TotpSecret.Encode(new byte[20]);
        Assert.Equal(32, secret.Length);
        Assert.True(TotpSecret.IsValid(secret));
        Assert.True(TotpSecret.IsValid(TotpSecret.Generate()));
    }

    [Fact]
    public void Secret_of_10_bytes_is_valid()
    {
        var secret = TotpSecret.Encode(Enumerable.Range(1, 10).Select(i => (byte)i).ToArray());
        Assert.Equal(16, secret.Length);
        Assert.True(TotpSecret.TryDecode(secret, out var key));
        Assert.Equal(10, key.Length);
    }

    [Fact]
    public void Secret_of_9_bytes_is_not_valid()
    {
        var secret = TotpSecret.Encode(new byte[9]);
        Assert.False(TotpSecret.IsValid(secret));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("====")]
    public void Empty_secret_is_not_valid(string? secret)
    {
        Assert.False(TotpSecret.IsValid(secret));
    }

    [Theory]
    [InlineData("JBSWY3DPEHPK3PX1")]
    [InlineData("JBSWY3DPEHPK3PX8")]
    [InlineData("JBSWY3DPEHPK3PX!")]
    [InlineData("JBSWY3DPEHPK3PXP0000")]
    public void Secret_with_a_character_outside_Base32_is_not_valid(string secret)
    {
        Assert.False(TotpSecret.IsValid(secret));
    }

    [Fact]
    public void Lower_case_spaces_and_padding_are_accepted_as_the_same_secret()
    {
        var secret = TotpSecret.Encode(Enumerable.Range(1, 20).Select(i => (byte)i).ToArray());
        var spaced = string.Join(' ', secret.ToLowerInvariant().Chunk(4).Select(c => new string(c))) + "====";
        Assert.True(TotpSecret.TryDecode(secret, out var a));
        Assert.True(TotpSecret.TryDecode(spaced, out var b));
        Assert.Equal(a, b);
    }

    [Fact]
    public void Encode_and_decode_give_the_same_bytes()
    {
        var data = Enumerable.Range(0, 20).Select(i => (byte)(i * 13)).ToArray();
        Assert.True(TotpSecret.TryDecode(TotpSecret.Encode(data), out var key));
        Assert.Equal(data, key);
    }
}
