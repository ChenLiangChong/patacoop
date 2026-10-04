using System;
using System.Collections.Generic;
using HarmonyLib;
using P2.Game.Unit;

namespace PataCoop.Coop;

/// <summary>
/// Story companions: a Patapon the mission adds to our troop for a while (the Tatepon who walks
/// with the army and later fades away) is added by the mission script, once on every machine. In
/// co-op every player gets their own: each squad the mission adds to our troop is copied for every
/// other player, follows that player's drum, and goes when the mission's own one goes. Every machine
/// copies at the same moment in the same way, so the copies' ids agree everywhere.
/// </summary>
internal static class StoryCompanions
{
    /// <summary>Copies get squad ids from here up, in the order they are made (away from the players' own ids).</summary>
    internal const int CopyBase = 200;

    /// <summary>Places in the troop's waiting list left for the mission's own squads.</summary>
    private const int Headroom = 8;

    /// <summary>The mission squad's id -> its copies' ids.</summary>
    private static readonly Dictionary<int, List<int>> Copies = new();
    /// <summary>Copies still waiting to come on when their original went: they go as soon as they appear.</summary>
    private static readonly HashSet<int> Gone = new();
    private static bool _copying, _calling;
    private static int _nextId = CopyBase;

    internal static void Reset()
    {
        Copies.Clear();
        Gone.Clear();
        _nextId = CopyBase;
    }

    private static bool Ours(SquadAddingParam param) => (param.rsv1 & unchecked((int)0xFFFF0000)) == Armies.OwnerTag;

    /// <summary>A squad is about to join a troop: if it is a mission squad joining ours, copy it for the other players.</summary>
    internal static void Queued(UnitTroop troop, SquadAddingParam? param)
    {
        if (_copying || param == null || !Battle.Active || troop.troopInfo_ == null || (int)troop.troopInfo_.troopType != 0) return;
        if (Ours(param) || param.id >= CopyBase) return;
        // copy for the players who started the battle (a player joining or leaving meanwhile must not
        // make one machine copy more than another)
        int players = 0;
        for (int p = 1; p < Session.MaxPlayers; p++) if (Armies.InBattle[p]) players++;
        if (players == 0) return;
        int waiting = troop.addingDataList_?.Count ?? 0;
        if (waiting + players + Headroom > UnitTroop.addingListSize_g)
        {
            CoopPlugin.L.LogWarning($"[coop] story companion {param.unitParam?.name} (squad {param.id}) not copied: the troop's waiting list is nearly full ({waiting}/{UnitTroop.addingListSize_g})");
            return;
        }
        var ids = new List<int>();
        _copying = true;
        try
        {
            for (int p = 1; p < Session.MaxPlayers; p++)
            {
                if (!Armies.InBattle[p]) continue;
                var copy = new SquadAddingParam();
                var from = param;
                copy.CopyFrom(ref from, false);
                copy.id = _nextId++;
                copy.rsv1 = Armies.OwnerTag | p;
                if (troop.addSquadToAddingList(copy)) ids.Add(copy.id);
            }
        }
        finally
        {
            _copying = false;
        }
        if (ids.Count == 0) return;
        Copies[param.id] = ids;
        CoopPlugin.L.LogInfo($"[coop] story companion {param.unitParam?.name} (squad {param.id}) copied for {ids.Count} more players: squads {string.Join(", ", ids)}");
    }

    /// <summary>
    /// The game brings a waiting squad on the field when something asks for it by id (the script
    /// looking it up or activating it): when it asks for a companion, it asks for the copies too.
    /// </summary>
    internal static void Called(UnitTroop troop, float checkX, int indexId)
    {
        if (_calling || !Battle.Active || troop.troopInfo_ == null || (int)troop.troopInfo_.troopType != 0) return;
        if (Gone.Count > 0) SendAway(troop);
        if (!Copies.TryGetValue(indexId, out var ids)) return;
        _calling = true;
        try
        {
            foreach (var id in ids) troop.squadAddingCheck(checkX, id);
        }
        finally
        {
            _calling = false;
        }
    }

    /// <summary>The mission's own companion is going: its copies go with it, on the field or still waiting.</summary>
    internal static void Killed(UnitSquad squad)
    {
        var info = squad.squadInfo_;
        var troop = squad.pUnitTroop_;
        if (!Battle.Active || info == null || troop?.troopInfo_ == null || (int)troop.troopInfo_.troopType != 0) return;
        if (info.squadAddingParam == null || Ours(info.squadAddingParam) || !Copies.TryGetValue(info.uniqueId, out var ids)) return;
        foreach (var id in ids) Gone.Add(id);
        SendAway(troop);
    }

    /// <summary>Kill the copies whose original went that are on the field now (the rest when they come on).</summary>
    private static void SendAway(UnitTroop troop)
    {
        var going = new List<UnitSquad>();
        foreach (var other in troop.unitSquadPtrList_)
            if (other?.squadInfo_ != null && Gone.Remove(other.squadInfo_.uniqueId)) going.Add(other);
        foreach (var other in going) other.kill();
    }
}

[HarmonyPatch(typeof(UnitTroop), nameof(UnitTroop.addSquadToAddingList))]
internal static class StoryCompanionAddPatch
{
    private static void Postfix(UnitTroop __instance, SquadAddingParam pSquadAddingParam, bool __result)
    {
        if (!__result) return;
        try { StoryCompanions.Queued(__instance, pSquadAddingParam); }
        catch (Exception e) { CoopPlugin.L.LogWarning("could not copy a story companion: " + e.Message); }
    }
}

[HarmonyPatch(typeof(UnitTroop), nameof(UnitTroop.squadAddingCheck))]
internal static class StoryCompanionAppearPatch
{
    private static void Postfix(UnitTroop __instance, float checkX, int indexId)
    {
        try { StoryCompanions.Called(__instance, checkX, indexId); }
        catch (Exception e) { CoopPlugin.L.LogWarning("could not bring a story companion's copies on: " + e.Message); }
    }
}

[HarmonyPatch(typeof(UnitSquad), nameof(UnitSquad.kill))]
internal static class StoryCompanionKillPatch
{
    private static void Postfix(UnitSquad __instance)
    {
        try { StoryCompanions.Killed(__instance); }
        catch (Exception e) { CoopPlugin.L.LogWarning("could not take a story companion's copies away: " + e.Message); }
    }
}
