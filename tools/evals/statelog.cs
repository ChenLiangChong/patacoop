using HarmonyLib;
public static class StateLog {
  static readonly BepInEx.Logging.ManualLogSource L = BepInEx.Logging.Logger.CreateLogSource("state");
  static int _next; static string _last = "";
  public static void Post(P2.Game.Game __instance) {
    if (UnityEngine.Time.frameCount < _next) return;
    _next = UnityEngine.Time.frameCount + 180;
    try {
      var g = __instance; var tr = g.getUnitMng()?.unitTroopPtrArray_; if (tr == null) return;
      var sb = new System.Text.StringBuilder($"{g.gamePhase_} base={(int)tr[0].troopBasePos_[0]} |");
      foreach (var sq in tr[0].unitSquadPtrList_) sb.Append($" {sq.squadInfo_?.uniqueId}:{sq.squadInfo_?.squadAddingParam?.unitParam?.name?.Replace("_01_01", "")}x{sq.unitBasePtrList_.Count}");
      sb.Append($" | enemies={tr[1].unitSquadPtrList_.Count} |");
      foreach (var gm in g.map_.gimmickManager_.gimmickList_) { if (gm == null) continue; var n = gm.layoutParam_?.name ?? ""; if (n.StartsWith("GRASS") || n.StartsWith("BASE_")) continue; sb.Append($" {n}:{(gm.isEnable_ ? gm.status_?.getHitPoint().ToString() : "gone")}"); }
      var s = sb.ToString(); if (s != _last) { _last = s; L.LogInfo(s); }
    } catch (System.Exception e) { L.LogWarning(e.Message); }
  }
}
var h = new Harmony("statelog");
h.Patch(AccessTools.Method(typeof(P2.Game.Game), "update", new[] { typeof(uint) }), postfix: new HarmonyMethod(typeof(StateLog).GetMethod("Post")));
Vars["statelog"] = h;
return $"[{Instance}] state log armed";
