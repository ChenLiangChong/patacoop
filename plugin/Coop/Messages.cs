using System;
using System.IO;
using System.Text;

namespace PataCoop.Coop;

/// <summary>Game-level messages carried inside relay packets. The first byte is the kind.</summary>
internal enum Msg : byte
{
    /// <summary>Who I am: name and mod version. Sent on joining and whenever the roster changes.</summary>
    Hello = 1,
    /// <summary>Host to all: the next mission is a co-op battle (mission id, player slots).</summary>
    Battle = 2,
    /// <summary>Mission start handshake (the game's own "sync scene" number).</summary>
    SyncScene = 3,
    /// <summary>One frame of the game's battle protocol (host packet or client packet).</summary>
    GamePacket = 4,
    /// <summary>Host to all: the co-op battle is over (cleared, failed or abandoned).</summary>
    BattleEnd = 5,
    /// <summary>My army (squads, units, equipment) as the headquarters wrote it.</summary>
    Formation = 6,
    /// <summary>Host to all: the mission the host is preparing in its headquarters (-1 = none).</summary>
    Lobby = 7,
    /// <summary>Where I am: camp / headquarters / ready / in a mission, and which mission.</summary>
    State = 8,
    /// <summary>Host to all: everyone is ready, sortie now.</summary>
    Go = 9,
    /// <summary>Host to all: the mission ended this way (clear / failed), and whether the host left at once.</summary>
    GameEnd = 10,
    /// <summary>Host to all: the battle's weather changed (kind, levels, wind direction).</summary>
    Weather = 11,
    /// <summary>Everyone to all, several times a second: my beat clock (ticks) and my ping to the relay.</summary>
    Clock = 12,
    /// <summary>Host to all, each frame something was hit: hit point changes and damage numbers.</summary>
    Hits = 13,
    /// <summary>Host to all, twice a second: the hit points of everything on the field.</summary>
    HitPoints = 14,
    /// <summary>Anyone to all: I got a story key item in this battle (item id, my total this battle).</summary>
    KeyItem = 15,
    /// <summary>Everyone to all, every frame of a co-op battle: where my army is (its base position).</summary>
    ArmyPos = 16,
    /// <summary>Host to all, every frame of a co-op battle: where each enemy unit stands.</summary>
    EnemyPos = 17,
    /// <summary>Guest to host: my army reached the goal (the host clears the mission for everyone).</summary>
    Goal = 18,
    /// <summary>Guest to host: our miracle succeeded (its score); the host activates it for everyone.</summary>
    Miracle = 19,
}

internal sealed class MsgWriter
{
    private readonly MemoryStream _ms = new();
    private readonly BinaryWriter _w;

    public MsgWriter(Msg kind)
    {
        _w = new BinaryWriter(_ms, Encoding.UTF8, leaveOpen: true);
        _w.Write((byte)kind);
    }

    public MsgWriter U8(byte v) { _w.Write(v); return this; }
    public MsgWriter I32(int v) { _w.Write(v); return this; }
    public MsgWriter U16(ushort v) { _w.Write(v); return this; }
    public MsgWriter U32(uint v) { _w.Write(v); return this; }
    public MsgWriter U64(ulong v) { _w.Write(v); return this; }
    public MsgWriter F32(float v) { _w.Write(v); return this; }
    public MsgWriter Str(string v) { _w.Write(v); return this; }

    public MsgWriter Bytes(byte[]? v)
    {
        _w.Write(v?.Length ?? -1);
        if (v != null) _w.Write(v);
        return this;
    }

    public byte[] ToArray()
    {
        _w.Flush();
        return _ms.ToArray();
    }
}

internal sealed class MsgReader
{
    private readonly BinaryReader _r;
    public Msg Kind { get; }

    public MsgReader(byte[] data)
    {
        _r = new BinaryReader(new MemoryStream(data, writable: false), Encoding.UTF8);
        Kind = (Msg)_r.ReadByte();
    }

    public byte U8() => _r.ReadByte();
    public int I32() => _r.ReadInt32();
    public ushort U16() => _r.ReadUInt16();
    public uint U32() => _r.ReadUInt32();
    public ulong U64() => _r.ReadUInt64();
    public float F32() => _r.ReadSingle();
    public string Str() => _r.ReadString();

    public byte[]? Bytes()
    {
        int n = _r.ReadInt32();
        if (n < 0) return null;
        if (n > 1 << 20) throw new InvalidDataException("message too large");
        return _r.ReadBytes(n);
    }
}
