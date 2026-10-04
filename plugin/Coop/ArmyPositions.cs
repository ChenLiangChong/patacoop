using System;
using System.Collections.Generic;
using HarmonyLib;
using P2.Game.Unit;
using PataCoop.Net;
using UnityEngine;

namespace PataCoop.Coop;

/// <summary>
/// Every player's army walks on its own player's drums, as in a single-player battle. On each
/// machine the troop's base position is the local player's army, so the camera, the flag bearer,
/// walls and enemies stopping the march and the goal all work as they do alone. The other players'
/// armies are where their owners say they are: every machine sends where its army and each of its
/// units stand every frame. While another player's squad is updated, the troop's base and flag
/// bearer stand at that player's army (so its orders and ranges are measured from there), and
/// afterwards its units are put where their owner says they are.
/// </summary>
internal static class ArmyPositions
{
    /// <summary>Further than this from where we show it, an army jumps instead of gliding.</summary>
    private const float SnapDistance = 200f;
    /// <summary>Share of the remaining distance a shown army covers each frame (smooths uneven arrival).</summary>
    private const float Ease = 0.5f;
    /// <summary>
    /// Unreliable messages must fit one UDP packet, and LiteNetLib starts from about 500 bytes until
    /// it finds the path's real limit: at most this many units or enemies (7 bytes each) go in one
    /// message. With more than that, each frame sends the next batch.
    /// </summary>
    private const int MaxUnitsPerMessage = 60, MaxEnemiesPerMessage = 60;
    private static int _unitCursor;
    private static int _enemyCursor;

    private static readonly float[] Shown = new float[Session.MaxPlayers];
    private static readonly float[] Reported = new float[Session.MaxPlayers];
    /// <summary>
    /// Other players' units by squad id and place (see <see cref="UnitIds"/>; the same on every
    /// machine, and unlike layout ids also for every player's copy of a story companion): where they
    /// were reported and where we show them.
    /// </summary>
    private static readonly Dictionary<(int Squad, int Place), (float Reported, float Shown)> Units = new();
    private static readonly List<(int Squad, int Place, float X)> Ours = new();
    /// <summary>Guests: the host's enemy units by squad id and place (see <see cref="UnitIds"/>): reported and shown x.</summary>
    private static readonly Dictionary<(int Squad, int Place), (float Reported, float Shown)> Enemies = new();
    private static readonly List<(int Squad, int Place, float X)> TheirEnemies = new();
    private static int _frame = -1;

    internal static void Reset()
    {
        Array.Fill(Shown, float.NaN);
        Array.Fill(Reported, float.NaN);
        Units.Clear();
        Enemies.Clear();
        _frame = -1;
    }

    /// <summary>How far the army furthest ahead (ours or another player's) is ahead of ours (0: ours leads).</summary>
    internal static float LeadOverUs(float ourBase)
    {
        float lead = 0;
        for (int p = 0; p < Shown.Length; p++)
            if (p != CoopNet.MySlot && Session.Occupied(p) && !float.IsNaN(Shown[p])) lead = Math.Max(lead, Shown[p] - ourBase);
        return lead;
    }

    /// <summary>Where this player's army is shown on this machine (NaN: not heard from yet).</summary>
    internal static float Of(int player) => player >= 0 && player < Shown.Length ? Shown[player] : float.NaN;

    /// <summary>Once per frame during a co-op battle: send where our army is, glide the others towards their reports.</summary>
    internal static void Tick()
    {
        var game = P2.Game.Game.pGame_g;
        if (!Battle.Active || game == null || game.gamePhase_ != P2.Game.Game.GamePhase.GamePhase_Play) return;
        int frame = Time.frameCount;
        if (frame == _frame) return;
        _frame = frame;
        var troop = game.getUnitMng()?.unitTroopPtrArray_?[0];
        var pos = troop?.troopBasePos_;
        if (troop == null || pos == null || pos.Length == 0) return;
        Send(troop, pos[0]);
        if (CoopNet.IsHost) SendEnemies(game);
        for (int p = 0; p < Session.MaxPlayers; p++)
        {
            float reported = Reported[p], shown = Shown[p];
            if (float.IsNaN(reported)) continue;
            Shown[p] = Glide(shown, reported);
        }
        foreach (var key in new List<(int, int)>(Units.Keys))
        {
            var u = Units[key];
            Units[key] = (u.Reported, Glide(u.Shown, u.Reported));
        }
        foreach (var key in new List<(int, int)>(Enemies.Keys))
        {
            var u = Enemies[key];
            Enemies[key] = (u.Reported, Glide(u.Shown, u.Reported));
        }
    }

    /// <summary>Host: where every enemy unit stands, by squad id and place (the same on every machine).</summary>
    private static void SendEnemies(P2.Game.Game game)
    {
        var troops = game.getUnitMng()?.unitTroopPtrArray_;
        if (troops == null || troops.Count < 2 || troops[1]?.unitSquadPtrList_ == null) return;
        TheirEnemies.Clear();
        foreach (var squad in troops[1].unitSquadPtrList_)
        {
            if (squad?.unitBasePtrList_ == null || squad.squadInfo_ == null) continue;
            foreach (var unit in squad.unitBasePtrList_)
            {
                if (unit?.info_ == null || unit.isEnd()) continue;
                var model = unit.pActorModel_?.TryCast<UnitModel>();
                int place = UnitIds.PlaceOf(unit);
                if (model != null && place >= 0) TheirEnemies.Add((squad.squadInfo_.uniqueId, place, model.pos_.x));
            }
        }
        int total = TheirEnemies.Count;
        if (total == 0) return;
        int count = Math.Min(total, MaxEnemiesPerMessage);
        if (_enemyCursor >= total) _enemyCursor = 0;
        var w = new MsgWriter(Msg.EnemyPos).U8((byte)count);
        for (int k = 0; k < count; k++)
        {
            var e = TheirEnemies[(_enemyCursor + k) % total];
            w.U16((ushort)e.Squad).U8((byte)e.Place).F32(e.X);
        }
        _enemyCursor = (_enemyCursor + count) % total;
        CoopNet.SendAll(w.ToArray(), false);
    }

    internal static void OnEnemyPos(int fromSlot, MsgReader r)
    {
        if (fromSlot != 0 || !HitSync.GuestBattle) return;
        int count = r.U8();
        for (int i = 0; i < count; i++)
        {
            int squad = r.U16(), place = r.U8();
            float x = r.F32();
            if (float.IsNaN(x)) continue;
            Enemies[(squad, place)] = Enemies.TryGetValue((squad, place), out var u) ? (x, u.Shown) : (x, float.NaN);
        }
    }

    /// <summary>Guests: put the enemy units of a squad where the host has them.</summary>
    internal static void PlaceEnemies(UnitSquad squad)
    {
        if (squad.unitBasePtrList_ == null || squad.squadInfo_ == null || Enemies.Count == 0) return;
        foreach (var unit in squad.unitBasePtrList_)
        {
            if (unit?.info_ == null || unit.isEnd()) continue;
            if (!Enemies.TryGetValue((squad.squadInfo_.uniqueId, UnitIds.PlaceOf(unit)), out var u)) continue;
            var model = unit.pActorModel_?.TryCast<UnitModel>();
            if (model == null) continue;
            var p = model.pos_;
            p.x = float.IsNaN(u.Shown) ? u.Reported : u.Shown;
            model.pos_ = p;
        }
    }

    private static float Glide(float shown, float reported) =>
        float.IsNaN(shown) || Math.Abs(reported - shown) > SnapDistance ? reported : shown + (reported - shown) * Ease;

    /// <summary>Our army's base and every unit of ours still standing.</summary>
    private static void Send(UnitTroop troop, float baseX)
    {
        Ours.Clear();
        foreach (var squad in troop.unitSquadPtrList_)
        {
            if (squad?.unitBasePtrList_ == null || squad.squadInfo_ == null || ArmyOf(squad) != CoopNet.MySlot) continue;
            foreach (var unit in squad.unitBasePtrList_)
            {
                if (unit == null || unit.isEnd()) continue;
                var model = unit.pActorModel_?.TryCast<UnitModel>();
                int place = UnitIds.PlaceOf(unit);
                if (model != null && place >= 0) Ours.Add((squad.squadInfo_.uniqueId, place, model.pos_.x));
            }
        }
        int total = Ours.Count, units = Math.Min(total, MaxUnitsPerMessage);
        if (_unitCursor >= total) _unitCursor = 0;
        var w = new MsgWriter(Msg.ArmyPos).F32(baseX).U8((byte)units);
        for (int k = 0; k < units; k++)
        {
            var u = Ours[(_unitCursor + k) % total];
            w.U16((ushort)u.Squad).U8((byte)u.Place).F32(u.X);
        }
        if (total > 0) _unitCursor = (_unitCursor + units) % total;
        CoopNet.SendAll(w.ToArray(), false);
    }

    internal static void OnArmyPos(int fromSlot, MsgReader r)
    {
        float x = r.F32();
        if (fromSlot < 0 || fromSlot >= Reported.Length || fromSlot == CoopNet.MySlot || float.IsNaN(x)) return;
        Reported[fromSlot] = x;
        int count = r.U8();
        for (int i = 0; i < count; i++)
        {
            int squad = r.U16(), place = r.U8();
            float ux = r.F32();
            if (float.IsNaN(ux)) continue;
            Units[(squad, place)] = Units.TryGetValue((squad, place), out var u) ? (ux, u.Shown) : (ux, float.NaN);
        }
    }

    /// <summary>Put another player's units where their owner says they are.</summary>
    internal static void PlaceUnits(UnitSquad squad)
    {
        if (squad.unitBasePtrList_ == null || squad.squadInfo_ == null) return;
        foreach (var unit in squad.unitBasePtrList_)
        {
            if (unit == null || unit.isEnd()) continue;
            if (!Units.TryGetValue((squad.squadInfo_.uniqueId, UnitIds.PlaceOf(unit)), out var u)) continue;
            float x = float.IsNaN(u.Shown) ? u.Reported : u.Shown;
            var model = unit.pActorModel_?.TryCast<UnitModel>();
            if (model == null) continue;
            var p = model.pos_;
            p.x = x;
            model.pos_ = p;
        }
    }

    /// <summary>The army a squad belongs on: its owner's, or whoever commands it now that they left (-1: the troop's own).</summary>
    internal static int ArmyOf(UnitSquad squad)
    {
        int tag = squad.squadInfo_?.squadAddingParam?.rsv1 ?? 0;
        if ((tag & unchecked((int)0xFFFF0000)) != Armies.OwnerTag) return -1; // the mission's own squads
        return Armies.Commander(tag & 0xFF);
    }
}

/// <summary>
/// While another player's squad is updated (its orders, formation place and units), the troop's
/// base and its flag bearer stand where that player's army is: squads measure their place and their
/// attack range from both.
/// </summary>
[HarmonyPatch(typeof(UnitSquad), nameof(UnitSquad.update))]
internal static class SquadFollowsItsArmyPatch
{
    private const int EnemyTroop = 1;

    internal struct Moved
    {
        public bool Done, Enemy;
        public float Base, Flag;
    }

    private static void Prefix(UnitSquad __instance, out Moved __state)
    {
        __state = default;
        if (!Battle.Active) return;
        UnitIds.Note(__instance);
        var troop = __instance.pUnitTroop_;
        if (troop?.troopInfo_ == null) return;
        if ((int)troop.troopInfo_.troopType == EnemyTroop)
        {
            __state.Enemy = HitSync.GuestBattle;
            return;
        }
        if ((int)troop.troopInfo_.troopType != 0) return;
        int army = ArmyPositions.ArmyOf(__instance);
        if (army < 0 || army == CoopNet.MySlot) return;
        float x = ArmyPositions.Of(army);
        var pos = troop.troopBasePos_;
        if (float.IsNaN(x) || pos == null || pos.Length == 0) return;
        __state.Done = true;
        __state.Base = pos[0];
        float shift = x - pos[0];
        pos[0] = x;
        if (troop.troopCtrl_?.flagUnit_?.flagUnitModel_ is { } flag)
        {
            var f = flag.pos_;
            __state.Flag = f.x;
            f.x += shift;
            flag.pos_ = f;
        }
        else __state.Flag = float.NaN;
    }

    private static Exception? Finalizer(UnitSquad __instance, Moved __state, Exception? __exception)
    {
        if (__state.Enemy)
        {
            try { ArmyPositions.PlaceEnemies(__instance); }
            catch (Exception e) { CoopPlugin.L.LogDebug("could not place the host's enemies: " + e.Message); }
            return __exception;
        }
        if (!__state.Done) return __exception;
        try { ArmyPositions.PlaceUnits(__instance); }
        catch (Exception e) { CoopPlugin.L.LogDebug("could not place another player's units: " + e.Message); }
        var troop = __instance.pUnitTroop_;
        var pos = troop?.troopBasePos_;
        if (pos != null && pos.Length > 0) pos[0] = __state.Base;
        if (!float.IsNaN(__state.Flag) && troop?.troopCtrl_?.flagUnit_?.flagUnitModel_ is { } flag)
        {
            var f = flag.pos_;
            f.x = __state.Flag;
            flag.pos_ = f;
        }
        return __exception;
    }
}

/// <summary>
/// Mission scripts start events (enemy arrivals, story scenes) by checking how far the flag bearer
/// has come. Every machine's flag bearer is its own army's, so while a script reads it, it stands
/// with the army furthest ahead: every machine starts each event when the first army gets there,
/// and they all spawn the same enemies. (The front unit, the scripts' other measure, already counts
/// every army: their units are all in the one troop.)
/// </summary>
[HarmonyPatch]
internal static class ScriptsSeeTheLeadPatch
{
    private static IEnumerable<System.Reflection.MethodBase> TargetMethods()
    {
        foreach (var name in new[] { nameof(P2.Game.Talk.CommandGame.getFlagUnitPosX), nameof(P2.Game.Talk.CommandGame.getFlagUnitPos) })
            if (AccessTools.Method(typeof(P2.Game.Talk.CommandGame), name) is { } m) yield return m;
    }

    /// <summary>The last moved read: how far, where the flag bearer was shown, and in which frame.</summary>
    internal static float LastLead, LastShownAt;
    internal static int LastFrame = -1;

    private static void Prefix(out float __state)
    {
        __state = float.NaN;
        if (!Battle.Active) return;
        var troop = P2.Game.Game.pGame_g?.getUnitMng()?.unitTroopPtrArray_?[0];
        var flag = troop?.troopCtrl_?.flagUnit_?.flagUnitModel_;
        var pos = troop?.troopBasePos_;
        if (flag == null || pos == null || pos.Length == 0) return;
        float lead = ArmyPositions.LeadOverUs(pos[0]);
        if (lead <= 0) return;
        var f = flag.pos_;
        __state = f.x;
        f.x += lead;
        flag.pos_ = f;
        LastLead = lead;
        LastShownAt = f.x;
        LastFrame = Time.frameCount;
    }

    private static Exception? Finalizer(float __state, Exception? __exception)
    {
        if (float.IsNaN(__state)) return __exception;
        var flag = P2.Game.Game.pGame_g?.getUnitMng()?.unitTroopPtrArray_?[0]?.troopCtrl_?.flagUnit_?.flagUnitModel_;
        if (flag != null)
        {
            var f = flag.pos_;
            f.x = __state;
            flag.pos_ = f;
        }
        return __exception;
    }
}

/// <summary>
/// Boss missions take the camera over from their scripts (setCameraX), placing it from where the
/// flag bearer stands. Scripts are shown the army furthest ahead (<see cref="ScriptsSeeTheLeadPatch"/>),
/// so every player's camera followed that army and an army behind it went off screen. A camera
/// place set in the same frame as a moved read of the flag bearer, and near where it was shown, is
/// moved back by as much: each player's camera stays with their own army. Places far from it (a
/// look at the boss) stay as the script set them.
/// </summary>
[HarmonyPatch(typeof(P2.Game.Talk.CommandGame), nameof(P2.Game.Talk.CommandGame.setCameraX))]
internal static class ScriptCameraPatch
{
    /// <summary>How far from the moved flag bearer a script's camera place still counts as following it.</summary>
    private const float Near = 600;

    internal static bool Running;

    private static void Prefix() => Running = true;

    private static Exception? Finalizer(Exception? __exception)
    {
        Running = false;
        return __exception;
    }

    /// <summary>The script's camera place, for this player's own army.</summary>
    internal static float Own(float x)
    {
        if (!Running || !Battle.Active || ScriptsSeeTheLeadPatch.LastFrame != Time.frameCount) return x;
        return Math.Abs(x - ScriptsSeeTheLeadPatch.LastShownAt) < Near ? x - ScriptsSeeTheLeadPatch.LastLead : x;
    }
}

[HarmonyPatch(typeof(P2.Game.TrackingCamera), nameof(P2.Game.TrackingCamera.setX))]
internal static class ScriptCameraPlacePatch
{
    private static void Prefix(ref float x) => x = ScriptCameraPatch.Own(x);
}

/// <summary>
/// A mission brings its enemy squads on as the player's army passes their places
/// (UnitTroop.squadAddingCheck reads our troop's base). Every machine's base is its own army, so for
/// that check it stands with the army furthest ahead: every machine brings the same squads on in
/// the same order, and their ids (which hit points and kills are matched by) stay the same everywhere.
/// </summary>
[HarmonyPatch(typeof(UnitTroop), nameof(UnitTroop.squadAddingCheck))]
internal static class SquadsComeForTheLeadPatch
{
    private static void Prefix(out float __state)
    {
        __state = float.NaN;
        if (!Battle.Active) return;
        var pos = P2.Game.Game.pGame_g?.getUnitMng()?.unitTroopPtrArray_?[0]?.troopBasePos_;
        if (pos == null || pos.Length == 0) return;
        float lead = ArmyPositions.LeadOverUs(pos[0]);
        if (lead <= 0) return;
        __state = pos[0];
        pos[0] += lead;
    }

    private static Exception? Finalizer(float __state, Exception? __exception)
    {
        if (!float.IsNaN(__state))
        {
            var pos = P2.Game.Game.pGame_g?.getUnitMng()?.unitTroopPtrArray_?[0]?.troopBasePos_;
            if (pos != null && pos.Length > 0) pos[0] = __state;
        }
        return __exception;
    }
}
