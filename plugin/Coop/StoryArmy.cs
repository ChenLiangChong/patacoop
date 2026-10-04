using System;
using HarmonyLib;
using P2.Game.Unit;

namespace PataCoop.Coop;

/// <summary>
/// The multiplayer engine we borrow was made for the PSP's multiplayer missions, where player 1's
/// first squad carries an egg and the flag bearer with it ("タマゴ持参", squad control functions
/// 7-9), so the march code leaves the flag bearer's own speed at zero. Ordinary story missions have
/// no egg: without these patches that squad walks off to "pick up" the flag bearer and, on the
/// guests' side, both stay behind at the starting line while the army marches on. The hero-world
/// missions the story leads to are the game's own multiplayer missions (eggId set): they keep it.
/// </summary>
internal static class StoryArmy
{
    internal const int FirstEggFunction = 7, LastEggFunction = 9, WaitFunction = 0;

    /// <summary>Troop base position the flag bearer saw last frame (NaN: not seen yet).</summary>
    internal static float LastBase = float.NaN;

    internal static void Reset() => LastBase = float.NaN;
}

/// <summary>The egg-carrying squad keeps its place in the line like every other squad.</summary>
[HarmonyPatch(typeof(SquadCtrl), nameof(SquadCtrl.callSquadCtrlFunc))]
internal static class NoEggCarrierPatch
{
    private static void Prefix(ref SquadCtrl.SquadCtrlTarget pSquadCtrlTarget)
    {
        // the hero-world missions are the game's own multiplayer missions: their egg is carried for real
        if (!Battle.Active || (Battle.Settings?.eggId ?? 0) != 0) return;
        var work = pSquadCtrlTarget?.workData;
        if (work != null && work.squadCtrlFuncId >= StoryArmy.FirstEggFunction && work.squadCtrlFuncId <= StoryArmy.LastEggFunction)
            work.squadCtrlFuncId = StoryArmy.WaitFunction;
    }
}

/// <summary>
/// The flag bearer walks towards the troop's base position at most at his own speed. In a
/// single-player battle the march sets that speed; the multiplayer march sets zero. Give him the
/// army's pace (plus a little, so he catches up when the army stops).
/// </summary>
[HarmonyPatch(typeof(FlagUnit), nameof(FlagUnit.update), new[] { typeof(uint) })]
internal static class FlagBearerFollowsPatch
{
    private const float Lead = 1.5f, CatchUp = 1.5f;

    private static void Prefix(FlagUnit __instance)
    {
        if (!Battle.Active || __instance.isEgg_) return;
        var basePos = __instance.pUnitTroop_?.troopBasePos_;
        if (basePos == null || basePos.Length == 0) return;
        float now = basePos[0];
        float moved = float.IsNaN(StoryArmy.LastBase) ? 0f : Math.Abs(now - StoryArmy.LastBase);
        StoryArmy.LastBase = now;
        __instance.moveSpeed_ = Math.Max(moved * Lead, CatchUp);
    }
}
