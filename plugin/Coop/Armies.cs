using System;
using System.Collections.Generic;
using HarmonyLib;
using Il2CppInterop.Runtime.InteropTypes.Arrays;
using P2.Game;
using P2.Game.Unit;
using PataCoop.Net;

namespace PataCoop.Coop;

/// <summary>
/// Builds the player troop for a co-op battle out of every player's own army.
///
/// Squads are found by their unique id, by the engine and by mission scripts alike, and story
/// missions add squads of their own to the player troop with small ids (a Patapon waiting to be
/// rescued, say), counting on the player's squads to be 0, 1, ... So the host's squads keep the
/// ids its headquarters gave them, as in a single-player battle, and the other players' squads
/// get ids from <see cref="RemoteSquadBase"/> up, which no mission uses. Every squad we add carries
/// its owner in a reserved field, which <see cref="SquadOwnerPatch"/> uses to hand it the owner's
/// commands. Every machine builds the same troop: slot order, then each player's squad order.
/// </summary>
internal static class Armies
{
    /// <summary>"PC" in the high half of SquadAddingParam.rsv1 marks our squads; the low byte is the owner slot.</summary>
    internal const int OwnerTag = 0x50430000;

    /// <summary>Squad ids of player P (P &gt;= 1) start at RemoteSquadBase + 10 P.</summary>
    internal const int RemoteSquadBase = 100;

    /// <summary>Latest formation of each slot (own one included).</summary>
    internal static readonly Formation?[] Formations = new Formation?[Session.MaxPlayers];

    /// <summary>Who has an army in this battle (fixed when it starts, the same on every machine).</summary>
    internal static readonly bool[] InBattle = new bool[Session.MaxPlayers];

    /// <summary>
    /// Unit ids in a co-op troop start here. The game writes a unit's death back into the layout
    /// entry its id points at (ids 0..23 only), and after a clear the camp copies every layout
    /// entry into the save's unit roster at the entry's uniqueId. With every id at 64 or more the
    /// game writes nothing back during the battle; <see cref="RestoreOwnLayout"/> brings back the
    /// player's own layout (with their losses) when the battle ends, exactly as a single-player
    /// battle would leave it.
    /// </summary>
    internal const int UnitIdBase = 64;

    /// <summary>
    /// The layout arrays the headquarters wrote. The co-op troop is built in copies, so these
    /// entries stay untouched until they are put back.
    /// </summary>
    private static Il2CppReferenceArray<SquadAddingParam>? _ownSquads;
    private static Il2CppReferenceArray<UnitAddingParam>? _ownUnits;
    private static int _ownSquadNum;
    /// <summary>Troop unit id of each of our units -> its entry in <see cref="_ownUnits"/>.</summary>
    private static readonly Dictionary<int, int> OwnUnitById = new();
    /// <summary>Our units that fell (troop unit ids), noted the frame they ended.</summary>
    private static readonly HashSet<int> OwnFallen = new();

    internal static bool HasOwnUnits => OwnUnitById.Count > 0;

    /// <summary>One of the troop's units ended (fell): remember it if it is ours.</summary>
    internal static void NoteEnded(int unitId)
    {
        if (OwnUnitById.ContainsKey(unitId)) OwnFallen.Add(unitId);
    }

    internal static string Summary(int slot) => Formations[slot]?.ToString() ?? "(no formation yet)";

    /// <summary>Names of players who left during the running battle (their armies stay on the field).</summary>
    private static readonly string?[] DepartedNames = new string?[Session.MaxPlayers];

    internal static void Departed(int slot, string name)
    {
        if (slot >= 0 && slot < DepartedNames.Length) DepartedNames[slot] = name;
    }

    /// <summary>Name to show over a slot's army: the player, or the one who left mid-battle.</summary>
    internal static string ArmyName(int slot) =>
        Session.Occupied(slot) ? Session.Name(slot) : DepartedNames[slot] is { } n ? n + Text.T(" (left)", "（已離開）") : "";

    /// <summary>Whose drum a squad of <paramref name="owner"/> follows: the host's once its owner has left.</summary>
    internal static int Commander(int owner) => owner > 0 && !Session.Occupied(owner) ? 0 : owner;

    /// <summary>Hand a departed player's squads to the host's drum. Returns how many squads changed hands.</summary>
    internal static int HandOver(int slot)
    {
        var troops = P2.Game.Game.pGame_g?.getUnitMng()?.unitTroopPtrArray_;
        if (troops == null || troops.Count == 0 || troops[0]?.unitSquadPtrList_ == null) return 0;
        var inst = troops[0].getInstructionParamA(0);
        if (inst == null) return 0;
        int n = 0;
        foreach (var squad in troops[0].unitSquadPtrList_)
        {
            if (squad?.squadInfo_?.squadAddingParam?.rsv1 != (OwnerTag | slot)) continue;
            squad.pInstParam_ = inst;
            n++;
        }
        return n;
    }

    /// <summary>Read our own formation from the layout the headquarters wrote, and share it if it changed.</summary>
    internal static void PublishOwn(string reason)
    {
        var layout = P2.LaboCommon.pLaboCommonInstance_g?.laboSettingDataPtr_?.gameSettingData?.unitSettingData?.playerUnitLayoutParam;
        if (layout == null || CoopNet.MySlot < 0) return;
        var f = Formation.Capture(layout);
        if (f.Squads.Count == 0) return;
        var bytes = f.ToBytes();
        Formations[CoopNet.MySlot] = f;
        if (_lastSent != null && bytes.AsSpan().SequenceEqual(_lastSent)) return;
        _lastSent = bytes;
        CoopNet.SendAll(new MsgWriter(Msg.Formation).Bytes(bytes).ToArray(), true);
        CoopPlugin.L.LogInfo($"[coop] shared formation ({reason}): {f}");
    }

    private static byte[]? _lastSent;

    internal static void OnFormation(int fromSlot, MsgReader r)
    {
        var bytes = r.Bytes();
        if (bytes == null || fromSlot < 0 || fromSlot >= Session.MaxPlayers) return;
        Formations[fromSlot] = Formation.FromBytes(bytes);
    }

    internal static void Forget(int slot)
    {
        if (slot >= 0 && slot < Formations.Length) Formations[slot] = null;
        if (slot == CoopNet.MySlot) _lastSent = null;
    }

    internal static void Prepare(GameSettingData s)
    {
        var layout = s.unitSettingData?.playerUnitLayoutParam;
        var troop = layout?.troopAddingParam;
        if (layout == null || troop == null) return;
        PutBackOwnLayout(layout); // in case a previous battle never got to give it back

        // Our own army exactly as the headquarters just wrote it.
        var own = Formation.Capture(layout);
        Formations[CoopNet.MySlot] = own;
        OwnUnitById.Clear();
        OwnFallen.Clear();
        Array.Clear(DepartedNames);

        var plan = new List<(int owner, Formation.Squad squad, int uid)>();
        for (int p = 0; p < Session.MaxPlayers; p++)
        {
            InBattle[p] = Session.Occupied(p);
            if (!InBattle[p]) continue;
            var f = Formations[p];
            if (f == null || f.Squads.Count == 0)
            {
                Session.Note($"no army received from {Session.Name(p)}; lending them a copy of yours", $"沒收到 {Session.Name(p)} 的部隊資料，先借他一份你的部隊");
                f = own;
            }
            for (int i = 0; i < f.Squads.Count; i++)
                plan.Add((p, f.Squads[i], p == 0 ? f.Squads[i].Id : RemoteSquadBase + 10 * p + i));
        }

        int units = 0;
        foreach (var e in plan) units += e.squad.Units.Count;

        // Build the co-op troop in copies of the layout entries; ours are put back afterwards.
        _ownSquads = layout.squadAddingParam;
        _ownUnits = layout.unitAddingParam;
        _ownSquadNum = troop.squadNum;
        layout.squadAddingParam = CopySquads(_ownSquads, plan.Count);
        layout.unitAddingParam = CopyUnits(_ownUnits, units);

        // Our units take the layout entries in squad order, so the n-th of them is entry n.
        int unit = 0, ownEntry = 0;
        for (int k = 0; k < plan.Count; k++)
        {
            var (owner, squad, uid) = plan[k];
            var dst = layout.squadAddingParam[k];
            Formation.WriteSquad(squad, dst, uid);
            dst.rsv1 = OwnerTag | owner;
            dst.posX = k * 20f; // one line, front to back in slot order
            bool ours = owner == CoopNet.MySlot && own.Squads.Contains(squad);
            for (int j = 0; j < squad.Units.Count; j++)
            {
                Formation.WriteUnit(squad.Units[j], layout.unitAddingParam[unit], UnitIdBase + unit);
                if (ours) OwnUnitById[UnitIdBase + unit] = ownEntry++;
                unit++;
            }
        }
        troop.squadNum = plan.Count;
        Session.Note($"troop: {plan.Count} squads, {units} units from {Session.PlayerCount} players", $"本場部隊：{Session.PlayerCount} 位玩家，共 {plan.Count} 隊、{units} 隻");
        for (int p = 0; p < Session.MaxPlayers; p++)
            if (Session.Occupied(p)) CoopPlugin.L.LogInfo($"[coop]   P{p + 1} {Session.Name(p)}: {Summary(p)}");
    }

    /// <summary>
    /// End of a co-op battle: put our own layout back, with the units we lost marked dead the way
    /// a single-player battle marks them. After a clear the camp copies the layout into the save
    /// (and brings the fallen back); after a failure it reloads the saved layout anyway.
    /// </summary>
    internal static void RestoreOwnLayout(GameSettingData s, P2.Game.Game? game)
    {
        var layout = s.unitSettingData?.playerUnitLayoutParam;
        var ownUnits = _ownUnits;
        if (layout?.troopAddingParam == null || ownUnits == null) return;

        // Units still standing in a squad that ended this very frame were not seen by the patch yet.
        var troops = game?.getUnitMng()?.unitTroopPtrArray_;
        if (troops != null && troops.Count > 0 && troops[0]?.unitSquadPtrList_ != null)
            foreach (var squad in troops[0].unitSquadPtrList_)
            {
                if (squad?.unitBasePtrList_ == null || squad.squadInfo_ == null || squad.squadInfo_.uniqueId >= StoryCompanions.CopyBase) continue;
                if (squad.squadInfo_.squadAddingParam?.rsv1 != (OwnerTag | CoopNet.MySlot)) continue;
                foreach (var u in squad.unitBasePtrList_)
                    if (u?.unitAddingParam_ != null && u.isEnd()) NoteEnded(u.unitAddingParam_.id);
            }

        int fallen = 0;
        foreach (int id in OwnFallen)
        {
            if (!OwnUnitById.TryGetValue(id, out int entry) || entry >= ownUnits.Length) continue;
            var d = ownUnits[entry];
            if (d == null || d.isLive == 0) continue;
            d.isLive = 0; // as UnitBase.update marks a fallen unit in a single-player battle
            d.deadCount++;
            fallen++;
        }
        PutBackOwnLayout(layout);
        Session.Note(fallen == 0 ? "your army came back complete" : $"{fallen} of your units fell in this battle",
            fallen == 0 ? "你的部隊全員平安歸來" : $"你的部隊這場倒下了 {fallen} 隻（過關後會在村莊復活）");
    }

    private static void PutBackOwnLayout(UnitLayoutParam layout)
    {
        if (_ownSquads == null || _ownUnits == null) return;
        layout.squadAddingParam = _ownSquads;
        layout.unitAddingParam = _ownUnits;
        layout.troopAddingParam.squadNum = _ownSquadNum;
        _ownSquads = null;
        _ownUnits = null;
        OwnUnitById.Clear();
        OwnFallen.Clear();
    }

    /// <summary>Copies of the layout entries (the game's own deep copy), at least <paramref name="count"/> of them.</summary>
    private static Il2CppReferenceArray<SquadAddingParam> CopySquads(Il2CppReferenceArray<SquadAddingParam> src, int count)
    {
        var arr = new Il2CppReferenceArray<SquadAddingParam>(Math.Max(count, src.Length));
        for (int i = 0; i < arr.Length; i++)
        {
            var copy = new SquadAddingParam();
            var from = src.Length > 0 ? src[Math.Min(i, src.Length - 1)] : null;
            if (from != null) copy.CopyFrom(ref from, false);
            arr[i] = copy;
        }
        return arr;
    }

    private static Il2CppReferenceArray<UnitAddingParam> CopyUnits(Il2CppReferenceArray<UnitAddingParam> src, int count)
    {
        var arr = new Il2CppReferenceArray<UnitAddingParam>(Math.Max(count, src.Length));
        for (int i = 0; i < arr.Length; i++)
        {
            var copy = new UnitAddingParam();
            var from = src.Length > 0 ? src[Math.Min(i, src.Length - 1)] : null;
            if (from != null) copy.CopyFrom(ref from, false);
            arr[i] = copy;
        }
        return arr;
    }
}

/// <summary>
/// A squad follows the drum commands of the player who owns it. Squads the mission adds itself
/// (the flag bearer, scripted reinforcements) carry no owner and follow the host.
/// </summary>
[HarmonyPatch(typeof(UnitSquad), nameof(UnitSquad.reset))]
internal static class SquadOwnerPatch
{
    private static void Postfix(UnitSquad __instance)
    {
        try
        {
            if (!Battle.Active) return;
            var troop = __instance.pUnitTroop_;
            if (troop?.troopInfo_ == null || (int)troop.troopInfo_.troopType != 0) return;
            int tag = __instance.squadInfo_?.squadAddingParam?.rsv1 ?? 0;
            int owner = (tag & unchecked((int)0xFFFF0000)) == Armies.OwnerTag ? tag & 0xFF : 0;
            var inst = troop.getInstructionParamA(Armies.Commander(owner));
            if (inst != null) __instance.pInstParam_ = inst;
        }
        catch (Exception e)
        {
            CoopPlugin.L.LogWarning("squad owner mapping failed: " + e.Message);
        }
    }
}

/// <summary>
/// In a single-player battle UnitBase.update marks a unit dead in the layout the frame it ends
/// (layout ids 0..23 only). Our co-op units have ids from 64, so we note our own fallen here and
/// mark them when the battle is over (they leave their squad soon after ending).
/// </summary>
[HarmonyPatch(typeof(UnitBase), nameof(UnitBase.update), new[] { typeof(uint) })]
internal static class OwnUnitEndPatch
{
    private static void Postfix(UnitBase __instance)
    {
        if (!Battle.Active || !Armies.HasOwnUnits) return;
        var p = __instance.unitAddingParam_;
        if (p == null || p.id < Armies.UnitIdBase || !__instance.isEnd()) return;
        var info = __instance.pUnitSquad_?.squadInfo_;
        // a story companion's copy carries its player's tag but is no part of their army
        if (info == null || info.uniqueId >= StoryCompanions.CopyBase) return;
        if (info.squadAddingParam?.rsv1 == (Armies.OwnerTag | CoopNet.MySlot)) Armies.NoteEnded(p.id);
    }
}

/// <summary>
/// A squad finds its units in the layout by counting the units of the squads before it, taking its
/// unique id as its place in the layout. The other players' squads have ids from
/// <see cref="Armies.RemoteSquadBase"/> (so they never meet a mission's own squads); count up to
/// where such a squad really is.
/// </summary>
[HarmonyPatch(typeof(UnitSquad), nameof(UnitSquad.getUnitParamIdOffset))]
internal static class RemoteSquadOffsetPatch
{
    private static bool Prefix(UnitLayoutParam pFileTop, int squadParamId, ref int __result)
    {
        if (!Battle.Active || squadParamId < Armies.RemoteSquadBase) return true;
        var squads = pFileTop?.squadAddingParam;
        if (squads == null) return true;
        int offset = 0;
        for (int k = 0; k < squads.Length; k++)
        {
            var s = squads[k];
            if (s == null) continue;
            if (s.id == squadParamId)
            {
                __result = offset;
                return false;
            }
            offset += s.initUnitNum != -1 ? s.initUnitNum : s.unitNum;
        }
        return true;
    }
}
