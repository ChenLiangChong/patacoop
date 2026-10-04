using HarmonyLib;
using P2.Game.Unit;
using P2.GameSystem;

namespace PataCoop.Coop;

/// <summary>
/// Story missions can add squads of their own to the player troop (a Patapon waiting off-field to
/// be rescued, say) and their scripts find those squads by unique id. In a single-player game no
/// squad of the player uses that id at that point of the story, but in co-op another player's
/// squad may: squad N belongs to player N. While a mission script runs, a lookup that lands on one
/// of our squads therefore prefers the mission's own squad with the same id.
/// </summary>
internal static class MissionScripts
{
    /// <summary>True while the mission's script manager runs its scripts.</summary>
    internal static bool Running;
}

[HarmonyPatch(typeof(ScriptMngBase), nameof(ScriptMngBase.update))]
internal static class MissionScriptRunPatch
{
    private static void Prefix() => MissionScripts.Running = true;
    private static void Postfix() => MissionScripts.Running = false;
}

[HarmonyPatch(typeof(P2.Game.Game), nameof(P2.Game.Game.update))]
internal static class MissionScriptResetPatch
{
    // A script that threw never reached the postfix above; start every frame clean.
    private static void Prefix() => MissionScripts.Running = false;
}

[HarmonyPatch(typeof(UnitTroop), nameof(UnitTroop.getSquad_UniqueId))]
internal static class MissionSquadLookupPatch
{
    private static void Postfix(UnitTroop __instance, int uniqueId, ref UnitSquad __result)
    {
        if (!MissionScripts.Running || !Battle.Active || __result == null) return;
        if (((__result.squadInfo_?.squadAddingParam?.rsv1 ?? 0) & unchecked((int)0xFFFF0000)) != Armies.OwnerTag) return;
        var squads = __instance.unitSquadPtrList_;
        if (squads == null) return;
        foreach (var squad in squads)
        {
            var info = squad?.squadInfo_;
            if (info == null || info.uniqueId != uniqueId) continue;
            if (((info.squadAddingParam?.rsv1 ?? 0) & unchecked((int)0xFFFF0000)) == Armies.OwnerTag) continue;
            __result = squad!;
            return;
        }
    }
}

/// <summary>
/// Scripts also queue squad operations (bring on, kill, move) that the squad operation agent carries
/// out later, outside the script run: its lookups by id are the mission's too.
/// </summary>
[HarmonyPatch(typeof(SquadOperationAgent), nameof(SquadOperationAgent.doOperation))]
internal static class MissionSquadOperationPatch
{
    private static void Prefix(out bool __state)
    {
        __state = MissionScripts.Running;
        MissionScripts.Running = true;
    }

    private static void Postfix(bool __state) => MissionScripts.Running = __state;
}
