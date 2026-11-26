using System.Net;

namespace ParentalControl.Domain.AdminAccess;

/// <summary>
/// The source of a login for the login limit. All addresses of this PC are one source, <see cref="Local"/>.
/// The source comes from the TCP connection only, never from a header such as <c>X-Forwarded-For</c>.
/// </summary>
public static class LoginSource
{
    public const string Local = "local";

    /// <summary>The source of a connection without a remote address.</summary>
    public const string Unknown = "unknown";

    /// <summary>
    /// An IPv4-mapped IPv6 address becomes IPv4, and the IPv6 scope ID is ignored.
    /// A loopback address and each address in <paramref name="localAddresses"/> give <see cref="Local"/>.
    /// </summary>
    public static string Of(IPAddress? remote, IEnumerable<IPAddress> localAddresses)
    {
        if (remote is null)
            return Unknown;

        var address = Canonical(remote);
        if (IPAddress.IsLoopback(address))
            return Local;

        foreach (var local in localAddresses)
        {
            if (Canonical(local).Equals(address))
                return Local;
        }
        return address.ToString();
    }

    /// <summary>The address without an IPv6 scope ID, and as IPv4 if it is IPv4-mapped.</summary>
    public static IPAddress Canonical(IPAddress address)
    {
        if (address.IsIPv4MappedToIPv6)
            return address.MapToIPv4();
        if (address.AddressFamily == System.Net.Sockets.AddressFamily.InterNetworkV6 && address.ScopeId != 0)
            return new IPAddress(address.GetAddressBytes());
        return address;
    }
}
