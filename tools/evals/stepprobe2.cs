// per march step of our troop: half beat, base before/after, flag bearer, enemy front (what the march is clamped to)
public static class StepProbe2 {
  public static System.Text.StringBuilder Log = new();
  public static int Left = 1500;
  public static void Pre(P2.Game.Unit.TroopCtrl __instance, out float __state) {
    __state = float.NaN; var t = __instance.pUnitTroop_;
    if (Left <= 0 || t?.troopInfo_ == null || (int)t.troopInfo_.troopType != 0) return;
    __state = t.troopBasePos_[0];
  }
  public static void Post(P2.Game.Unit.TroopCtrl __instance, float __state) {
    if (float.IsNaN(__state)) return; var t = __instance.pUnitTroop_; var g = P2.Game.Game.pGame_g;
    var f = __instance.flagUnit_; var et = g.getUnitMng().getUnitTroop(P2.Game.Unit.TroopType.TroopType_Enemy); var tft = et?.pTpdUnitPosX_TFT_;
    var own = t.pTpdUnitPosX_TFT_;
    Log.Append($"{g.soundDirector_.getBeatTimer().halfBeatCount_} base={__state:F1}->{t.troopBasePos_[0]:F1} flag={(f?.flagUnitModel_?.pos_.x ?? -1):F1} grip={f?.isGripped_} egg={f?.isEgg_} fspd={(f?.moveSpeed_ ?? -1):F2} enemyFront={(tft != null && tft.Length > 0 ? tft[0].ToString("F0") : "-")} ownFront={(own != null && own.Length > 0 ? own[0].ToString("F0") : "-")} cmd0={(int)t.aInstructionParam_[0].lastActionCmd_}\n");
    Left--;
  }
}
var h = new HarmonyLib.Harmony("stepprobe2");
var m = HarmonyLib.AccessTools.Method(typeof(P2.Game.Unit.TroopCtrl), "moveSquadLine");
h.Patch(m, prefix: new HarmonyLib.HarmonyMethod(typeof(StepProbe2).GetMethod("Pre")) { priority = HarmonyLib.Priority.First + 300 }, postfix: new HarmonyLib.HarmonyMethod(typeof(StepProbe2).GetMethod("Post")) { priority = HarmonyLib.Priority.Last });
Vars["stepprobe2"] = typeof(StepProbe2);
return $"[{Instance}] step probe armed";
