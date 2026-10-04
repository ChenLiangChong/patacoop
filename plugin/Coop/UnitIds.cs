using System;
using System.Collections.Generic;
using P2.Game.Unit;

namespace PataCoop.Coop;

/// <summary>
/// Units have no id that is the same on every machine: their unique id is a slot in the battle's
/// stock, taken in the order they were made, and two machines make enemies in different orders.
/// Squads do have one (the mission's own ids, or ours). So a unit is named by its troop, its squad's
/// id and its place among that squad's units, in the order they joined it, as the game's own
/// multiplayer named units (squad id and line index). Unlike a line index, a place stays the unit's
/// when units before it fall.
/// </summary>
internal static class UnitIds
{
    private static readonly Dictionary<IntPtr, (int Squad, int Slot, int Place)> Known = new();
    private static readonly Dictionary<(int Troop, int Squad), int> Next = new();

    internal static void Reset()
    {
        Known.Clear();
        Next.Clear();
    }

    /// <summary>Give a squad's new units their places, in list order (every squad, every update).</summary>
    internal static void Note(UnitSquad? squad)
    {
        var units = squad?.unitBasePtrList_;
        if (units == null || squad!.squadInfo_ == null) return;
        int troop = (int)(squad.pUnitTroop_?.troopInfo_?.troopType ?? (TroopType)(-1));
        int id = squad.squadInfo_.uniqueId;
        foreach (var unit in units)
        {
            if (unit?.info_ == null) continue;
            int slot = unit.info_.uniqueId;
            // a unit object can come back from the stock for another squad: that is a new unit
            if (Known.TryGetValue(unit.Pointer, out var k) && k.Squad == id && k.Slot == slot) continue;
            Next.TryGetValue((troop, id), out int place);
            Next[(troop, id)] = place + 1;
            Known[unit.Pointer] = (id, slot, place);
        }
    }

    /// <summary>This unit's place in its squad (-1 if it has no squad).</summary>
    internal static int PlaceOf(UnitBase unit)
    {
        if (!Known.TryGetValue(unit.Pointer, out var k) || k.Slot != (unit.info_?.uniqueId ?? -1) || k.Squad != (unit.pUnitSquad_?.squadInfo_?.uniqueId ?? -1))
        {
            Note(unit.pUnitSquad_);
            if (!Known.TryGetValue(unit.Pointer, out k)) return -1;
        }
        return k.Place;
    }

    /// <summary>Every unit on the field by (troop, squad id, place).</summary>
    internal static Dictionary<(int Troop, int Squad, int Place), UnitBase> All(P2.Game.Game? game)
    {
        var map = new Dictionary<(int, int, int), UnitBase>();
        var troops = game?.getUnitMng()?.unitTroopPtrArray_;
        if (troops == null) return map;
        foreach (var troop in troops)
        {
            if (troop?.unitSquadPtrList_ == null || troop.troopInfo_ == null) continue;
            int t = (int)troop.troopInfo_.troopType;
            foreach (var squad in troop.unitSquadPtrList_)
            {
                if (squad?.unitBasePtrList_ == null || squad.squadInfo_ == null) continue;
                Note(squad);
                foreach (var u in squad.unitBasePtrList_)
                    if (u != null && Known.TryGetValue(u.Pointer, out var k)) map[(t, squad.squadInfo_.uniqueId, k.Place)] = u;
            }
        }
        return map;
    }
}
