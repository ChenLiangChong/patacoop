// per march step of our troop: half beat, base movement, every player's raw command, flag bearer position
public static class StepProbe {
  public static System.Text.StringBuilder Log = new();
  public static int Left = 1600;
  public static void Pre(P2.Game.Unit.TroopCtrl __instance, out float __state) {
    __state = float.NaN; var t = __instance.pUnitTroop_;
    if (Left <= 0 || t?.troopInfo_ == null || (int)t.troopInfo_.troopType != 0) return;
    __state = t.troopBasePos_[0];
    var inst = t.aInstructionParam_; var f = __instance.flagUnit_;
    Log.Append($"{P2.Game.Game.pGame_g.soundDirector_.getBeatTimer().halfBeatCount_} ");
    for (int p = 0; p < 2 && p < inst.Length; p++) Log.Append((int)inst[p].lastActionCmd_).Append(' ');
    Log.Append($"flag={(f?.flagUnitModel_?.pos_.x ?? 0):F0} spd={(f?.moveSpeed_ ?? 0):F1} ");
  }
  public static void Post(P2.Game.Unit.TroopCtrl __instance, float __state) {
    if (float.IsNaN(__state)) return; var t = __instance.pUnitTroop_;
    Log.Append($"base={t.troopBasePos_[0]:F1} d={t.troopBasePos_[0] - __state:F2}\n"); Left--;
  }
}
var h = new HarmonyLib.Harmony("stepprobe");
var m = HarmonyLib.AccessTools.Method(typeof(P2.Game.Unit.TroopCtrl), "moveSquadLine");
h.Patch(m, prefix: new HarmonyLib.HarmonyMethod(typeof(StepProbe).GetMethod("Pre")) { priority = HarmonyLib.Priority.First + 300 }, postfix: new HarmonyLib.HarmonyMethod(typeof(StepProbe).GetMethod("Post")) { priority = HarmonyLib.Priority.Last });
Vars["stepprobe"] = typeof(StepProbe);
return $"[{Instance}] step probe armed";
