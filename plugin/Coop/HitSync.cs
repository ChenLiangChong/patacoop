using System;
using System.Collections.Generic;
using HarmonyLib;
using P2.Game.Unit;
using P2.GameSystem;
using P2.GameSystem.Actor.Status;
using PataCoop.Net;
using UnityEngine;

namespace PataCoop.Coop;

/// <summary>
/// Hits are decided by the host alone. Every machine still simulates the battle (so units swing,
/// shoot and flinch on their own screens), but on a guest no hit changes anybody's hit points and
/// no damage number appears by itself: the host sends every hit point change and every damage
/// number as they happen, and twice a second the hit points of everything on the field. So the
/// same numbers pop up everywhere, and a unit, enemy or wall falls on every screen at once.
/// </summary>
internal static class HitSync
{
    /// <summary>True while a guest applies the host's word (the only hit point changes and numbers it lets through).</summary>
    internal static bool Applying;

    private enum Kind : byte { Unit = 1, Gimmick = 2, Flag = 3 }

    private struct Hp { public Kind Kind; public int Troop, Squad, Id, Value; }
    private struct Fx { public byte Type; public uint Number; public Vector4 Pos; }

    private static readonly Dictionary<(Kind, int), Hp> PendingHp = new();
    private static readonly List<Fx> PendingFx = new();
    private static readonly List<(int id, float wait, float fade, bool force, bool broke)> PendingKills = new();
    private static readonly Dictionary<IntPtr, int> GimmickByStatus = new();
    private static int _nextSnapshot;
    private const byte KillForce = 1, KillBroke = 2;
    /// <summary>Hit points per snapshot packet (14 bytes each: about 1 KB, well inside one datagram).</summary>
    private const int SnapshotChunk = 70;

    /// <summary>Hit point changes / numbers sent (host) or applied (guest) this battle, for the panel.</summary>
    internal static int Sent, Applied, Unmatched;

    internal static void Reset()
    {
        PendingHp.Clear();
        PendingFx.Clear();
        PendingKills.Clear();
        GimmickByStatus.Clear();
        Breaking.Clear();
        BrokenHere.Clear();
        KilledHere.Clear();
        _eventOf = -1;
        Sent = Applied = Unmatched = 0;
    }

    internal static string Describe() => CoopNet.IsHost
        ? Text.T($"hits sent {Sent}", $"傷害事件 已送 {Sent}")
        : Text.T($"hits applied {Applied}", $"傷害事件 已套用 {Applied}") + (Unmatched > 0 ? Text.T($" ({Unmatched} unmatched)", $"（{Unmatched} 筆對不上）") : "");

    private static bool HostBattle => Battle.Active && CoopNet.IsHost && Session.CoopActive;
    internal static bool GuestBattle => Battle.Active && !CoopNet.IsHost;

    // ------------------------------------------------------------------ host: what happened

    internal static void HostHpChanged(GameStatus status)
    {
        if (!HostBattle) return;
        if (Identify(status) is { } hp) PendingHp[(hp.Kind, hp.Kind == Kind.Unit ? hp.Id : hp.Kind == Kind.Gimmick ? hp.Id : -1)] = hp;
    }

    internal static void HostHitEffect(HitEffectType type, uint number, Vector4 pos)
    {
        if (HostBattle) PendingFx.Add(new Fx { Type = (byte)type, Number = number, Pos = pos });
    }

    /// <summary>
    /// A gimmick goes away on the host. If that happens inside its own event script it broke (the
    /// fatal hit: breaking animation, drops); anywhere else the mission removed it whole.
    /// </summary>
    internal static void HostGimmickKilled(P2.Game.Gimmick.Gimmick gimmick, float wait, float fade, bool force)
    {
        if (HostBattle) PendingKills.Add(((int)gimmick.id_, wait, fade, force, _eventOf == (int)gimmick.id_));
    }

    /// <summary>The gimmick whose event script is running (-1: none).</summary>
    private static int _eventOf = -1;

    internal static int EventStarted(int id)
    {
        int outer = _eventOf;
        _eventOf = id;
        return outer;
    }

    internal static void EventEnded(int id, int outer)
    {
        _eventOf = outer;
        // a guest's own simulation ran the event of a gimmick at zero hit points: it broke here
        if (GuestBattle && (FindGimmick(id)?.status_?.getHitPoint() ?? 1) <= 0) BrokenHere.Add(id);
    }

    /// <summary>Guest: a gimmick was removed here (its own script or ours), so it needs no more help.</summary>
    internal static void KilledLocally(P2.Game.Gimmick.Gimmick gimmick) => KilledHere.Add((int)gimmick.id_);

    private static Hp? Identify(GameStatus status)
    {
        try
        {
            int value = status.getHitPoint();
            var actor = status.TryCast<ActorStatusObj>()?.pActorObj_;
            if (actor != null)
            {
                if (actor.TryCast<UnitBase>() is { } unit)
                {
                    var squad = unit.pUnitSquad_;
                    return new Hp
                    {
                        Kind = Kind.Unit, Troop = (int)(squad?.pUnitTroop_?.troopInfo_?.troopType ?? 0), Squad = squad?.squadInfo_?.uniqueId ?? -1,
                        Id = unit.info_?.uniqueId ?? -1, Value = value,
                    };
                }
                if (actor.TryCast<FlagUnit>() != null) return new Hp { Kind = Kind.Flag, Value = value };
            }
            if (GimmickId(status) is int gid) return new Hp { Kind = Kind.Gimmick, Id = gid, Value = value };
        }
        catch
        {
            // something without an identity of its own: not shared
        }
        return null;
    }

    private static int? GimmickId(GameStatus status)
    {
        if (GimmickByStatus.TryGetValue(status.Pointer, out int id)) return id;
        var list = P2.Game.Game.pGame_g?.map_?.gimmickManager_?.gimmickList_;
        if (list == null) return null;
        foreach (var g in list)
            if (g?.status_ != null) GimmickByStatus[g.status_.Pointer] = (int)g.id_;
        return GimmickByStatus.TryGetValue(status.Pointer, out id) ? id : null;
    }

    internal static void Tick()
    {
        if (GuestBattle && Breaking.Count > 0) FinishBreaking();
        if (!HostBattle) return;
        if (PendingHp.Count > 0 || PendingFx.Count > 0 || PendingKills.Count > 0)
        {
            var w = new MsgWriter(Msg.Hits);
            WriteHp(w, PendingHp.Values);
            w.U16((ushort)PendingFx.Count);
            foreach (var fx in PendingFx) w.U8(fx.Type).U32(fx.Number).F32(fx.Pos.x).F32(fx.Pos.y).F32(fx.Pos.z).F32(fx.Pos.w);
            w.U16((ushort)PendingKills.Count);
            foreach (var k in PendingKills) w.I32(k.id).F32(k.wait).F32(k.fade).U8((byte)((k.force ? KillForce : 0) | (k.broke ? KillBroke : 0)));
            CoopNet.SendAll(w.ToArray(), true);
            Sent += PendingHp.Count + PendingFx.Count + PendingKills.Count;
            PendingHp.Clear();
            PendingFx.Clear();
            PendingKills.Clear();
        }
        if (Time.frameCount >= _nextSnapshot)
        {
            _nextSnapshot = Time.frameCount + 30;
            // unreliable packets must fit one datagram: a full four-player field goes out in parts
            var all = Everything();
            for (int i = 0; i < all.Count; i += SnapshotChunk)
                CoopNet.SendAll(WriteHp(new MsgWriter(Msg.HitPoints), all.GetRange(i, Math.Min(SnapshotChunk, all.Count - i))).ToArray(), false);
        }
    }

    private static MsgWriter WriteHp(MsgWriter w, ICollection<Hp> list)
    {
        w.U16((ushort)list.Count);
        foreach (var hp in list) w.U8((byte)hp.Kind).U8((byte)hp.Troop).I32(hp.Squad).I32(hp.Id).I32(hp.Value);
        return w;
    }

    /// <summary>Hit points of every unit, flag bearer and gimmick on the field (the host's snapshot).</summary>
    private static List<Hp> Everything()
    {
        var list = new List<Hp>();
        var game = P2.Game.Game.pGame_g;
        var troops = game?.getUnitMng()?.unitTroopPtrArray_;
        if (troops != null)
            for (int t = 0; t < troops.Count; t++)
            {
                var troop = troops[t];
                if (troop?.unitSquadPtrList_ == null) continue;
                foreach (var squad in troop.unitSquadPtrList_)
                {
                    if (squad?.unitBasePtrList_ == null) continue;
                    foreach (var u in squad.unitBasePtrList_)
                        if (u?.pActorStatus_ != null && u.info_ != null)
                            list.Add(new Hp { Kind = Kind.Unit, Troop = (int)troop.troopInfo_.troopType, Squad = squad.squadInfo_?.uniqueId ?? -1, Id = u.info_.uniqueId, Value = u.pActorStatus_.getHitPoint() });
                }
                if (t == 0 && troop.troopCtrl_?.flagUnit_?.pActorStatus_ is { } flag) list.Add(new Hp { Kind = Kind.Flag, Value = flag.getHitPoint() });
            }
        var gimmicks = game?.map_?.gimmickManager_?.gimmickList_;
        if (gimmicks != null)
            foreach (var g in gimmicks)
                if (g?.status_ != null && g.isEnable_) list.Add(new Hp { Kind = Kind.Gimmick, Id = (int)g.id_, Value = g.status_.getHitPoint() });
        return list;
    }

    // ------------------------------------------------------------------ guest: the host's word

    internal static void OnHits(int fromSlot, MsgReader r)
    {
        if (fromSlot != 0 || !GuestBattle) return;
        ApplyHp(r);
        int fx = r.U16();
        var player = P2.Game.Game.pGame_g?.getParticlePlayer(1);
        for (int i = 0; i < fx; i++)
        {
            byte type = r.U8();
            uint number = r.U32();
            var pos = new Vector4(r.F32(), r.F32(), r.F32(), r.F32());
            if (player == null) continue;
            Applying = true;
            try { player.playHitEffect((HitEffectType)type, number, ref pos); }
            catch (Exception e) { CoopPlugin.L.LogWarning("could not show the host's hit: " + e.Message); }
            finally { Applying = false; }
        }
        int kills = r.U16();
        for (int i = 0; i < kills; i++)
        {
            int id = r.I32();
            float wait = r.F32(), fade = r.F32();
            byte how = r.U8();
            var gimmick = FindGimmick(id);
            if (gimmick == null || !gimmick.isEnable_) { if (gimmick == null) Unmatched++; continue; }
            Break(gimmick, wait, fade, (how & KillForce) != 0, (how & KillBroke) != 0);
        }
    }

    /// <summary>Gimmicks the host broke that are still standing here, and when to stop waiting for their own script.</summary>
    private static readonly List<(int id, float wait, float fade, bool force, int deadline)> Breaking = new();
    /// <summary>Guest: gimmicks whose breaking already ran here, and gimmicks already removed here.</summary>
    private static readonly HashSet<int> BrokenHere = new(), KilledHere = new();
    /// <summary>How long a broken gimmick's own script gets to remove it (its breaking animation plays first).</summary>
    private const int BreakingFrames = 600;

    /// <summary>
    /// The host broke or removed a gimmick. A removal (by the mission) is copied as it is. A break is
    /// played here the way the game's own multiplayer plays it on a client when the host reports a
    /// broken gimmick (PD_KillGimmick): hit points to zero, marked as hit by the players, then its
    /// event script, which plays the breaking animation, drops whatever it drops (our own roll) and
    /// removes it. If our own simulation has broken it already, nothing more is needed.
    /// </summary>
    private static void Break(P2.Game.Gimmick.Gimmick gimmick, float wait, float fade, bool force, bool broke)
    {
        int id = (int)gimmick.id_;
        // already removed here (a breaking gimmick takes its parts with it), or already broken here
        if (KilledHere.Contains(id) || (broke && BrokenHere.Contains(id))) return;
        Applying = true;
        try
        {
            if (broke)
            {
                var status = gimmick.status_;
                int hp = status?.getHitPoint() ?? 0;
                if (hp > 0) status!.addHitPoint(-hp);
                gimmick.prevTarget_ = 1;
                gimmick.eventScript_?.execute(id, gimmick.eventType_);
                Breaking.Add((id, wait, fade, force, Time.frameCount + BreakingFrames));
            }
            else if (force) gimmick.forceKill();
            else gimmick.kill(wait, fade);
            Applied++;
        }
        catch (Exception e) { CoopPlugin.L.LogWarning("could not break the host's gimmick: " + e.Message); }
        finally { Applying = false; }
    }

    /// <summary>Guest: remove a broken gimmick its own script did not remove in time.</summary>
    private static void FinishBreaking()
    {
        for (int i = Breaking.Count - 1; i >= 0; i--)
        {
            var b = Breaking[i];
            if (KilledHere.Contains(b.id)) { Breaking.RemoveAt(i); continue; }
            if (Time.frameCount < b.deadline) continue;
            Breaking.RemoveAt(i);
            var gimmick = FindGimmick(b.id);
            if (gimmick == null || !gimmick.isEnable_) continue;
            CoopPlugin.L.LogWarning($"[coop] gimmick {b.id} did not remove itself after breaking: removing it");
            Applying = true;
            try { if (b.force) gimmick.forceKill(); else gimmick.kill(b.wait, b.fade); }
            catch (Exception e) { CoopPlugin.L.LogWarning("could not remove the host's broken gimmick: " + e.Message); }
            finally { Applying = false; }
        }
    }

    private static P2.Game.Gimmick.Gimmick? FindGimmick(int id)
    {
        var gimmicks = P2.Game.Game.pGame_g?.map_?.gimmickManager_?.gimmickList_;
        if (gimmicks != null)
            foreach (var g in gimmicks)
                if (g != null && (int)g.id_ == id) return g;
        return null;
    }

    internal static void OnHitPoints(int fromSlot, MsgReader r)
    {
        if (fromSlot == 0 && GuestBattle) ApplyHp(r);
    }

    private static void ApplyHp(MsgReader r)
    {
        int n = r.U16();
        if (n == 0) return;
        var game = P2.Game.Game.pGame_g;
        Dictionary<int, UnitBase>? units = null;
        for (int i = 0; i < n; i++)
        {
            var hp = new Hp { Kind = (Kind)r.U8(), Troop = r.U8(), Squad = r.I32(), Id = r.I32(), Value = r.I32() };
            GameStatus? status = null;
            switch (hp.Kind)
            {
                case Kind.Unit:
                    units ??= UnitsById(game);
                    if (units.TryGetValue(hp.Id, out var unit)
                        && (int)(unit.pUnitSquad_?.pUnitTroop_?.troopInfo_?.troopType ?? (TroopType)(-1)) == hp.Troop
                        && unit.pUnitSquad_?.squadInfo_?.uniqueId == hp.Squad)
                        status = unit.pActorStatus_;
                    break;
                case Kind.Flag:
                    status = game?.getUnitMng()?.unitTroopPtrArray_?[0]?.troopCtrl_?.flagUnit_?.pActorStatus_;
                    break;
                case Kind.Gimmick:
                    status = FindGimmick(hp.Id)?.status_;
                    break;
            }
            if (status == null) { Unmatched++; continue; }
            int delta = hp.Value - status.getHitPoint();
            if (delta == 0) continue;
            Applying = true;
            try { status.addHitPoint(delta); Applied++; }
            catch (Exception e) { CoopPlugin.L.LogWarning("could not apply the host's hit points: " + e.Message); }
            finally { Applying = false; }
        }
    }

    private static Dictionary<int, UnitBase> UnitsById(P2.Game.Game? game)
    {
        var map = new Dictionary<int, UnitBase>();
        var troops = game?.getUnitMng()?.unitTroopPtrArray_;
        if (troops == null) return map;
        foreach (var troop in troops)
        {
            if (troop?.unitSquadPtrList_ == null) continue;
            foreach (var squad in troop.unitSquadPtrList_)
            {
                if (squad?.unitBasePtrList_ == null) continue;
                foreach (var u in squad.unitBasePtrList_)
                    if (u?.info_ != null) map[u.info_.uniqueId] = u;
            }
        }
        return map;
    }
}

/// <summary>
/// Host: report hit point changes. Guest: only the host's changes go through.
///
/// The multiplayer engine we borrow treats player squads 0-3 as players 1-4's own squads and
/// ignores any hit on a squad another player controls ("other control": its owner sends its hit
/// points). In co-op the host decides every hit point, and squads 0-3 are simply the host's first
/// squads or a story squad, so that rule would make them invincible on the host (a story squad
/// the mission kills would never die) and drop the host's numbers for them on the guests.
/// </summary>
[HarmonyPatch(typeof(GameStatus), nameof(GameStatus.addHitPoint))]
internal static class HitPointsChangePatch
{
    [HarmonyPriority(Priority.First)]
    private static bool Prefix(GameStatus __instance)
    {
        if (HitSync.GuestBattle && !HitSync.Applying) return false;
        // the flag is read only here (the engine sets it again every frame)
        if (Battle.Active && __instance.info_ is { isNoDamageOC: true } info) info.isNoDamageOC = false;
        return true;
    }

    private static void Postfix(GameStatus __instance, bool __runOriginal)
    {
        if (__runOriginal) HitSync.HostHpChanged(__instance);
    }
}

[HarmonyPatch(typeof(GameStatus), nameof(GameStatus.setHitPoint))]
internal static class HitPointsSetPatch
{
    private static void Postfix(GameStatus __instance) => HitSync.HostHpChanged(__instance);
}

/// <summary>Host: report damage numbers. Guest: only the host's numbers appear.</summary>
[HarmonyPatch(typeof(ParticlePlayer), nameof(ParticlePlayer.playHitEffect))]
internal static class HitNumberPatch
{
    private static bool Prefix(HitEffectType type, uint number, ref Vector4 trans)
    {
        if (HitSync.GuestBattle) return HitSync.Applying;
        HitSync.HostHitEffect(type, number, trans);
        return true;
    }
}

/// <summary>Host: report a gimmick breaking. Guest: gimmicks break only when the host's do.</summary>
[HarmonyPatch(typeof(P2.Game.Gimmick.Gimmick), nameof(P2.Game.Gimmick.Gimmick.kill))]
internal static class GimmickKillPatch
{
    private static bool Prefix(P2.Game.Gimmick.Gimmick __instance, float waitTime, float fadeTime)
    {
        // a guest's own script may remove a gimmick only once the host has broken it (zero hit points)
        if (HitSync.GuestBattle) return HitSync.Applying || (__instance.status_?.getHitPoint() ?? 1) <= 0;
        HitSync.HostGimmickKilled(__instance, waitTime, fadeTime, false);
        return true;
    }

    private static void Postfix(P2.Game.Gimmick.Gimmick __instance, bool __runOriginal)
    {
        if (__runOriginal && HitSync.GuestBattle) HitSync.KilledLocally(__instance);
    }
}

[HarmonyPatch(typeof(P2.Game.Gimmick.Gimmick), nameof(P2.Game.Gimmick.Gimmick.forceKill))]
internal static class GimmickForceKillPatch
{
    private static bool Prefix(P2.Game.Gimmick.Gimmick __instance)
    {
        if (HitSync.GuestBattle) return HitSync.Applying || (__instance.status_?.getHitPoint() ?? 1) <= 0;
        HitSync.HostGimmickKilled(__instance, 0f, 0f, true);
        return true;
    }

    private static void Postfix(P2.Game.Gimmick.Gimmick __instance, bool __runOriginal)
    {
        if (__runOriginal && HitSync.GuestBattle) HitSync.KilledLocally(__instance);
    }
}

/// <summary>
/// Which gimmick's event script is running: a removal inside it is that gimmick breaking. Only the
/// gimmick's own event counts; area triggers (EBox) and init scripts have their own numbering.
/// </summary>
[HarmonyPatch(typeof(P2.Game.Map.Event.Script), nameof(P2.Game.Map.Event.Script.execute))]
internal static class GimmickEventPatch
{
    private const int Untracked = int.MinValue;

    private static void Prefix(int __0, P2.Game.Map.EventType __1, out int __state) =>
        __state = Battle.Active && __1 == P2.Game.Map.EventType.EventType_Gimmick ? HitSync.EventStarted(__0) : Untracked;

    private static void Postfix(int __0, int __state)
    {
        if (__state != Untracked && Battle.Active) HitSync.EventEnded(__0, __state);
    }
}
