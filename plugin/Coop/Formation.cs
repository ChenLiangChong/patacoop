using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using Il2CppInterop.Runtime.InteropTypes.Arrays;
using P2.Game.Unit;

namespace PataCoop.Coop;

/// <summary>
/// One player's army as the game hands it to a mission: the squads of the player unit layout
/// (written by the headquarters when the formation changes) and every unit's adding parameters
/// (class, level, weapon, equipment, evolution). Plain data, so it can travel between games.
/// </summary>
internal sealed class Formation
{
    public sealed class Equip
    {
        public string Name = "";
        public uint Crc, State;
        public int NodeIndex, ItemIdx;
    }

    public sealed class Unit
    {
        public string AName = "", ParamName = "";
        public uint ParamCrc;
        public int Id, UniqueId, Level, Experience, WeaponId, WeaponAttributeId, IsLive, DeadCount, MissionCount, Cap;
        public byte[] Rgba = Array.Empty<byte>();
        public short AdjustDamageParamId, BirthParamId;
        public List<Equip> Equips = new();
        public sbyte EvolutionClass, EvolutionClassLevel;
        public sbyte[] EvolutionLevel = Array.Empty<sbyte>();
    }

    public sealed class Squad
    {
        public string AName = "", ParamName = "";
        public uint ParamCrc;
        public int Id, RenderPriority, IsHero;
        public short UnitNum, InitUnitNum, IsFixed, IsDefaultAppear, IsUseDefaultUnitParam, IsDefaultStay;
        public float PosX, PosY, PosZ, PosRandX;
        public List<Unit> Units = new();
    }

    public List<Squad> Squads = new();
    public int UnitCount { get { int n = 0; foreach (var s in Squads) n += s.Units.Count; return n; } }

    public override string ToString()
    {
        var sb = new StringBuilder();
        foreach (var s in Squads) sb.Append(sb.Length > 0 ? ", " : "").Append(s.ParamName).Append('x').Append(s.Units.Count).Append(s.IsHero != 0 ? " (hero)" : "");
        return sb.Length == 0 ? "(empty)" : sb.ToString();
    }

    // ------------------------------------------------------------------ from / to the game

    /// <summary>Read the squads the headquarters wrote into the player unit layout.</summary>
    public static Formation Capture(UnitLayoutParam layout)
    {
        var f = new Formation();
        var troop = layout.troopAddingParam;
        int unit = 0;
        for (int i = 0; i < troop.squadNum && i < layout.squadAddingParam.Length; i++)
        {
            var src = layout.squadAddingParam[i];
            var s = new Squad
            {
                AName = src.aName ?? "", Id = src.id, UnitNum = src.unitNum, InitUnitNum = src.initUnitNum,
                IsFixed = src.isFixed, IsDefaultAppear = src.isDefaultAppear, IsUseDefaultUnitParam = src.isUseDefaultUnitParam,
                RenderPriority = src.renderPriority, IsDefaultStay = src.isDefaultStay, IsHero = src.isHero,
                PosX = src.posX, PosY = src.posY, PosZ = src.posZ, PosRandX = src.posRandX,
                ParamName = src.unitParam?.name ?? "", ParamCrc = src.unitParam?.crc ?? 0,
            };
            for (int u = 0; u < src.unitNum && unit < layout.unitAddingParam.Length; u++, unit++)
                s.Units.Add(CaptureUnit(layout.unitAddingParam[unit]));
            f.Squads.Add(s);
        }
        return f;
    }

    private static Unit CaptureUnit(UnitAddingParam p)
    {
        var u = new Unit
        {
            AName = p.aName ?? "", Id = p.id, UniqueId = p.uniqueId, Level = p.level, Experience = p.experience,
            WeaponId = p.weaponId, WeaponAttributeId = p.weaponAttributeId, IsLive = p.isLive, DeadCount = p.deadCount,
            MissionCount = p.missionCount, Cap = p.cap, AdjustDamageParamId = p.adjustDamageParamId, BirthParamId = p.birthParamId,
            ParamName = p.unitParam_dmy?.name ?? "", ParamCrc = p.unitParam_dmy?.crc ?? 0,
            EvolutionClass = p.evolutionClass, EvolutionClassLevel = p.evolutionClassLevel,
        };
        if (p.rgba != null) { u.Rgba = new byte[p.rgba.Length]; for (int i = 0; i < u.Rgba.Length; i++) u.Rgba[i] = p.rgba[i]; }
        if (p.evolutionLevel != null) { u.EvolutionLevel = new sbyte[p.evolutionLevel.Length]; for (int i = 0; i < u.EvolutionLevel.Length; i++) u.EvolutionLevel[i] = p.evolutionLevel[i]; }
        if (p.equipParam != null)
            foreach (var e in p.equipParam)
                u.Equips.Add(e == null ? new Equip { NodeIndex = -1, ItemIdx = -1 }
                    : new Equip { Name = e.name?.name ?? "", Crc = e.name?.crc ?? 0, State = e.state, NodeIndex = e.nodeIndex, ItemIdx = e.itemIdx });
        return u;
    }

    /// <summary>Write one squad into layout slots (the slot objects already exist).</summary>
    public static void WriteSquad(Squad s, SquadAddingParam dst, int id)
    {
        dst.aName = s.AName;
        dst.id = id;
        dst.unitNum = (short)s.Units.Count;
        dst.initUnitNum = (short)s.Units.Count;
        dst.isFixed = s.IsFixed;
        dst.isDefaultAppear = s.IsDefaultAppear;
        dst.isUseDefaultUnitParam = s.IsUseDefaultUnitParam;
        dst.renderPriority = s.RenderPriority;
        dst.isDefaultStay = s.IsDefaultStay;
        dst.isHero = s.IsHero;
        dst.posX = s.PosX; dst.posY = s.PosY; dst.posZ = s.PosZ; dst.posRandX = s.PosRandX;
        SetName(dst.unitParam, s.ParamName, s.ParamCrc);
    }

    public static void WriteUnit(Unit u, UnitAddingParam dst, int index)
    {
        dst.aName = u.AName;
        dst.id = index;
        dst.uniqueId = index;
        dst.level = u.Level; dst.experience = u.Experience;
        dst.weaponId = u.WeaponId; dst.weaponAttributeId = u.WeaponAttributeId;
        dst.isLive = u.IsLive; dst.deadCount = u.DeadCount; dst.missionCount = u.MissionCount; dst.cap = u.Cap;
        dst.adjustDamageParamId = u.AdjustDamageParamId; dst.birthParamId = u.BirthParamId;
        dst.evolutionClass = u.EvolutionClass; dst.evolutionClassLevel = u.EvolutionClassLevel;
        SetName(dst.unitParam_dmy, u.ParamName, u.ParamCrc);
        if (dst.rgba == null || dst.rgba.Length != u.Rgba.Length) dst.rgba = new Il2CppStructArray<byte>(u.Rgba.Length);
        for (int i = 0; i < u.Rgba.Length; i++) dst.rgba[i] = u.Rgba[i];
        if (dst.evolutionLevel == null || dst.evolutionLevel.Length != u.EvolutionLevel.Length) dst.evolutionLevel = new Il2CppStructArray<sbyte>(u.EvolutionLevel.Length);
        for (int i = 0; i < u.EvolutionLevel.Length; i++) dst.evolutionLevel[i] = u.EvolutionLevel[i];
        var equips = dst.equipParam;
        for (int i = 0; equips != null && i < equips.Length; i++)
        {
            var e = equips[i];
            if (e == null) continue;
            var src = i < u.Equips.Count ? u.Equips[i] : new Equip { NodeIndex = -1, ItemIdx = -1 };
            SetName(e.name, src.Name, src.Crc);
            e.state = src.State; e.nodeIndex = src.NodeIndex; e.itemIdx = src.ItemIdx;
        }
    }

    private static void SetName(AddingParamName? n, string name, uint crc)
    {
        if (n is null) return;
        n.name = name;
        n.crc = crc;
    }

    // ------------------------------------------------------------------ wire format

    public byte[] ToBytes()
    {
        var ms = new MemoryStream();
        var w = new BinaryWriter(ms, Encoding.UTF8);
        w.Write((byte)1); // format version
        w.Write((byte)Squads.Count);
        foreach (var s in Squads)
        {
            w.Write(s.AName); w.Write(s.ParamName); w.Write(s.ParamCrc);
            w.Write(s.Id); w.Write(s.RenderPriority); w.Write(s.IsHero);
            w.Write(s.UnitNum); w.Write(s.InitUnitNum); w.Write(s.IsFixed); w.Write(s.IsDefaultAppear); w.Write(s.IsUseDefaultUnitParam); w.Write(s.IsDefaultStay);
            w.Write(s.PosX); w.Write(s.PosY); w.Write(s.PosZ); w.Write(s.PosRandX);
            w.Write((byte)s.Units.Count);
            foreach (var u in s.Units)
            {
                w.Write(u.AName); w.Write(u.ParamName); w.Write(u.ParamCrc);
                w.Write(u.Id); w.Write(u.UniqueId); w.Write(u.Level); w.Write(u.Experience); w.Write(u.WeaponId); w.Write(u.WeaponAttributeId);
                w.Write(u.IsLive); w.Write(u.DeadCount); w.Write(u.MissionCount); w.Write(u.Cap);
                w.Write((byte)u.Rgba.Length); w.Write(u.Rgba);
                w.Write(u.AdjustDamageParamId); w.Write(u.BirthParamId);
                w.Write((byte)u.Equips.Count);
                foreach (var e in u.Equips) { w.Write(e.Name); w.Write(e.Crc); w.Write(e.State); w.Write(e.NodeIndex); w.Write(e.ItemIdx); }
                w.Write(u.EvolutionClass); w.Write(u.EvolutionClassLevel);
                w.Write((byte)u.EvolutionLevel.Length);
                foreach (var v in u.EvolutionLevel) w.Write(v);
            }
        }
        w.Flush();
        return ms.ToArray();
    }

    public static Formation FromBytes(byte[] data)
    {
        var r = new BinaryReader(new MemoryStream(data, false), Encoding.UTF8);
        if (r.ReadByte() != 1) throw new InvalidDataException("unknown formation format");
        var f = new Formation();
        int squads = r.ReadByte();
        for (int i = 0; i < squads; i++)
        {
            var s = new Squad
            {
                AName = r.ReadString(), ParamName = r.ReadString(), ParamCrc = r.ReadUInt32(),
                Id = r.ReadInt32(), RenderPriority = r.ReadInt32(), IsHero = r.ReadInt32(),
                UnitNum = r.ReadInt16(), InitUnitNum = r.ReadInt16(), IsFixed = r.ReadInt16(), IsDefaultAppear = r.ReadInt16(),
                IsUseDefaultUnitParam = r.ReadInt16(), IsDefaultStay = r.ReadInt16(),
                PosX = r.ReadSingle(), PosY = r.ReadSingle(), PosZ = r.ReadSingle(), PosRandX = r.ReadSingle(),
            };
            int units = r.ReadByte();
            for (int j = 0; j < units; j++)
            {
                var u = new Unit
                {
                    AName = r.ReadString(), ParamName = r.ReadString(), ParamCrc = r.ReadUInt32(),
                    Id = r.ReadInt32(), UniqueId = r.ReadInt32(), Level = r.ReadInt32(), Experience = r.ReadInt32(),
                    WeaponId = r.ReadInt32(), WeaponAttributeId = r.ReadInt32(),
                    IsLive = r.ReadInt32(), DeadCount = r.ReadInt32(), MissionCount = r.ReadInt32(), Cap = r.ReadInt32(),
                };
                u.Rgba = r.ReadBytes(r.ReadByte());
                u.AdjustDamageParamId = r.ReadInt16(); u.BirthParamId = r.ReadInt16();
                int equips = r.ReadByte();
                for (int k = 0; k < equips; k++)
                    u.Equips.Add(new Equip { Name = r.ReadString(), Crc = r.ReadUInt32(), State = r.ReadUInt32(), NodeIndex = r.ReadInt32(), ItemIdx = r.ReadInt32() });
                u.EvolutionClass = r.ReadSByte(); u.EvolutionClassLevel = r.ReadSByte();
                int lv = r.ReadByte();
                u.EvolutionLevel = new sbyte[lv];
                for (int k = 0; k < lv; k++) u.EvolutionLevel[k] = r.ReadSByte();
                s.Units.Add(u);
            }
            f.Squads.Add(s);
        }
        return f;
    }
}
