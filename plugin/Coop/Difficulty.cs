using System;
using HarmonyLib;
using P2.Game.Unit;
using P2.GameSystem;
using P2.GameSystem.Actor.Status;
using UnityEngine;

namespace PataCoop.Coop;

/// <summary>
/// More players bring more armies, so enemies get tougher: enemy units get more hit points and
/// hit harder, both growing with the number of players. The host's settings travel with the
/// "go" message, so every machine scales the same way (the battle is simulated everywhere).
/// </summary>
public static class Difficulty
{
    /// <summary>Enemy hit point multiplier for the running battle (1 = unchanged).</summary>
    public static float EnemyHp { get; private set; } = 1f;
    /// <summary>Multiplier on damage enemies deal to player units.</summary>
    public static float EnemyDamage { get; private set; } = 1f;

    /// <summary>The host's multipliers for a battle with <paramref name="players"/> players.</summary>
    internal static (float hp, float damage) ForPlayers(int players)
    {
        int extra = Math.Max(0, players - 1);
        return (1f + CoopPlugin.HpPerExtraPlayer.Value * extra, 1f + CoopPlugin.DamagePerExtraPlayer.Value * extra);
    }

    internal static void Use(float hp, float damage)
    {
        EnemyHp = hp > 0 ? hp : 1f;
        EnemyDamage = damage > 0 ? damage : 1f;
    }

    internal static void Reset() => Use(1f, 1f);

    /// <summary>Set while an enemy unit is being (re)built, so its hit points get scaled.</summary>
    [ThreadStatic] internal static bool ScalingEnemyUnit;

    internal static bool IsEnemy(UnitSquad? squad) =>
        squad?.pUnitTroop_?.troopInfo_ is { } info && (int)info.troopType == 1;
}

[HarmonyPatch(typeof(UnitBase), nameof(UnitBase.reset), new[] { typeof(UnitSquad), typeof(UnitAddingParam), typeof(P2.Game.Unit.CharaParam.BaseParam), typeof(Vector4) })]
internal static class EnemyUnitResetPatch
{
    private static void Prefix(UnitSquad pUnitSquad)
    {
        Difficulty.ScalingEnemyUnit = Battle.Active && Difficulty.EnemyHp != 1f && Difficulty.IsEnemy(pUnitSquad);
    }

    private static void Postfix() => Difficulty.ScalingEnemyUnit = false;
}

[HarmonyPatch(typeof(GameStatus.Info), nameof(GameStatus.Info.setMaxHitPoint))]
internal static class EnemyHitPointsPatch
{
    private static void Prefix(ref int maxHp)
    {
        if (Difficulty.ScalingEnemyUnit && maxHp > 0) maxHp = (int)Math.Round(maxHp * (double)Difficulty.EnemyHp);
    }
}

/// <summary>Hits on player units: enemies hit harder in bigger parties.</summary>
[HarmonyPatch(typeof(GameStatus), nameof(GameStatus.addHitPoint))]
internal static class EnemyDamagePatch
{
    private static void Prefix(GameStatus __instance, ref int _hp)
    {
        // (a guest applies the host's already scaled hit point changes as they are)
        if (!Battle.Active || HitSync.GuestBattle || _hp >= 0 || Difficulty.EnemyDamage == 1f) return;
        try
        {
            var unit = __instance.TryCast<ActorStatusObj>()?.pActorObj_?.TryCast<UnitBase>();
            var info = unit?.pUnitSquad_?.pUnitTroop_?.troopInfo_;
            if (info == null || (int)info.troopType != 0) return;
            _hp = (int)Math.Round(_hp * (double)Difficulty.EnemyDamage);
        }
        catch
        {
            // not a unit (gimmicks have statuses too): leave the hit alone
        }
    }
}
