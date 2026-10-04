using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using LiteNetLib;

namespace PataCoop.Server;

public sealed class Player
{
    public required NetPeer Peer { get; init; }
    public ulong Id { get; set; }
    public string Name { get; set; } = "?";
    public ulong SteamId { get; set; }
    public bool Greeted { get; set; }
    public Room? Room { get; set; }
    public int Slot { get; set; } = -1;
}

public sealed class Room
{
    public const int Capacity = 4;

    public required string Code { get; init; }
    public required Player Host { get; init; }
    public Player?[] Slots { get; } = new Player?[Capacity];
    /// <summary>Bitmask of slots the host's game says may be filled; null = all.</summary>
    public int? OpenMask { get; set; }
    public byte PhaseId { get; set; }
    public int MissionId { get; set; } = -1;

    public IEnumerable<Player> Members => Slots.Where(p => p != null)!;
    public int Count => Slots.Count(p => p != null);

    public int FreeSlot()
    {
        int mask = OpenMask ?? 0b1111;
        for (int i = 0; i < Capacity; i++)
            if (Slots[i] == null && (mask & (1 << i)) != 0)
                return i;
        return -1;
    }

    public bool HasRoom => FreeSlot() >= 0;

    /// <summary>User ids by slot, trimmed after the highest occupied slot; gaps are ulong.MaxValue.</summary>
    public ulong[] RosterBySlot()
    {
        int last = -1;
        for (int i = 0; i < Capacity; i++) if (Slots[i] != null) last = i;
        var ids = new ulong[last + 1];
        for (int i = 0; i <= last; i++) ids[i] = Slots[i]?.Id ?? ulong.MaxValue;
        return ids;
    }
}

public sealed class RelayServer : INetEventListener
{
    // 9: PataCoop 0.4.2 (every army walks on its own drums; army positions shared each frame)
    public const byte ProtocolVersion = 9;
    /// <summary>
    /// A room search must be at least this long and is answered with no more bytes than it had, so
    /// nobody can use a server to flood a third party with answers bigger than what they sent.
    /// </summary>
    public const int FindSize = 160;
    private const int MaxNameInAnswer = 16, MaxRoomsInAnswer = 4;
    public static readonly string ConnectionKey = "patamod/" + ProtocolVersion;
    private static readonly char[] CodeChars = "ABCDEFGHJKLMNPQRSTUVWXYZ23456789".ToCharArray();

    private readonly NetManager _net;
    private readonly Dictionary<int, Player> _byPeer = new();
    private readonly Dictionary<string, Room> _rooms = new(StringComparer.OrdinalIgnoreCase);
    private readonly Action<string> _log;
    private ulong _nextId = (ulong)Random.Shared.NextInt64(1000, 9000) * 1000;

    /// <summary>Version shown to players searching for rooms (the game plugin's version).</summary>
    public string Version { get; init; } = "";

    public RelayServer(Action<string> log)
    {
        _log = log;
        _net = new NetManager(this)
        {
            DisconnectTimeout = 15000,
            UpdateTime = 5,
            AutoRecycle = true,
            // room searches: broadcasts on the LAN / Radmin network and questions sent straight to us
            UnconnectedMessagesEnabled = true,
            BroadcastReceiveEnabled = true,
        };
    }

    public bool Start(int port) => _net.Start(port);
    public void Poll() => _net.PollEvents();
    public void Stop() => _net.Stop();

    public string Status() =>
        $"{_byPeer.Count} player(s), {_rooms.Count} room(s)" +
        string.Concat(_rooms.Values.Select(r => $" | {r.Code}: {string.Join(", ", r.Members.Select(m => $"{m.Name}@{m.Slot}"))}"));

    // ---- connection lifecycle -------------------------------------------------

    public void OnConnectionRequest(ConnectionRequest request)
    {
        string key = request.Data.AvailableBytes > 0 ? request.Data.GetString() : "";
        if (key == ConnectionKey)
        {
            request.Accept();
            return;
        }
        _log($"rejected {request.RemoteEndPoint}: key '{key}'");
        // "PNV" + our version, so an out-of-date client can say why it was refused.
        request.Reject(new byte[] { (byte)'P', (byte)'N', (byte)'V', ProtocolVersion });
    }

    public void OnPeerConnected(NetPeer peer)
    {
        _byPeer[peer.Id] = new Player { Peer = peer };
        _log($"connected {peer}");
    }

    public void OnPeerDisconnected(NetPeer peer, DisconnectInfo info)
    {
        if (!_byPeer.Remove(peer.Id, out var p)) return;
        LeaveRoom(p);
        _log($"disconnected {p.Name} (#{p.Id}): {info.Reason}");
    }

    public void OnNetworkError(System.Net.IPEndPoint endPoint, System.Net.Sockets.SocketError socketError) =>
        _log($"socket error {socketError} from {endPoint}");

    public void OnNetworkReceiveUnconnected(System.Net.IPEndPoint remoteEndPoint, NetPacketReader reader, UnconnectedMessageType messageType)
    {
        int size = reader.AvailableBytes;
        if (size < FindSize) return;
        try
        {
            if (new PacketReader(reader.GetRemainingBytes()).Op != Op.Find) return;
        }
        catch (Exception)
        {
            return;
        }
        var answer = FoundAnswer(_rooms.Values.Count);
        for (int rooms = _rooms.Values.Count - 1; answer.Length > size && rooms >= 0; rooms--) answer = FoundAnswer(rooms);
        if (answer.Length <= size) _net.SendUnconnectedMessage(answer, remoteEndPoint);
    }

    /// <summary>Our rooms for a player searching the network (the first <paramref name="rooms"/> of them).</summary>
    private byte[] FoundAnswer(int rooms)
    {
        var list = _rooms.Values.Take(Math.Min(rooms, MaxRoomsInAnswer)).ToList();
        var w = new PacketWriter(Op.Found).U8(ProtocolVersion).Str(Version).U8((byte)list.Count);
        foreach (var r in list)
        {
            string host = r.Host.Name.Length > MaxNameInAnswer ? r.Host.Name[..MaxNameInAnswer] : r.Host.Name;
            w.Str(r.Code).Str(host).U8((byte)r.Count).U8(Room.Capacity).Bool(r.HasRoom);
        }
        return w.ToArray();
    }

    public void OnNetworkLatencyUpdate(NetPeer peer, int latency) { }

    // ---- messages ---------------------------------------------------------------

    public void OnNetworkReceive(NetPeer peer, NetPacketReader reader, byte channel, DeliveryMethod method)
    {
        if (!_byPeer.TryGetValue(peer.Id, out var p)) return;
        PacketReader r;
        try { r = new PacketReader(reader.GetRemainingBytes()); }
        catch (Exception e) { _log($"bad packet from {p.Name}: {e.Message}"); return; }

        try
        {
            if (!p.Greeted && r.Op != Op.Hello)
            {
                Refuse(p, 0, "say hello first");
                return;
            }
            switch (r.Op)
            {
                case Op.Hello: OnHello(p, r); break;
                case Op.CreateRoom: OnCreate(p, r.I32()); break;
                case Op.JoinRoom: OnJoin(p, r.Str() ?? ""); break;
                case Op.Leave: LeaveRoom(p); break;
                case Op.OpenSlots: OnOpenSlots(p, r.I32()); break;
                case Op.ListRooms: SendRoomList(p); break;
                case Op.Phase: OnPhase(p, r.U8(), r.I32(), r.U8()); break;
                case Op.Loadout: RelayLoadout(p, r.U64(), r.Blob()); break;
                case Op.Scene: RelayScene(p, r.U64(), r.I32()); break;
                case Op.Frame: RelayFrame(p, r.U64(), r.I32(), r.U16(), r.U16(), r.Blob()); break;
                case Op.Battle: RelayBattle(p, r.U64(), r.I32(), r.Blob()); break;
                case Op.Relay: Relay(p, r.I32(), r.U8(), r.Blob()); break;
                default: _log($"{p.Name} sent server-only op {r.Op}; ignored"); break;
            }
        }
        catch (Exception e)
        {
            _log($"error handling {r.Op} from {p.Name}: {e.Message}");
        }
    }

    private void OnHello(Player p, PacketReader r)
    {
        byte version = r.U8();
        string name = r.Str() ?? "";
        ulong steamId = r.U64();
        if (version != ProtocolVersion)
        {
            Refuse(p, 0, $"protocol v{version} not supported (server is v{ProtocolVersion})");
            p.Peer.Disconnect();
            return;
        }
        p.Name = string.IsNullOrWhiteSpace(name) ? "Patapon" : name.Trim();
        p.SteamId = steamId;
        // Ids are server-assigned so two game copies on one Steam account stay distinct.
        p.Id = ++_nextId;
        p.Greeted = true;
        Send(p, new PacketWriter(Op.Welcome).U64(p.Id));
        _log($"hello {p.Name} -> #{p.Id}");
    }

    private void OnCreate(Player p, int openMask)
    {
        if (p.Room != null) { Refuse(p, (int)RefuseReason.AlreadyInRoom, "already in a room"); return; }
        var room = new Room { Code = NewCode(), Host = p, OpenMask = openMask < 0 ? null : openMask };
        _rooms[room.Code] = room;
        Seat(p, room, 0);
        _log($"{p.Name} opened room {room.Code}");
    }

    private void OnJoin(Player p, string code)
    {
        if (p.Room != null) { Refuse(p, (int)RefuseReason.AlreadyInRoom, "already in a room"); return; }
        Room? room = code.Length == 0
            ? _rooms.Values.FirstOrDefault(x => x.HasRoom)
            : _rooms.GetValueOrDefault(code.Trim());
        if (room == null) { Refuse(p, (int)RefuseReason.UnknownRoom, "no such room"); return; }
        int slot = room.FreeSlot();
        if (slot < 0) { Refuse(p, (int)RefuseReason.RoomFull, "room is full"); return; }
        Seat(p, room, slot);
        _log($"{p.Name} joined {room.Code} in slot {slot}");
    }

    private void Seat(Player p, Room room, int slot)
    {
        room.Slots[slot] = p;
        p.Room = room;
        p.Slot = slot;
        Send(p, new PacketWriter(Op.RoomJoined).Str(room.Code).I32(slot).Bool(room.Host == p));
        BroadcastRoster(room);
    }

    private void LeaveRoom(Player p)
    {
        var room = p.Room;
        if (room == null) return;
        room.Slots[p.Slot] = null;
        p.Room = null;
        p.Slot = -1;

        if (room.Host == p || room.Count == 0)
        {
            // Host left: close the room; an empty roster tells guests it is gone.
            foreach (var guest in room.Members.ToList())
            {
                guest.Room = null;
                guest.Slot = -1;
                Send(guest, new PacketWriter(Op.Roster).U64s(Array.Empty<ulong>()));
            }
            Array.Clear(room.Slots);
            _rooms.Remove(room.Code);
            _log($"room {room.Code} closed");
        }
        else
        {
            BroadcastRoster(room);
            _log($"{p.Name} left {room.Code}");
        }
    }

    private void OnOpenSlots(Player p, int mask)
    {
        if (p.Room is { } room && room.Host == p) room.OpenMask = mask < 0 ? null : mask;
    }

    private void OnPhase(Player p, byte phase, int mission, byte outcome)
    {
        if (p.Room is not { } room || room.Host != p) return;
        room.PhaseId = phase;
        room.MissionId = mission;
        _log($"room {room.Code}: phase {phase}, mission {mission}, outcome {outcome}");
    }

    private void SendRoomList(Player p)
    {
        var open = _rooms.Values.Where(r => r.HasRoom).ToList();
        var w = new PacketWriter(Op.RoomList).I32(open.Count);
        foreach (var r in open) w.Str(r.Code).Str(r.Host.Name).U8((byte)r.Count).U8(Room.Capacity);
        Send(p, w);
    }

    // ---- relays: the sender id is always stamped by the server -------------------

    private void RelayLoadout(Player p, ulong _, byte[]? payload)
    {
        if (p.Room == null) return;
        ToOthers(p, new PacketWriter(Op.Loadout).U64(p.Id).Blob(payload), DeliveryMethod.ReliableOrdered);
    }

    private void RelayScene(Player p, ulong _, int scene)
    {
        if (p.Room == null) return;
        ToOthers(p, new PacketWriter(Op.Scene).U64(p.Id).I32(scene), DeliveryMethod.ReliableOrdered);
    }

    private void RelayFrame(Player p, ulong _, int playerId, ushort protocolId, ushort counter, byte[]? packet)
    {
        if (p.Room == null) return;
        ToOthers(p, new PacketWriter(Op.Frame).U64(p.Id).I32(playerId).U16(protocolId).U16(counter).Blob(packet),
            DeliveryMethod.Unreliable);
    }

    private void RelayBattle(Player p, ulong _, int targetSlot, byte[]? payload)
    {
        if (p.Room is not { } room) return;
        var w = new PacketWriter(Op.Battle).U64(p.Id).I32(targetSlot).Blob(payload);
        var target = targetSlot >= 0 && targetSlot < Room.Capacity ? room.Slots[targetSlot] : null;
        if (target != null && target != p) Send(target, w);
        else ToOthers(p, w, DeliveryMethod.ReliableOrdered);
    }

    /// <summary>Flag bit in <see cref="Op.Relay"/>: deliver reliably and in order (otherwise unreliable, newest wins).</summary>
    public const byte RelayReliable = 1;

    private void Relay(Player p, int targetSlot, byte flags, byte[]? payload)
    {
        if (p.Room is not { } room) return;
        var w = new PacketWriter(Op.Relay).U64(p.Id).I32(p.Slot).Blob(payload);
        var method = (flags & RelayReliable) != 0 ? DeliveryMethod.ReliableOrdered : DeliveryMethod.Sequenced;
        if (targetSlot < 0) { ToOthers(p, w, method); return; }
        var target = targetSlot < Room.Capacity ? room.Slots[targetSlot] : null;
        if (target != null && target != p) target.Peer.Send(w.ToArray(), method);
    }

    // ---- helpers ------------------------------------------------------------------

    private void BroadcastRoster(Room room)
    {
        var w = new PacketWriter(Op.Roster).U64s(room.RosterBySlot());
        foreach (var m in room.Members) Send(m, w);
    }

    private void ToOthers(Player from, PacketWriter w, DeliveryMethod method)
    {
        var bytes = w.ToArray();
        foreach (var m in from.Room!.Members)
            if (m != from) m.Peer.Send(bytes, method);
    }

    private void Refuse(Player p, int code, string text)
    {
        Send(p, new PacketWriter(Op.Refusal).I32(code).Str(text));
        _log($"refused {p.Name}: {text}");
    }

    private static void Send(Player p, PacketWriter w) => p.Peer.Send(w.ToArray(), DeliveryMethod.ReliableOrdered);

    private string NewCode()
    {
        while (true)
        {
            var code = new string(Enumerable.Range(0, 5).Select(_ => CodeChars[Random.Shared.Next(CodeChars.Length)]).ToArray());
            if (!_rooms.ContainsKey(code)) return code;
        }
    }
}
