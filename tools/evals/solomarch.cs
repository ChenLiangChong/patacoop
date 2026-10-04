// experiment: run our own troop's march on the single-player path (isMultiMode off just for moveSquadLine)
public static class SoloMarch {
  public static int Calls;
  static P2.Game.GameSettingData S => P2.LaboCommon.pLaboCommonInstance_g?.laboSettingDataPtr_?.gameSettingData;
  public static void Pre(P2.Game.Unit.TroopCtrl __instance, out bool __state) {
    __state = false;
    var t = __instance.pUnitTroop_; var s = S;
    if (t?.troopInfo_ == null || (int)t.troopInfo_.troopType != 0 || s == null || !s.isMultiMode) return;
    s.isMultiMode = false; __state = true; Calls++;
  }
  public static void Post(bool __state) { if (__state && S != null) S.isMultiMode = true; }
}
var h = new HarmonyLib.Harmony("solomarch");
var m = HarmonyLib.AccessTools.Method(typeof(P2.Game.Unit.TroopCtrl), "moveSquadLine");
h.Patch(m, prefix: new HarmonyLib.HarmonyMethod(typeof(SoloMarch).GetMethod("Pre")) { priority = HarmonyLib.Priority.Last }, postfix: new HarmonyLib.HarmonyMethod(typeof(SoloMarch).GetMethod("Post")) { priority = HarmonyLib.Priority.First });
Vars["solomarch"] = typeof(SoloMarch);
return $"[{Instance}] solo-path march armed";
