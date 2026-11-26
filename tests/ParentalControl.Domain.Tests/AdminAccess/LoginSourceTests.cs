using System.Net;
using ParentalControl.Domain.AdminAccess;

namespace ParentalControl.Domain.Tests.AdminAccess;

public class LoginSourceTests
{
    private static readonly IPAddress[] ThisPc =
    {
        IPAddress.Parse("192.168.1.63"),
        IPAddress.Parse("fe80::1234:5678:9abc:def0%7"),
    };

    [Theory]
    [InlineData("127.0.0.1")]
    [InlineData("127.0.0.2")]
    [InlineData("::1")]
    [InlineData("::ffff:127.0.0.1")]
    [InlineData("192.168.1.63")]
    [InlineData("::ffff:192.168.1.63")]
    [InlineData("fe80::1234:5678:9abc:def0%12")]
    [InlineData("fe80::1234:5678:9abc:def0")]
    public void Addresses_of_this_PC_are_local(string address)
    {
        Assert.Equal(LoginSource.Local, LoginSource.Of(IPAddress.Parse(address), ThisPc));
    }

    [Fact]
    public void Lan_address_gives_itself()
    {
        Assert.Equal("192.168.1.50", LoginSource.Of(IPAddress.Parse("192.168.1.50"), ThisPc));
        Assert.Equal("192.168.1.50", LoginSource.Of(IPAddress.Parse("::ffff:192.168.1.50"), ThisPc));
    }

    [Fact]
    public void Link_local_address_with_two_zones_is_one_source()
    {
        var a = LoginSource.Of(IPAddress.Parse("fe80::99%3"), ThisPc);
        var b = LoginSource.Of(IPAddress.Parse("fe80::99%4"), ThisPc);
        Assert.Equal(a, b);
        Assert.Equal("fe80::99", a);
    }

    [Fact]
    public void Missing_address_is_unknown_not_local()
    {
        Assert.Equal(LoginSource.Unknown, LoginSource.Of(null, ThisPc));
    }
}
