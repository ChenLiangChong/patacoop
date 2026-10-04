using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;

namespace PataCoop.Server;

/// <summary>Message ids on the wire. Values are fixed by the game-side client.</summary>
public enum Op : byte
{
    Hello = 1,
    Welcome = 2,
    CreateRoom = 3,
    JoinRoom = 4,
    RoomJoined = 5,
    Roster = 6,
    Loadout = 7,
    Leave = 8,
    Refusal = 9,
    OpenSlots = 10,
    Battle = 11,
    Scene = 12,
    Frame = 13,
    ListRooms = 14,
    RoomList = 15,
    Phase = 16,
    /// <summary>Opaque game message: client sends (I32 targetSlot or -1 = everyone else, U8 flags, Blob); server delivers (U64 fromId, I32 fromSlot, Blob).</summary>
    Relay = 17,
    /// <summary>Room search, sent without a connection (broadcast or straight to a host): U8 protocol, padding up to <see cref="RelayServer.FindSize"/> bytes.</summary>
    Find = 18,
    /// <summary>Answer to <see cref="Find"/>: U8 protocol, Str version, U8 rooms, per room (Str code, Str host name, U8 players, U8 capacity, Bool has room).</summary>
    Found = 19,
}

/// <summary>Why a join was refused. Sent as the Refusal code.</summary>
public enum RefuseReason
{
    None = 0,
    UnknownRoom = 1,
    RoomFull = 2,
    AlreadyInRoom = 3,
}

/// <summary>
/// Little-endian packet writer. Strings are a presence flag, an int32 byte
/// length and UTF-8 bytes; blobs are an int32 length (-1 = null) and bytes.
/// </summary>
public sealed class PacketWriter
{
    private readonly MemoryStream _ms = new();
    private readonly BinaryWriter _w;

    public PacketWriter(Op op)
    {
        _w = new BinaryWriter(_ms, Encoding.UTF8, leaveOpen: true);
        _w.Write((byte)op);
    }

    public PacketWriter U8(byte v) { _w.Write(v); return this; }
    public PacketWriter Bool(bool v) { _w.Write(v); return this; }
    public PacketWriter I32(int v) { _w.Write(v); return this; }
    public PacketWriter U16(ushort v) { _w.Write(v); return this; }
    public PacketWriter U64(ulong v) { _w.Write(v); return this; }

    public PacketWriter Str(string? v)
    {
        _w.Write(v != null);
        if (v == null) return this;
        var bytes = Encoding.UTF8.GetBytes(v);
        _w.Write(bytes.Length);
        _w.Write(bytes);
        return this;
    }

    public PacketWriter Blob(byte[]? v)
    {
        _w.Write(v?.Length ?? -1);
        if (v != null) _w.Write(v);
        return this;
    }

    public PacketWriter U64s(IReadOnlyList<ulong> v)
    {
        _w.Write(v.Count);
        foreach (var x in v) _w.Write(x);
        return this;
    }

    public byte[] ToArray()
    {
        _w.Flush();
        return _ms.ToArray();
    }
}

/// <summary>Bounds-checked reader matching <see cref="PacketWriter"/>.</summary>
public sealed class PacketReader
{
    private const int MaxLen = 1 << 16;
    private readonly BinaryReader _r;
    private readonly long _length;

    public Op Op { get; }

    public PacketReader(byte[] data)
    {
        if (data.Length == 0) throw new InvalidDataException("empty packet");
        _r = new BinaryReader(new MemoryStream(data, writable: false), Encoding.UTF8);
        _length = data.Length;
        byte op = _r.ReadByte();
        if (!Enum.IsDefined(typeof(Op), op)) throw new InvalidDataException($"unknown op {op}");
        Op = (Op)op;
    }

    public bool AtEnd => _r.BaseStream.Position == _length;

    public byte U8() => _r.ReadByte();
    public bool Bool() => _r.ReadBoolean();
    public int I32() => _r.ReadInt32();
    public ushort U16() => _r.ReadUInt16();
    public ulong U64() => _r.ReadUInt64();

    private int Len()
    {
        int n = _r.ReadInt32();
        if (n < -1 || n > MaxLen) throw new InvalidDataException($"length {n} out of range");
        return n;
    }

    private byte[] Exactly(int n)
    {
        var b = _r.ReadBytes(n);
        if (b.Length != n) throw new InvalidDataException("truncated packet");
        return b;
    }

    public string? Str()
    {
        if (!_r.ReadBoolean()) return null;
        int n = Len();
        return n <= 0 ? string.Empty : Encoding.UTF8.GetString(Exactly(n));
    }

    public byte[]? Blob()
    {
        int n = Len();
        return n < 0 ? null : Exactly(n);
    }
}
