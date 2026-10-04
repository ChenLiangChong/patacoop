using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Threading.Tasks;
using LiteNetLib;
using PataCoop.Server;

namespace PataCoop.Net;

/// <summary>
/// Finds rooms without anyone typing an address: asks every network this PC is on (Radmin VPN,
/// LAN) by broadcast, plus the hosts we already know, and lists the rooms that answer. It only
/// runs while the panel is open and we are not in a room.
/// </summary>
public static class RoomFinder
{
    public sealed class Room
    {
        public string Address = "", Code = "", Host = "", Version = "";
        public int Players, Capacity;
        public bool Open, SameProtocol;
        internal long Seen;
    }

    private const int AskEveryMs = 1500, ForgetAfterMs = 5000, RetryOpenMs = 10000;
    private static NetManager? _net;
    private static readonly Listener Events = new();
    private static readonly List<Room> Found = new();
    private static long _nextAsk, _retryOpen;
    private static bool _warned;

    public static IReadOnlyList<Room> Rooms => Found;

    /// <summary>Called every frame: search while <paramref name="wanted"/>, otherwise stay closed.</summary>
    public static void Tick(bool wanted)
    {
        if (!wanted)
        {
            Stop();
            return;
        }
        if (_net == null && (Environment.TickCount64 < _retryOpen || !Start())) return;
        _net!.PollEvents();
        long now = Environment.TickCount64;
        Found.RemoveAll(r => now - r.Seen > ForgetAfterMs);
        if (now < _nextAsk) return;
        _nextAsk = now + AskEveryMs;
        Ask();
    }

    private static bool Start()
    {
        var net = new NetManager(Events) { UnconnectedMessagesEnabled = true, IPv6Enabled = false, AutoRecycle = true };
        if (!net.Start())
        {
            if (!_warned) CoopPlugin.L.LogWarning("room search: cannot open a UDP socket; trying again in a while");
            _warned = true;
            _retryOpen = Environment.TickCount64 + RetryOpenMs;
            return false;
        }
        _net = net;
        _nextAsk = 0;
        return true;
    }

    public static void Stop()
    {
        _net?.Stop();
        _net = null;
        Found.Clear();
        _nextAsk = 0;
    }

    private static void Ask()
    {
        var ask = new PacketWriter(Op.Find).U8(RelayServer.ProtocolVersion).ToArray();
        Array.Resize(ref ask, RelayServer.FindSize);
        int port = CoopPlugin.Port.Value;
        _net!.SendBroadcast(ask, port);
        foreach (var address in Networks.Broadcasts) _net.SendUnconnectedMessage(ask, new IPEndPoint(address, port));
        // the host we joined last time (or the one the installer was given), and this PC itself
        if (IPAddress.TryParse(CoopPlugin.HostAddress.Value.Trim(), out var known) && known.AddressFamily == AddressFamily.InterNetwork)
            _net.SendUnconnectedMessage(ask, new IPEndPoint(known, port));
        _net.SendUnconnectedMessage(ask, new IPEndPoint(IPAddress.Loopback, port));
    }

    private sealed class Listener : INetEventListener
    {
        public void OnNetworkReceiveUnconnected(IPEndPoint remoteEndPoint, NetPacketReader reader, UnconnectedMessageType messageType)
        {
            try
            {
                var r = new PacketReader(reader.GetRemainingBytes());
                if (r.Op != Op.Found) return;
                byte protocol = r.U8();
                string version = r.Str() ?? "";
                int count = r.U8();
                long now = Environment.TickCount64;
                for (int i = 0; i < count; i++)
                {
                    string code = r.Str() ?? "", host = r.Str() ?? "?";
                    int players = r.U8(), capacity = r.U8();
                    bool open = r.Bool();
                    var room = Found.FirstOrDefault(x => string.Equals(x.Code, code, StringComparison.OrdinalIgnoreCase));
                    if (room == null)
                    {
                        // the first address that answered stays (a host on the LAN and on Radmin answers twice)
                        room = new Room { Code = code, Address = remoteEndPoint.Address.ToString() };
                        Found.Add(room);
                        CoopPlugin.L.LogInfo($"[find] room {code} by {host} at {room.Address} ({players}/{capacity}, v{version}, protocol {protocol})");
                    }
                    room.Host = host;
                    room.Version = version;
                    room.Players = players;
                    room.Capacity = capacity;
                    room.Open = open;
                    room.SameProtocol = protocol == RelayServer.ProtocolVersion;
                    room.Seen = now;
                }
            }
            catch (Exception e)
            {
                CoopPlugin.L.LogDebug($"room search: bad answer from {remoteEndPoint}: {e.Message}");
            }
        }

        public void OnPeerConnected(NetPeer peer) { }
        public void OnPeerDisconnected(NetPeer peer, DisconnectInfo info) { }
        public void OnNetworkError(IPEndPoint endPoint, SocketError socketError) { }
        public void OnNetworkReceive(NetPeer peer, NetPacketReader reader, byte channel, DeliveryMethod method) { }
        public void OnNetworkLatencyUpdate(NetPeer peer, int latency) { }
        public void OnConnectionRequest(ConnectionRequest request) => request.Reject();
    }
}

/// <summary>
/// The networks this PC is on, looked up in the background every few seconds: the broadcast
/// address of each (Radmin VPN's is 26.255.255.255) and the address friends should use.
/// </summary>
public static class Networks
{
    private const int RefreshMs = 10000;
    private static long _next;
    private static bool _busy;

    public static IReadOnlyList<IPAddress> Broadcasts { get; private set; } = Array.Empty<IPAddress>();
    /// <summary>This PC's Radmin VPN address, "" without Radmin VPN.</summary>
    public static string Radmin { get; private set; } = "";
    /// <summary>This PC's LAN address, "" when there is none.</summary>
    public static string Lan { get; private set; } = "";

    public static void Tick()
    {
        long now = Environment.TickCount64;
        if (_busy || now < _next) return;
        _busy = true;
        _next = now + RefreshMs;
        Task.Run(Look);
    }

    private static void Look()
    {
        try
        {
            var broadcasts = new List<IPAddress>();
            string radmin = "", lan = "";
            foreach (var nic in NetworkInterface.GetAllNetworkInterfaces())
            {
                if (nic.OperationalStatus != OperationalStatus.Up || nic.NetworkInterfaceType == NetworkInterfaceType.Loopback) continue;
                bool isRadmin = nic.Name.Contains("Radmin", StringComparison.OrdinalIgnoreCase) || nic.Description.Contains("Radmin", StringComparison.OrdinalIgnoreCase);
                foreach (var u in nic.GetIPProperties().UnicastAddresses)
                {
                    if (u.Address.AddressFamily != AddressFamily.InterNetwork || u.IPv4Mask == null) continue;
                    var ip = u.Address.GetAddressBytes();
                    var mask = u.IPv4Mask.GetAddressBytes();
                    if (ip[0] == 169 && ip[1] == 254) continue; // no address from DHCP
                    if (mask.All(b => b == 255)) continue;
                    var broadcast = new byte[4];
                    for (int i = 0; i < 4; i++) broadcast[i] = (byte)(ip[i] | ~mask[i]);
                    broadcasts.Add(new IPAddress(broadcast));
                    if (isRadmin && radmin.Length == 0) radmin = u.Address.ToString();
                    else if (!isRadmin && lan.Length == 0 && nic.GetIPProperties().GatewayAddresses.Count > 0) lan = u.Address.ToString();
                }
            }
            Broadcasts = broadcasts.Distinct().ToList();
            Radmin = radmin;
            Lan = lan;
        }
        catch (Exception e)
        {
            CoopPlugin.L.LogDebug("network lookup failed: " + e.Message);
        }
        finally
        {
            _busy = false;
        }
    }
}
