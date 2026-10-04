// Which squads a battle brings in, and how: UnitTroop.addMapSquad / addMapSquadEx / addSquadToAddingList / addSquad
// and squadAddingCheck, with the troop, ids and the frame. Typed arguments only.
// Read: return string.Join("\n", (System.Collections.Generic.List<string>)Vars["squadAdd"]);
using HarmonyLib;
public static class SquadAdd {
  public static readonly System.Collections.Generic.List<string> Log = new();
  static string Troop(P2.Game.Unit.UnitTroop t) { try { return ((int)t.troopInfo_.troopType).ToString(); } catch { return "?"; } }
  static void Add(string s) { if (Log.Count < 5000) Log.Add($"f{UnityEngine.Time.frameCount} {s}"); }
  public static void Map(P2.Game.Unit.UnitTroop __instance, int id, int uniqueId) => Add($"troop{Troop(__instance)} addMapSquad id{id} uid{uniqueId}");
  public static void MapEx(P2.Game.Unit.UnitTroop __instance, int id, int uniqueId, int checkType, bool enablePos, float x, float y, float randX) => Add($"troop{Troop(__instance)} addMapSquadEx id{id} uid{uniqueId} check{checkType} pos{enablePos} x{x:F0} y{y:F0} randX{randX:F0}");
  static string P(P2.Game.Unit.SquadAddingParam p) { try { return $"id{p.id} {p.unitParam?.name} n{p.unitNum} posX{p.posX:F0} hero{p.isHero}"; } catch { return "?"; } }
  public static void ToList(P2.Game.Unit.UnitTroop __instance, P2.Game.Unit.SquadAddingParam pSquadAddingParam) => Add($"troop{Troop(__instance)} addSquadToAddingList {P(pSquadAddingParam)}");
  public static void Direct(P2.Game.Unit.UnitTroop __instance, P2.Game.Unit.SquadAddingParam pSquadAddingParam) => Add($"troop{Troop(__instance)} addSquad {P(pSquadAddingParam)}");
}
SquadAdd.Log.Clear();
if (Vars.TryGetValue("squadAddHooks", out var old)) ((Harmony)old).UnpatchSelf();
var h = new Harmony("squadAddHooks"); var t = typeof(P2.Game.Unit.UnitTroop);
h.Patch(AccessTools.Method(t, "addMapSquad"), prefix: new HarmonyMethod(typeof(SquadAdd).GetMethod("Map")));
h.Patch(AccessTools.Method(t, "addMapSquadEx"), prefix: new HarmonyMethod(typeof(SquadAdd).GetMethod("MapEx")));
h.Patch(AccessTools.Method(t, "addSquadToAddingList"), prefix: new HarmonyMethod(typeof(SquadAdd).GetMethod("ToList")));
h.Patch(AccessTools.Method(t, "addSquad"), prefix: new HarmonyMethod(typeof(SquadAdd).GetMethod("Direct")));
Vars["squadAddHooks"] = h; Vars["squadAdd"] = SquadAdd.Log;
return $"[{Instance}] squad adding probe armed";
