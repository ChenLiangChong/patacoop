using HarmonyLib;
public static class PoolWatch {
  public static readonly int[] Miss = new int[64], Peak = new int[64];
  public static int Logged;
  static readonly BepInEx.Logging.ManualLogSource L = BepInEx.Logging.Logger.CreateLogSource("poolwatch");
  public static void Post(P2.Game.Actor.GameActorPool __instance, int actorCategoryId, P2.GameSystem.Actor.ActorObj __result) {
    try {
      int t = actorCategoryId; if (t < 0 || t >= 64) return;
      int a = __instance.allocListBasePtrArray_?[t]?.Count ?? -1, f = __instance.freeListBasePtrArray_?[t]?.Count ?? -1;
      if (a > Peak[t]) Peak[t] = a;
      if (__result == null) { Miss[t]++; if (Logged++ < 12) L.LogWarning($"pool empty: category {t} free={f} alloc={a} (miss #{Miss[t]})"); }
    } catch (System.Exception e) { if (Logged++ < 12) L.LogError(e.ToString()); }
  }
  public static string Counts() {
    var g = P2.Game.Game.pGame_g; int pu = 0, ps = 0, eu = 0, es = 0;
    var tr = g.getUnitMng().unitTroopPtrArray_;
    foreach (var sq in tr[0].unitSquadPtrList_) { ps++; pu += sq.unitBasePtrList_.Count; }
    foreach (var sq in tr[1].unitSquadPtrList_) { es++; eu += sq.unitBasePtrList_.Count; }
    return $"player {ps} squads/{pu} units, enemy {es} squads/{eu} units; misses squad={Miss[6]} unit={Miss[25]} equip={Miss[30]}; peak in use squad={Peak[6]} unit={Peak[25]} equip={Peak[30]}";
  }
  public static void EndPre(P2.Game.Game.GameEndType gameEndType) {
    try { L.LogWarning($"setGameEnd({gameEndType}) at frame {UnityEngine.Time.frameCount}: " + Counts()); } catch (System.Exception e) { L.LogError(e.ToString()); }
  }
}
var h = new Harmony("poolwatch");
h.Patch(AccessTools.Method(typeof(P2.Game.Actor.GameActorPool), "getObject"), postfix: new HarmonyMethod(typeof(PoolWatch).GetMethod("Post")));
h.Patch(AccessTools.Method(typeof(P2.Game.Game), "setGameEnd"), prefix: new HarmonyMethod(typeof(PoolWatch).GetMethod("EndPre")));
Vars["poolwatch"] = h;
return $"[{Instance}] pool watch armed";
