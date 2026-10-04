using System;
using HarmonyLib;
using P2.Game.Actor;
using P2.Game.Unit;
using P2.GameSystem.Actor;

namespace PataCoop.Coop;

/// <summary>
/// Room for every army on the field.
///
/// A mission sets up a fixed stock of battle objects that both sides draw from: 64 units, 40 squads
/// and 160 pieces of equipment. That fits one army plus the enemies, and the game's own multiplayer
/// (one hero squad per extra player) stays within it too. Four full armies are 80-odd units on our
/// side alone: units beyond the stock are never created and the enemies run short as well. In a
/// co-op battle the stock grows by a full army for each player beyond the first, and should it still
/// run dry, one more object is made on the spot.
///
/// A unit's id is its place in the stock (the game numbers them 0..63), so added units continue that
/// numbering. Every machine adds the same objects in the same order, so the ids agree between them.
/// </summary>
internal static class ActorPools
{
    // the game's stock lists, by actor category
    private const int SquadCategory = 6, UnitCategory = 25, EquipCategory = 30;
    // the game's own stock, and one more full army: about 21 units in up to 6 squads, up to 4 pieces of equipment each
    private const int BaseSquads = 40, BaseUnits = 64, BaseEquip = 160;
    private const int SquadsPerPlayer = 8, UnitsPerPlayer = 24, EquipPerPlayer = 96;
    // made on demand at most up to this many times the grown stock (a runaway spawner must not grow it forever)
    private const int OnDemandLimit = 2;
    private const int MaxOnDemandNotes = 5;

    private static int _onDemand;

    private static int Target(int category, int others) => category switch
    {
        UnitCategory => BaseUnits + UnitsPerPlayer * others,
        SquadCategory => BaseSquads + SquadsPerPlayer * others,
        _ => BaseEquip + EquipPerPlayer * others,
    };

    /// <summary>
    /// Grow a battle's stock for the other players' armies, up to a size fixed by the player count
    /// (so a stock set up again, or kept from an earlier battle, ends up the same on every machine).
    /// </summary>
    internal static void Grow(GameActorPool? pool)
    {
        if (pool == null || !Battle.Active) return;
        int others = Math.Max(0, Session.PlayerCount - 1);
        if (others == 0) return;
        int units = Add(pool, UnitCategory, Target(UnitCategory, others) - Total(pool, UnitCategory));
        int squads = Add(pool, SquadCategory, Target(SquadCategory, others) - Total(pool, SquadCategory));
        int equipment = Add(pool, EquipCategory, Target(EquipCategory, others) - Total(pool, EquipCategory));
        if (units + squads + equipment == 0) return;
        _onDemand = 0;
        CoopPlugin.L.LogInfo($"[coop] battle stock for {others} more armies: +{units} units (now {Total(pool, UnitCategory)}), +{squads} squads, +{equipment} equipment");
    }

    /// <summary>The game wants an object the stock has run out of: make one.</summary>
    internal static void Refill(GameActorPool pool, int category)
    {
        if (!Battle.Active || (category != UnitCategory && category != SquadCategory && category != EquipCategory)) return;
        var free = pool.freeListBasePtrArray_?[category];
        if (free == null || free.Count > 0) return;
        if (Total(pool, category) >= OnDemandLimit * Target(category, Math.Max(0, Session.PlayerCount - 1))) return;
        if (Add(pool, category, 1) == 0) return;
        if (_onDemand++ < MaxOnDemandNotes)
            CoopPlugin.L.LogWarning($"[coop] battle stock ran out of category {category}: made one more (now {Total(pool, category)})");
    }

    private static int Total(GameActorPool pool, int category) =>
        (pool.freeListBasePtrArray_?[category]?.Count ?? 0) + (pool.allocListBasePtrArray_?[category]?.Count ?? 0);

    /// <summary>Make objects the way the game's own stock set-up does and put them in the free list.</summary>
    private static int Add(GameActorPool pool, int category, int count)
    {
        var free = pool.freeListBasePtrArray_?[category];
        var accessor = pool.pSystemAccessor_;
        var game = accessor?.pGameSceneBase_?.TryCast<P2.Game.Game>();
        if (free == null || accessor == null || game == null) return 0;
        int made = 0;
        if (count <= 0) return 0;
        try
        {
            for (; made < count; made++)
            {
                ActorObj obj = category switch
                {
                    UnitCategory => new UnitBase(game, game.unitMngPtr_, Total(pool, category)), // id = next place in the stock
                    SquadCategory => new UnitSquad(game, game.unitMngPtr_),
                    _ => new UnitEquip(accessor),
                };
                free.Add(obj);
            }
        }
        catch (Exception e)
        {
            CoopPlugin.L.LogError($"could not add to the battle stock (category {category}, {made} made): {e}");
        }
        return made;
    }
}

/// <summary>A battle's stock was just set up (the game builds its lists in reset).</summary>
[HarmonyPatch(typeof(GameActorPool), nameof(GameActorPool.reset))]
internal static class ActorPoolResetPatch
{
    private static void Postfix(GameActorPool __instance)
    {
        try { ActorPools.Grow(__instance); }
        catch (Exception e) { CoopPlugin.L.LogError("could not grow the battle stock: " + e); }
    }
}

[HarmonyPatch(typeof(GameActorPool), nameof(GameActorPool.getObject))]
internal static class ActorPoolRefillPatch
{
    private static void Prefix(GameActorPool __instance, int actorCategoryId)
    {
        try { ActorPools.Refill(__instance, actorCategoryId); }
        catch (Exception e) { CoopPlugin.L.LogError("could not refill the battle stock: " + e); }
    }
}
