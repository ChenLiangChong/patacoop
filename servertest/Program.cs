using System.Collections.Concurrent;
using LiteNetLib;
using LiteNetLib.Utils;
using PataCoop.Server;

int port = args.Length > 0 ? int.Parse(args[0]) : 27115;
int failures = 0;
const byte Proto = 8; const int FindSize = 160; // RelayServer.ProtocolVersion and RelayServer.FindSize
void Check(bool ok, string what) { Console.WriteLine((ok ? "PASS " : "FAIL ") + what); if (!ok) failures++; }

var a = new TestClient("Alice", port);
var b = new TestClient("Bob", port);
var c = new TestClient("Carol", port);

// --- handshake
Check(a.Connect() && b.Connect() && c.Connect(), "three clients connect with the right key");
a.Hello(); b.Hello(); c.Hello();
var wa = a.Expect(Op.Welcome); var wb = b.Expect(Op.Welcome); var wc = c.Expect(Op.Welcome);
Check(wa != null && wb != null && wc != null, "each client is welcomed");
ulong idA = wa!.U64(), idB = wb!.U64(), idC = wc!.U64();
Check(idA != idB && idB != idC, $"server assigns distinct ids even with the same steam id ({idA}, {idB}, {idC})");

// --- create / list / join
a.Send(new PacketWriter(Op.CreateRoom).I32(-1));
var joinedA = a.Expect(Op.RoomJoined);
string code = joinedA!.Str()!; int slotA = joinedA.I32(); bool hostA = joinedA.Bool();
Check(slotA == 0 && hostA, $"host gets slot 0 in room {code}");
var rosterA = a.Expect(Op.Roster);
Check(rosterA != null, "host receives a roster");

// --- room search without a connection (what the in-game panel does on the LAN / Radmin network)
var seeker = new TestClient("Seeker", port);
byte[] Ask(int size) { var w = new PacketWriter(Op.Find).U8(Proto).ToArray(); Array.Resize(ref w, size); return w; }
seeker.SendUnconnected(Ask(FindSize), broadcast: false);
var found = seeker.ExpectUnconnected();
if (found != null)
{
    byte proto = found.U8(); string version = found.Str()!; int rooms = found.U8();
    string fCode = found.Str()!; string fHost = found.Str()!; int players = found.U8(); int cap = found.U8(); bool open = found.Bool();
    Check(proto == Proto && version != null && rooms == 1 && fCode == code && fHost == "Alice" && players == 1 && cap == 4 && open,
        $"a search sent straight to the host finds the room ({fCode} by {fHost}, {players}/{cap})");
}
else Check(false, "a search sent straight to the host is answered");
seeker.SendUnconnected(Ask(FindSize), broadcast: true);
Check(seeker.ExpectUnconnected() != null, "a broadcast search is answered");
seeker.SendUnconnected(Ask(FindSize - 1), broadcast: false);
Check(seeker.ExpectUnconnected(500) == null, "a search shorter than the answer could be is ignored (no flooding through the server)");
seeker.Disconnect();

b.Send(new PacketWriter(Op.ListRooms));
var list = b.Expect(Op.RoomList)!;
int n = list.I32();
string listedCode = n > 0 ? list.Str()! : ""; string hostName = n > 0 ? list.Str()! : "";
Check(n == 1 && listedCode == code && hostName == "Alice", "room appears in the lobby list with the host name");

b.Send(new PacketWriter(Op.JoinRoom).Str(code.ToLowerInvariant()));
var joinedB = b.Expect(Op.RoomJoined)!;
Check(joinedB.Str() == code && joinedB.I32() == 1 && !joinedB.Bool(), "guest joins by code (case-insensitive) into slot 1");
var rB = b.Expect(Op.Roster)!; int cnt = rB.I32(); ulong r0 = rB.U64(), r1 = rB.U64();
Check(cnt == 2 && r0 == idA && r1 == idB, "roster lists host then guest by slot");
Check(a.Expect(Op.Roster) != null, "host is told about the new guest");

c.Send(new PacketWriter(Op.JoinRoom).Str(""));
var joinedC = c.Expect(Op.RoomJoined)!;
Check(joinedC.Str() == code && joinedC.I32() == 2, "empty code joins any open room (slot 2)");
a.Expect(Op.Roster); b.Expect(Op.Roster); c.Expect(Op.Roster);

// --- relays
a.Send(new PacketWriter(Op.Frame).U64(999).I32(0).U16(7).U16(42).Blob(new byte[] { 1, 2, 3 }), DeliveryMethod.Unreliable);
var fb = b.Expect(Op.Frame); var fc = c.Expect(Op.Frame);
Check(fb != null && fc != null, "frame packet reaches both other members");
if (fb != null)
{
    ulong sender = fb.U64(); int pid = fb.I32(); ushort proto = fb.U16(); ushort ctr = fb.U16(); var blob = fb.Blob();
    Check(sender == idA && pid == 0 && proto == 7 && ctr == 42 && blob!.SequenceEqual(new byte[] { 1, 2, 3 }),
        "frame fields survive and the sender id is stamped by the server (spoofed 999 ignored)");
}
Check(a.Expect(Op.Frame, 300) == null, "sender does not get its own frame back");

b.Send(new PacketWriter(Op.Battle).U64(0).I32(2).Blob(new byte[] { 9 }));
var bc = c.Expect(Op.Battle);
Check(bc != null && bc.U64() == idB && bc.I32() == 2, "battle packet goes to the target slot");
Check(a.Expect(Op.Battle, 300) == null, "battle packet is not sent to non-targets");

c.Send(new PacketWriter(Op.Loadout).U64(0).Blob(new byte[] { 5, 5 }));
Check(a.Expect(Op.Loadout) != null && b.Expect(Op.Loadout) != null, "loadout is shared with the room");

c.Send(new PacketWriter(Op.Scene).U64(0).I32(3));
var sa = a.Expect(Op.Scene);
Check(sa != null && sa.U64() == idC && sa.I32() == 3, "scene sync is relayed with sender id");
b.Expect(Op.Scene);

// --- generic relay (PataCoop game messages)
a.Send(new PacketWriter(Op.Relay).I32(-1).U8(1).Blob(new byte[] { 7, 7 }));
var ra = b.Expect(Op.Relay); var rc = c.Expect(Op.Relay);
Check(ra != null && rc != null, "relay to everyone reaches both other members");
if (ra != null) { ulong from = ra.U64(); int fromSlot = ra.I32(); var body = ra.Blob(); Check(from == idA && fromSlot == 0 && body!.SequenceEqual(new byte[] { 7, 7 }), "relay carries sender id, sender slot and payload"); }
Check(a.Expect(Op.Relay, 300) == null, "relay sender does not get its own message");
b.Send(new PacketWriter(Op.Relay).I32(0).U8(0).Blob(new byte[] { 1 }), DeliveryMethod.Unreliable);
var toHost = a.Expect(Op.Relay);
Check(toHost != null && toHost.U64() == idB && toHost.I32() == 1, "targeted relay reaches slot 0 with the sender slot");
Check(c.Expect(Op.Relay, 300) == null, "targeted relay does not reach others");
var big = new byte[20000]; new Random(1).NextBytes(big);
c.Send(new PacketWriter(Op.Relay).I32(0).U8(1).Blob(big));
var bigIn = a.Expect(Op.Relay, 3000);
Check(bigIn != null && bigIn.U64() == idC && bigIn.I32() == 2 && bigIn.Blob()!.SequenceEqual(big), "20 KB reliable relay arrives intact (fragmented)");

// --- refusals
var d = new TestClient("Dave", port);
d.Connect(); d.Hello(); d.Expect(Op.Welcome);
d.Send(new PacketWriter(Op.JoinRoom).Str("ZZZZZ"));
var refusal = d.Expect(Op.Refusal);
Check(refusal != null && refusal.I32() == (int)RefuseReason.UnknownRoom, "unknown code is refused with code 1");
d.Send(new PacketWriter(Op.JoinRoom).Str(code));
d.Expect(Op.RoomJoined); a.Expect(Op.Roster); b.Expect(Op.Roster); c.Expect(Op.Roster); d.Expect(Op.Roster);
var e = new TestClient("Eve", port);
e.Connect(); e.Hello(); e.Expect(Op.Welcome);
e.Send(new PacketWriter(Op.JoinRoom).Str(code));
var full = e.Expect(Op.Refusal);
Check(full != null && full.I32() == (int)RefuseReason.RoomFull, "fifth player is refused: room full");
b.Send(new PacketWriter(Op.CreateRoom).I32(-1));
var already = b.Expect(Op.Refusal);
Check(already != null && already.I32() == (int)RefuseReason.AlreadyInRoom, "creating while in a room is refused");

var bad = new TestClient("Mallory", port, key: "patamod/7");
Check(!bad.Connect(), "wrong protocol key is rejected");

// --- leaving
c.Send(new PacketWriter(Op.Leave));
var afterLeave = a.Expect(Op.Roster)!; int len = afterLeave.I32();
var ids = Enumerable.Range(0, len).Select(_ => afterLeave.U64()).ToArray();
Check(len == 4 && ids[2] == ulong.MaxValue && ids[3] != ulong.MaxValue, "guest leaving leaves a gap in the slot roster");
b.Expect(Op.Roster); d.Expect(Op.Roster);

a.Disconnect();
var closedB = b.Expect(Op.Roster, 3000);
Check(closedB != null && closedB.I32() == 0, "host disconnect closes the room (empty roster to guests)");
b.Send(new PacketWriter(Op.ListRooms));
var empty = b.Expect(Op.RoomList)!;
Check(empty.I32() == 0, "closed room disappears from the lobby list");

foreach (var t in new[] { b, c, d, e }) t.Disconnect();
Console.WriteLine(failures == 0 ? "ALL PASSED" : $"{failures} FAILED");
return failures == 0 ? 0 : 1;

sealed class TestClient : INetEventListener
{
    private readonly NetManager _net;
    private readonly string _name, _key;
    private readonly int _port;
    private NetPeer? _peer;
    private bool _connected, _rejected;
    private readonly ConcurrentQueue<PacketReader> _inbox = new();
    private readonly ConcurrentQueue<PacketReader> _unconnected = new();

    public TestClient(string name, int port, string key = "patamod/8")
    {
        _name = name; _port = port; _key = key;
        _net = new NetManager(this) { DisconnectTimeout = 5000, UnconnectedMessagesEnabled = true, IPv6Enabled = false };
        _net.Start();
    }

    public bool Connect()
    {
        _peer = _net.Connect("127.0.0.1", _port, _key);
        var until = DateTime.UtcNow.AddSeconds(3);
        while (!_connected && !_rejected && DateTime.UtcNow < until) { _net.PollEvents(); Thread.Sleep(5); }
        return _connected;
    }

    public void Hello() => Send(new PacketWriter(Op.Hello).U8(8).Str(_name).U64(76561198000000000));
    public void Send(PacketWriter w, DeliveryMethod m = DeliveryMethod.ReliableOrdered) => _peer!.Send(w.ToArray(), m);
    public void Disconnect() { _net.DisconnectAll(); _net.Stop(); }

    public void SendUnconnected(byte[] data, bool broadcast)
    {
        if (broadcast) _net.SendBroadcast(data, _port);
        else _net.SendUnconnectedMessage(data, new System.Net.IPEndPoint(System.Net.IPAddress.Loopback, _port));
    }

    /// <summary>The next room-search answer, already past its op byte.</summary>
    public PacketReader? ExpectUnconnected(int ms = 2000)
    {
        var until = DateTime.UtcNow.AddMilliseconds(ms);
        while (DateTime.UtcNow < until)
        {
            _net.PollEvents();
            if (_unconnected.TryDequeue(out var r) && r.Op == Op.Found) return r;
            Thread.Sleep(5);
        }
        return null;
    }

    public PacketReader? Expect(Op op, int ms = 2000)
    {
        var until = DateTime.UtcNow.AddMilliseconds(ms);
        while (DateTime.UtcNow < until)
        {
            _net.PollEvents();
            if (_inbox.TryDequeue(out var r))
            {
                if (r.Op == op) return r;
                continue; // skip unrelated traffic
            }
            Thread.Sleep(5);
        }
        return null;
    }

    public void OnPeerConnected(NetPeer peer) => _connected = true;
    public void OnPeerDisconnected(NetPeer peer, DisconnectInfo info) { if (info.Reason == DisconnectReason.ConnectionRejected) _rejected = true; }
    public void OnNetworkReceive(NetPeer peer, NetPacketReader reader, byte channel, DeliveryMethod method) => _inbox.Enqueue(new PacketReader(reader.GetRemainingBytes()));
    public void OnNetworkError(System.Net.IPEndPoint endPoint, System.Net.Sockets.SocketError socketError) { }
    public void OnNetworkReceiveUnconnected(System.Net.IPEndPoint remoteEndPoint, NetPacketReader reader, UnconnectedMessageType messageType) =>
        _unconnected.Enqueue(new PacketReader(reader.GetRemainingBytes()));
    public void OnNetworkLatencyUpdate(NetPeer peer, int latency) { }
    public void OnConnectionRequest(ConnectionRequest request) => request.Reject();
}
