using System;
using System.Collections.Generic;
using HarmonyLib;

namespace PataCoop.Coop;

/// <summary>
/// Some of the battle is decided with Unity's random numbers: which of a mission's alternative
/// squads come on (a hunting ground's script picks its herds with "rand"), and what some units do.
/// Every machine rolled its own, so the host and a guest got different animals. One shared stream
/// is not enough: scripts also roll every battle step, and machines run different numbers of
/// frames. So each of those rolls is seeded on its own from what every machine agrees on: the
/// battle's seed (the host's, sent with the sortie), which roll it is (script, squad), the battle
/// step (<see cref="BattleClock"/>, the same at the same step everywhere, miracles or not) and how
/// many such rolls came before it in that step. Before the battle's first step every roll of a
/// script gives the same number: scripts keep time while the machines wait for each other
/// before the mission's opening, then run the waited ticks in one step, rolling once per tick (a
/// hunting ground picks its herds there). How long each machine waited, and so how many rolls it
/// made, depends on the network, so no number there may depend on the count. Unity's own random state is put back afterwards, so weather, particles,
/// damage spread and result chests keep their own randomness.
/// </summary>
internal static class SharedRandom
{
    /// <summary>Kinds of roll (the script's "rand", a squad's units, a script command over all units).</summary>
    internal const int ScriptRoll = 1, UnitSetRoll = 2, AllUnitsRoll = 3;

    /// <summary>The seed for the next co-op battle (the host's choice).</summary>
    internal static int Seed { get; set; }

    /// <summary>Testing: when set (by a dev script), every roll's key is added here.</summary>
    internal static List<string>? Trace = null;

    private static bool _ready;
    /// <summary>The step the counts below belong to (battle or opening, and which step).</summary>
    private static (bool Battle, uint Step) _bucket = (false, uint.MaxValue);
    private static readonly Dictionary<(int Kind, int Who), int> RollsThisStep = new();

    /// <summary>Start a battle's rolls (before the mission sets itself up).</summary>
    internal static void Start()
    {
        _ready = true;
        _bucket = (false, uint.MaxValue);
        RollsThisStep.Clear();
        CoopPlugin.L.LogInfo($"[coop] shared random seed {Seed}");
    }

    internal static void Stop() => _ready = false;

    /// <summary>Seed Unity's random numbers for this roll (returns what to put back).</summary>
    internal static UnityEngine.Random.State? Enter(int kind, int who)
    {
        if (!_ready || !Battle.Active && !Battle.Preparing) return null;
        bool battle = BattleClock.Started;
        var bucket = (battle, battle ? (uint)BattleClock.Ticks : (uint)BattleClock.OpeningSteps);
        if (bucket != _bucket)
        {
            _bucket = bucket;
            RollsThisStep.Clear();
        }
        uint tick = battle ? bucket.Item2 : 0;
        RollsThisStep.TryGetValue((kind, who), out int n);
        RollsThisStep[(kind, who)] = n + 1;
        Trace?.Add($"{P2.Game.Game.pGame_g?.gamePhase_.ToString().Replace("GamePhase_", "") ?? "-"} {(battle ? "battle" : "opening")} t{bucket.Item2} k{kind} w{who} n{n}");
        var outside = UnityEngine.Random.state;
        UnityEngine.Random.InitState(battle ? Mix(Seed, kind, who, 1, (int)tick, n)
            : kind == ScriptRoll ? Mix(Seed, kind, who, 0) : Mix(Seed, kind, who, 0, 0, n));
        return outside;
    }

    internal static void Leave(UnityEngine.Random.State? outside)
    {
        if (outside is { } o) UnityEngine.Random.state = o;
    }

    private static int Mix(params int[] values)
    {
        unchecked
        {
            uint h = 2166136261;
            foreach (int v in values)
                for (int b = 0; b < 4; b++) h = (h ^ (uint)(v >> (8 * b) & 0xFF)) * 16777619;
            return (int)h;
        }
    }
}

/// <summary>The mission scripts' own "rand".</summary>
[HarmonyPatch(typeof(P2.Pyd.Script.Talk.CommandBasic), nameof(P2.Pyd.Script.Talk.CommandBasic.cmd_rand))]
internal static class ScriptRandPatch
{
    private static void Prefix(P2.Pyd.Script.Talk.Controller talkController, out UnityEngine.Random.State? __state) =>
        __state = SharedRandom.Enter(SharedRandom.ScriptRoll, (int)(talkController?.id_ ?? 0));

    private static Exception? Finalizer(UnityEngine.Random.State? __state, Exception? __exception)
    {
        SharedRandom.Leave(__state);
        return __exception;
    }
}

/// <summary>A squad's units being put together (which ones come).</summary>
[HarmonyPatch(typeof(P2.Game.Unit.UnitSquad), nameof(P2.Game.Unit.UnitSquad.addUnitSet))]
internal static class UnitSetRandPatch
{
    private static void Prefix(P2.Game.Unit.UnitSquad __instance, out UnityEngine.Random.State? __state) =>
        __state = SharedRandom.Enter(SharedRandom.UnitSetRoll, __instance?.squadInfo_?.uniqueId ?? -1);

    private static Exception? Finalizer(UnityEngine.Random.State? __state, Exception? __exception)
    {
        SharedRandom.Leave(__state);
        return __exception;
    }
}

/// <summary>A mission script's command over all units.</summary>
[HarmonyPatch(typeof(P2.Game.Talk.CommandGame), nameof(P2.Game.Talk.CommandGame.variousProcForAllUnit))]
internal static class AllUnitsRandPatch
{
    private static void Prefix(out UnityEngine.Random.State? __state) => __state = SharedRandom.Enter(SharedRandom.AllUnitsRoll, 0);

    private static Exception? Finalizer(UnityEngine.Random.State? __state, Exception? __exception)
    {
        SharedRandom.Leave(__state);
        return __exception;
    }
}
