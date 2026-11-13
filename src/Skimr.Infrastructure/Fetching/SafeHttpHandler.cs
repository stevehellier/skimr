using System.Net;
using System.Net.Sockets;

namespace Skimr.Infrastructure.Fetching;

internal sealed class SsrfBlockedException(string host, IPAddress address)
    : Exception($"{host} resolves to {address}, which is a blocked address.")
{
    public IPAddress Address { get; } = address;
}

/// <summary>
/// Builds the only handler used for outbound requests. The IP check runs in ConnectCallback,
/// on the exact addresses the socket connects to, for every connection including each
/// redirect hop. That closes the DNS-rebinding gap a check-then-connect guard leaves (PLAN D6).
/// </summary>
internal static class SafeHttpHandler
{
    internal delegate Task<IPAddress[]> Resolver(string host, CancellationToken cancellationToken);

    public static SocketsHttpHandler Create(FetchOptions options) => Create(options, null, null);

    /// <param name="resolve">Test seam: replaces DNS.</param>
    /// <param name="isAllowed">Test seam: replaces the IP policy.</param>
    internal static SocketsHttpHandler Create(FetchOptions options, Resolver? resolve, Func<IPAddress, bool>? isAllowed)
    {
        resolve ??= Dns.GetHostAddressesAsync;
        isAllowed ??= ip => IpPolicy.IsAllowed(ip, options.AllowPrivateNetworks);

        return new SocketsHttpHandler
        {
            AllowAutoRedirect = options.MaxRedirects > 0,
            MaxAutomaticRedirections = Math.Max(1, options.MaxRedirects),
            AutomaticDecompression = DecompressionMethods.All,
            // A proxy would make ConnectCallback check the proxy, not the target.
            UseProxy = false,
            UseCookies = false,
            ConnectTimeout = TimeSpan.FromSeconds(10),
            PooledConnectionLifetime = TimeSpan.FromMinutes(5),
            ConnectCallback = async (context, cancellationToken) =>
            {
                var host = context.DnsEndPoint.Host;
                var addresses = IPAddress.TryParse(host, out var literal)
                    ? [literal]
                    : await resolve(host, cancellationToken);

                if (addresses.Length == 0)
                {
                    throw new HttpRequestException($"DNS lookup for {host} returned no addresses.");
                }

                // Any blocked answer blocks the host: a mixed answer is how rebinding attacks start.
                if (addresses.FirstOrDefault(a => !isAllowed(a)) is { } blocked)
                {
                    throw new SsrfBlockedException(host, blocked);
                }

                var socket = new Socket(SocketType.Stream, ProtocolType.Tcp) { NoDelay = true };
                try
                {
                    await socket.ConnectAsync(addresses, context.DnsEndPoint.Port, cancellationToken);
                    return new NetworkStream(socket, ownsSocket: true);
                }
                catch
                {
                    socket.Dispose();
                    throw;
                }
            },
        };
    }
}
