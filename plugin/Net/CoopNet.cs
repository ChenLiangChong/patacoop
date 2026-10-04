using System;
using System.Net;
using System.Net.Sockets;
using LiteNetLib;
using LiteNetLib.Utils;
using PataCoop.Server;

namespace PataCoop.Net;

public enum NetState { Offline, Connecting, Connected, InRoom }

/// <summary>
/// Connection to the relay server. The host also runs the server inside its own game
/// (<see cref="HostGame"/>); everyone, the host included, talks to it as a client.
/// All events are raised from <see cref="Poll"/>, i.e. on the Unity main thread.
/// </summary>
public static class CoopNet
{
    private static NetManager? _net;
    private static NetPeer? _peer;
    private static RelayServer? _server;
    private static readonly Listener Events = new();
    private static bool _createRoom;
    private static string _joinCode = "";
    private static string _name = "";

    public static NetState State { get; private set; }
    public static bool IsHost { get; private set; }
    public static ulong MyId { get; private set; }
    public static int MySlot { get; private set; } = -1;
    public static string RoomCode { get; private set; } = "";
    /// <summary>User id per slot; <see cref="ulong.MaxValue"/> = empty slot.</summary>
    public static ulong[] Roster { get; private set; } = Array.Empty<ulong>();
    public static string Status { get; private set; } = Text.T("offline", "未連線");
    public static int Ping => _peer?.Ping ?? -1;
    public static bool ServerRunning => _server != null;

    /// <summary>A game message from another player: (sender slot, sender id, payload).</summary>
    public static event Action<int, ulong, byte[]>? Message;
    public static event Action? RosterChanged;

    /// <summary>Start the relay inside this game (if needed) and open a room as its host.</summary>
    public static bool HostGame(int port, string name)
    {
        Disconnect();
        if (_server == null)
        {
            var server = new RelayServer(s => CoopPlugin.L.LogInfo("[server] " + s)) { Version = CoopPlugin.Version };
            if (!server.Start(port))
            {
                Status = Text.T($"cannot open UDP port {port} (is another host running?)", $"無法開啟 UDP 連接埠 {port}（是不是已經開著另一個房間？）");
                return false;
            }
            _server = server;
        }
        _createRoom = true;
        return Connect("127.0.0.1", port, name);
    }

    /// <summary>Join the host at <paramref name="address"/>; an empty code joins any room with space.</summary>
    public static bool JoinGame(string address, int port, string name, string code = "")
    {
        Disconnect();
        _createRoom = false;
        _joinCode = code;
        return Connect(address, port, name);
    }

    private static bool Connect(string address, int port, string name)
    {
        _name = name;
        _net = new NetManager(Events) { DisconnectTimeout = 10000, UpdateTime = 5, AutoRecycle = true, UnconnectedMessagesEnabled = true };
        if (!_net.Start())
        {
            Status = Text.T("cannot open a UDP socket", "無法開啟 UDP 連線");
            _net = null;
            return false;
        }
        var key = new NetDataWriter();
        key.Put(RelayServer.ConnectionKey);
        _peer = _net.Connect(address, port, key);
        State = NetState.Connecting;
        Status = Text.T($"connecting to {address}:{port}", $"正在連線到 {address}:{port}");
        return true;
    }

    /// <summary>Leave the room and drop the connection. A host also stops its server.</summary>
    public static void Disconnect()
    {
        if (_peer != null && State == NetState.InRoom) Send(new PacketWriter(Op.Leave));
        _net?.Stop();
        _net = null;
        _peer = null;
        bool hadRoom = State == NetState.InRoom;
        State = NetState.Offline;
        MySlot = -1;
        IsHost = false;
        RoomCode = "";
        Roster = Array.Empty<ulong>();
        Status = Text.T("offline", "未連線");
        _server?.Stop();
        _server = null;
        if (hadRoom) RosterChanged?.Invoke();
    }

    public static void Poll()
    {
        _server?.Poll();
        _net?.PollEvents();
    }

    /// <summary>Send a game message to one slot (or -1 = everyone else in the room).</summary>
    public static void SendTo(int slot, byte[] payload, bool reliable)
    {
        if (State != NetState.InRoom) return;
        var w = new PacketWriter(Op.Relay).I32(slot).U8(reliable ? RelayServer.RelayReliable : (byte)0).Blob(payload);
        Send(w, reliable ? DeliveryMethod.ReliableOrdered : DeliveryMethod.Sequenced);
    }

    public static void SendAll(byte[] payload, bool reliable) => SendTo(-1, payload, reliable);

    private static void Send(PacketWriter w, DeliveryMethod method = DeliveryMethod.ReliableOrdered) =>
        _peer?.Send(w.ToArray(), method);

    private sealed class Listener : INetEventListener
    {
        public void OnPeerConnected(NetPeer peer)
        {
            Status = Text.T("connected, saying hello", "已連線，打招呼中");
            Send(new PacketWriter(Op.Hello).U8(RelayServer.ProtocolVersion).Str(_name).U64(0));
        }

        public void OnPeerDisconnected(NetPeer peer, DisconnectInfo info)
        {
            if (peer != _peer) return;
            bool hadRoom = State == NetState.InRoom;
            State = NetState.Offline;
            MySlot = -1;
            Roster = Array.Empty<ulong>();
            Status = info.Reason switch
            {
                DisconnectReason.ConnectionFailed => Text.T("could not reach the host (check the IP, Radmin VPN and the host's firewall)", "連不到房主（請檢查 IP、Radmin VPN 和房主的防火牆）"),
                DisconnectReason.ConnectionRejected => Text.T("the host refused the connection (different PataCoop version?)", "房主拒絕連線（PataCoop 版本不同？）"),
                DisconnectReason.Timeout => Text.T("connection lost (timeout)", "連線中斷（逾時）"),
                _ => Text.T("disconnected: ", "已斷線：") + info.Reason,
            };
            if (hadRoom) RosterChanged?.Invoke();
        }

        public void OnNetworkReceive(NetPeer peer, NetPacketReader reader, byte channel, DeliveryMethod method)
        {
            PacketReader r;
            try { r = new PacketReader(reader.GetRemainingBytes()); }
            catch (Exception e) { CoopPlugin.L.LogWarning("bad packet from server: " + e.Message); return; }
            switch (r.Op)
            {
                case Op.Welcome:
                    MyId = r.U64();
                    State = NetState.Connected;
                    Status = _createRoom ? Text.T("opening a room", "開房中") : Text.T("joining", "加入中");
                    Send(_createRoom ? new PacketWriter(Op.CreateRoom).I32(-1) : new PacketWriter(Op.JoinRoom).Str(_joinCode));
                    break;
                case Op.RoomJoined:
                    RoomCode = r.Str() ?? "";
                    MySlot = r.I32();
                    IsHost = r.Bool();
                    State = NetState.InRoom;
                    Status = IsHost ? Text.T($"hosting room {RoomCode}", $"已開房：{RoomCode}") : Text.T($"in room {RoomCode}", $"已加入房間：{RoomCode}");
                    break;
                case Op.Roster:
                    int n = r.I32();
                    var ids = new ulong[n];
                    for (int i = 0; i < n; i++) ids[i] = r.U64();
                    Roster = ids;
                    if (n == 0 && State == NetState.InRoom)
                    {
                        State = NetState.Connected;
                        MySlot = -1;
                        Status = Text.T("the host closed the room", "房主關閉了房間");
                    }
                    RosterChanged?.Invoke();
                    break;
                case Op.Refusal:
                    int code = r.I32();
                    Status = Text.T("refused: ", "被拒絕：") + (r.Str() ?? code.ToString());
                    break;
                case Op.Relay:
                    ulong from = r.U64();
                    int fromSlot = r.I32();
                    var payload = r.Blob();
                    if (payload != null) Message?.Invoke(fromSlot, from, payload);
                    break;
            }
        }

        public void OnNetworkError(IPEndPoint endPoint, SocketError socketError) => Status = Text.T("network error: ", "網路錯誤：") + socketError;
        public void OnNetworkReceiveUnconnected(IPEndPoint remoteEndPoint, NetPacketReader reader, UnconnectedMessageType messageType) { }
        public void OnNetworkLatencyUpdate(NetPeer peer, int latency) { }
        public void OnConnectionRequest(ConnectionRequest request) => request.Reject();
    }
}
