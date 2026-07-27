using System.Collections.Concurrent;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Text;
using WingSync.Core.Domain;

namespace WingSync.Infrastructure.Discovery;

/// <summary>
/// Discovers every Behringer WING that replies on any active IPv4 network adapter.
/// </summary>
public sealed class WingDiscoveryService
{
    private static readonly byte[] DiscoveryProbe = Encoding.ASCII.GetBytes("WING?");
    private readonly TimeProvider _timeProvider;

    /// <summary>
    /// Initializes a new instance of the <see cref="WingDiscoveryService"/> class.
    /// </summary>
    /// <param name="timeProvider">
    /// The clock used to timestamp announcements, or <see cref="TimeProvider.System"/> when omitted.
    /// </param>
    public WingDiscoveryService(TimeProvider? timeProvider = null)
    {
        _timeProvider = timeProvider ?? TimeProvider.System;
    }

    /// <summary>
    /// Sends the exact five-byte <c>WING?</c> probe on every eligible adapter and collects
    /// all valid replies for the complete configured timeout.
    /// </summary>
    /// <param name="options">Discovery options, or defaults when omitted.</param>
    /// <param name="cancellationToken">Cancels socket activity and aborts the discovery pass.</param>
    /// <returns>All unique serial/IP announcements plus non-fatal adapter issues.</returns>
    /// <exception cref="OperationCanceledException">
    /// Thrown when <paramref name="cancellationToken"/> is cancelled.
    /// </exception>
    public async Task<WingDiscoveryResult> DiscoverAsync(
        WingDiscoveryOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        options ??= new WingDiscoveryOptions();
        options.Validate();
        cancellationToken.ThrowIfCancellationRequested();

        var issues = new ConcurrentQueue<WingDiscoveryIssue>();
        var discovered = new ConcurrentDictionary<string, DiscoveredWing>(
            StringComparer.OrdinalIgnoreCase);

        var bindings = GetBindings(options, issues);
        if (bindings.Count == 0)
        {
            return new WingDiscoveryResult([], issues.ToArray());
        }

        using var timeoutSource =
            CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutSource.CancelAfter(options.ReplyTimeout);

        // Probe adapters concurrently and keep listening for the complete reply
        // window; consoles may answer at different speeds on different subnets.
        var adapterTasks = bindings.Select(
            binding => QueryBindingAsync(
                binding,
                options.Port,
                discovered,
                issues,
                timeoutSource.Token));

        await Task.WhenAll(adapterTasks).ConfigureAwait(false);
        cancellationToken.ThrowIfCancellationRequested();

        var wings = discovered.Values
            .OrderBy(static wing => ParseAddressForOrdering(wing.IpAddress))
            .ThenBy(static wing => wing.SerialNumber, StringComparer.OrdinalIgnoreCase)
            .ToArray();

        return new WingDiscoveryResult(wings, issues.ToArray());
    }

    private static List<DiscoveryBinding> GetBindings(
        WingDiscoveryOptions options,
        ConcurrentQueue<WingDiscoveryIssue> issues)
    {
        var bindings = new List<DiscoveryBinding>();

        try
        {
            foreach (var networkInterface in NetworkInterface.GetAllNetworkInterfaces())
            {
                if (networkInterface.OperationalStatus != OperationalStatus.Up
                    || networkInterface.NetworkInterfaceType is NetworkInterfaceType.Loopback
                        or NetworkInterfaceType.Tunnel)
                {
                    continue;
                }

                foreach (var unicast in networkInterface.GetIPProperties().UnicastAddresses)
                {
                    if (unicast.Address.AddressFamily != AddressFamily.InterNetwork)
                    {
                        continue;
                    }

                    var targets = new HashSet<IPAddress>();

                    // Directed broadcast reaches the adapter subnet reliably;
                    // the global address is an optional fallback and is deduplicated.
                    if (options.IncludeDirectedBroadcast
                        && TryCalculateDirectedBroadcast(
                            unicast.Address,
                            unicast.IPv4Mask,
                            out var directedBroadcast))
                    {
                        targets.Add(directedBroadcast);
                    }

                    if (options.IncludeGlobalBroadcast)
                    {
                        targets.Add(IPAddress.Broadcast);
                    }

                    if (targets.Count > 0)
                    {
                        bindings.Add(
                            new DiscoveryBinding(
                                networkInterface.Name,
                                unicast.Address,
                                targets.ToArray()));
                    }
                }
            }
        }
        catch (NetworkInformationException exception)
        {
            issues.Enqueue(
                new WingDiscoveryIssue(
                    string.Empty,
                    string.Empty,
                    WingDiscoveryOperation.EnumerateAdapters,
                    exception.Message));
        }

        return bindings;
    }

    private async Task QueryBindingAsync(
        DiscoveryBinding binding,
        int port,
        ConcurrentDictionary<string, DiscoveredWing> discovered,
        ConcurrentQueue<WingDiscoveryIssue> issues,
        CancellationToken cancellationToken)
    {
        using var client = new UdpClient(AddressFamily.InterNetwork);

        try
        {
            client.EnableBroadcast = true;
            client.Client.Bind(new IPEndPoint(binding.LocalAddress, 0));
        }
        catch (SocketException exception)
        {
            issues.Enqueue(CreateIssue(binding, WingDiscoveryOperation.OpenSocket, exception));
            return;
        }

        var receiveTask = ReceiveRepliesAsync(
            client,
            binding,
            discovered,
            issues,
            cancellationToken);

        // Start receiving before sending so a fast local reply cannot arrive
        // between probe transmission and listener setup.
        foreach (var target in binding.Targets)
        {
            try
            {
                await client.SendAsync(
                        DiscoveryProbe.AsMemory(),
                        new IPEndPoint(target, port),
                        cancellationToken)
                    .ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                break;
            }
            catch (SocketException exception)
            {
                issues.Enqueue(CreateIssue(binding, WingDiscoveryOperation.SendProbe, exception));
            }
        }

        await receiveTask.ConfigureAwait(false);
    }

    private async Task ReceiveRepliesAsync(
        UdpClient client,
        DiscoveryBinding binding,
        ConcurrentDictionary<string, DiscoveredWing> discovered,
        ConcurrentQueue<WingDiscoveryIssue> issues,
        CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            UdpReceiveResult received;

            try
            {
                received = await client.ReceiveAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                return;
            }
            catch (SocketException exception)
            {
                issues.Enqueue(CreateIssue(binding, WingDiscoveryOperation.ReceiveReply, exception));
                return;
            }

            var discoveredAt = _timeProvider.GetUtcNow();
            if (!WingAnnouncementParser.TryParse(
                    received.Buffer,
                    received.RemoteEndPoint,
                    discoveredAt,
                    out var wing))
            {
                continue;
            }

            var key = string.Concat(
                wing.SerialNumber,
                "\n",
                wing.IpAddress);

            // A console may answer both directed and global broadcasts. Serial
            // plus address identifies one announcement without hiding duplicate IPs.
            discovered.TryAdd(key, wing);
        }
    }

    private static bool TryCalculateDirectedBroadcast(
        IPAddress address,
        IPAddress? mask,
        out IPAddress broadcast)
    {
        broadcast = IPAddress.None;
        if (mask is null)
        {
            return false;
        }

        var addressBytes = address.GetAddressBytes();
        var maskBytes = mask.GetAddressBytes();
        if (addressBytes.Length != 4 || maskBytes.Length != 4)
        {
            return false;
        }

        var broadcastBytes = new byte[4];
        for (var index = 0; index < broadcastBytes.Length; index++)
        {
            broadcastBytes[index] =
                (byte)(addressBytes[index] | (maskBytes[index] ^ byte.MaxValue));
        }

        broadcast = new IPAddress(broadcastBytes);
        return !broadcast.Equals(address);
    }

    private static WingDiscoveryIssue CreateIssue(
        DiscoveryBinding binding,
        WingDiscoveryOperation operation,
        SocketException exception)
    {
        return new WingDiscoveryIssue(
            binding.AdapterName,
            binding.LocalAddress.ToString(),
            operation,
            $"{exception.SocketErrorCode}: {exception.Message}");
    }

    private static uint ParseAddressForOrdering(string address)
    {
        var bytes = IPAddress.Parse(address).GetAddressBytes();
        return ((uint)bytes[0] << 24)
               | ((uint)bytes[1] << 16)
               | ((uint)bytes[2] << 8)
               | bytes[3];
    }

    private sealed record DiscoveryBinding(
        string AdapterName,
        IPAddress LocalAddress,
        IReadOnlyList<IPAddress> Targets);
}
