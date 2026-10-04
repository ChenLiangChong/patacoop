using System;
using System.Collections.Generic;
using HarmonyLib;
using P2.Game.Unit;
using PataCoop.Net;
using UnityEngine;

namespace PataCoop.Coop;

/// <summary>
/// Which enemy squads are on the field is the host's call, and every guest's field follows it.
///
/// A mission brings its enemy squads in from a list of candidates (UnitTroop.addingDataList_): as the
/// army passes them, or when its script names one (squadAddingCheck with that squad's id). A hunting
/// ground's script picks its herds that way at the battle's first step, with random rolls whose number
/// depends on how long each machine waited for the others before the opening, so machines with the
/// same candidates brought in different herds. Every machine still runs its own script (its commands
/// find the squads it made). A guest then brings in, the game's own way, each squad the host has and
/// it lacks, and takes away, without a death or drops, each squad of its own the host has not had for
/// a while. The host reports its living enemies every frame (<see cref="ArmyPositions"/>).
///
/// Only at the battle's start: later squads (a fortress's waves) come in on every machine by the
/// army furthest ahead, a moment apart, and a squad brought in late from the host's report would come
/// whole while the host's had already lost units.
/// </summary>
internal static class EnemyRoster
{
    private const int EnemyTroop = 1;
    /// <summary>How long the fields may differ before a squad is brought in or taken away (1.5 s).</summary>
    private const int GraceFrames = 90;
    private const int CheckEvery = 15;
    /// <summary>How long after the battle's first step the fields are matched (10 s).</summary>
    private const int OpeningFrames = 600;
    /// <summary>A place no army reaches: squadAddingCheck then brings in only the squad it is given.</summary>
    private const float Nowhere = -1000000f;

    /// <summary>Guest: when the host last reported a living unit of each squad.</summary>
    private static readonly Dictionary<int, int> HostSeen = new();
    /// <summary>Guest: when each of our squads was first seen alive here.</summary>
    private static readonly Dictionary<int, int> OursSince = new();
    /// <summary>Guest: every candidate the mission listed, by squad id (also after it came in).</summary>
    private static readonly Dictionary<int, SquadAddingParam> Candidates = new();
    /// <summary>Guest: squads brought in from the host's report, and how often each was tried.</summary>
    private static readonly Dictionary<int, int> Brought = new();
    private const int MaxTries = 3;
    private static int _firstReport = -1, _nextCheck, _playFrom = -1;

    /// <summary>Squads brought in and taken away this battle (guest), for the log.</summary>
    internal static int BroughtIn, TakenAway, Unknown;

    internal static void Reset()
    {
        HostSeen.Clear();
        OursSince.Clear();
        Candidates.Clear();
        Brought.Clear();
        _firstReport = -1;
        _nextCheck = 0;
        _playFrom = -1;
        BroughtIn = TakenAway = Unknown = 0;
    }

    /// <summary>Guest: the host has a living unit of this squad.</summary>
    internal static void HostHas(int squad)
    {
        int now = Time.frameCount;
        HostSeen[squad] = now;
        if (_firstReport < 0) _firstReport = now;
    }

    /// <summary>A candidate enemy squad was listed (kept so a squad can be brought in again).</summary>
    internal static void Listed(UnitTroop troop, SquadAddingParam? param)
    {
        if (param == null || !Battle.Active || CoopNet.IsHost || !IsEnemy(troop)) return;
        Candidates[param.id] = param;
    }

    /// <summary>Guest: a squad we brought in from the host's report is on the field with this id.</summary>
    internal static bool OnField(UnitTroop troop, SquadAddingParam? param)
    {
        if (param == null || !HitSync.GuestBattle || !Brought.ContainsKey(param.id) || !IsEnemy(troop) || troop.unitSquadPtrList_ == null) return false;
        foreach (var squad in troop.unitSquadPtrList_)
            if (squad?.squadInfo_ != null && squad.squadInfo_.uniqueId == param.id && squad.unitBasePtrList_ != null && Living(squad)) return true;
        return false;
    }

    private static bool IsEnemy(UnitTroop? troop) => troop?.troopInfo_ != null && (int)troop.troopInfo_.troopType == EnemyTroop;

    internal static void Tick()
    {
        if (!HitSync.GuestBattle || _firstReport < 0) return;
        int now = Time.frameCount;
        if (now < _nextCheck) return;
        _nextCheck = now + CheckEvery;
        var game = P2.Game.Game.pGame_g;
        if (game == null || game.gamePhase_ != P2.Game.Game.GamePhase.GamePhase_Play || game.isGameEndOrder_) return;
        if (_playFrom < 0) _playFrom = now;
        if (now - _playFrom > OpeningFrames) return;
        var troops = game.getUnitMng()?.unitTroopPtrArray_;
        if (troops == null || troops.Count <= EnemyTroop) return;
        var troop = troops[EnemyTroop];
        if (troop?.unitSquadPtrList_ == null) return;

        var alive = new Dictionary<int, UnitSquad>();
        foreach (var squad in troop.unitSquadPtrList_)
        {
            if (squad?.squadInfo_ == null || squad.unitBasePtrList_ == null || !Living(squad)) continue;
            int id = squad.squadInfo_.uniqueId;
            alive[id] = squad;
            if (!OursSince.ContainsKey(id)) OursSince[id] = now;
        }

        foreach (var (id, seen) in new List<KeyValuePair<int, int>>(HostSeen))
            if (now - seen <= GraceFrames && !alive.ContainsKey(id)) BringIn(troop, id);

        // nothing is taken away before the host's reports have gone round all its enemies
        if (now - _firstReport <= GraceFrames) return;
        foreach (var (id, squad) in alive)
        {
            if (HostSeen.TryGetValue(id, out int seen) && now - seen <= GraceFrames) continue;
            if (now - OursSince[id] <= GraceFrames) continue;
            TakeAway(squad, id);
        }
    }

    private static bool Living(UnitSquad squad)
    {
        foreach (var u in squad.unitBasePtrList_)
            if (u != null && !u.isEnd()) return true;
        return false;
    }

    /// <summary>The way the mission's script does it: squadAddingCheck naming the squad.</summary>
    private static void BringIn(UnitTroop troop, int id)
    {
        Brought.TryGetValue(id, out int tries);
        if (tries >= MaxTries) return;
        Brought[id] = tries + 1;
        if (!InList(troop, id))
        {
            // came in here earlier and was taken away: list it again
            if (!Candidates.TryGetValue(id, out var param))
            {
                if (Unknown++ < 5) CoopPlugin.L.LogWarning($"[coop] the host has enemy squad {id}, which this mission never listed here");
                HostSeen.Remove(id);
                return;
            }
            troop.addSquadToAddingList(param);
        }
        troop.squadAddingCheck(Nowhere, id);
        OursSince.Remove(id);
        if (BroughtIn++ < 20) CoopPlugin.L.LogInfo($"[coop] enemy squad {id} brought in as on the host");
    }

    private static bool InList(UnitTroop troop, int id)
    {
        var list = troop.addingDataList_;
        if (list == null) return false;
        foreach (var p in list)
            if (p != null && p.id == id) return true;
        return false;
    }

    /// <summary>Gone without dying (no death, no drops): it never was on the host's field.</summary>
    private static void TakeAway(UnitSquad squad, int id)
    {
        foreach (var u in squad.unitBasePtrList_)
            if (u != null && !u.isEnd()) u.deleteUnit();
        OursSince.Remove(id);
        if (TakenAway++ < 20) CoopPlugin.L.LogInfo($"[coop] enemy squad {id} taken away: the host does not have it");
    }
}

[HarmonyPatch(typeof(UnitTroop), nameof(UnitTroop.addSquadToAddingList))]
internal static class EnemyCandidatePatch
{
    private static void Postfix(UnitTroop __instance, SquadAddingParam pSquadAddingParam)
    {
        try { EnemyRoster.Listed(__instance, pSquadAddingParam); }
        catch (Exception e) { CoopPlugin.L.LogWarning("could not note an enemy candidate: " + e.Message); }
    }
}

/// <summary>
/// Guest: a squad brought in as on the host may come in again from our own script (its candidate
/// listed anew, or its place reached): it is on the field already, so it does not come twice.
/// </summary>
[HarmonyPatch(typeof(UnitTroop), nameof(UnitTroop.addSquad))]
internal static class EnemyOnceOnlyPatch
{
    private static bool Prefix(UnitTroop __instance, SquadAddingParam pSquadAddingParam, ref bool __result)
    {
        try
        {
            if (!EnemyRoster.OnField(__instance, pSquadAddingParam)) return true;
            __result = false;
            return false;
        }
        catch (Exception e)
        {
            CoopPlugin.L.LogWarning("could not check an enemy squad coming in: " + e.Message);
            return true;
        }
    }
}
