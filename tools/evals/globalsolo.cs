// experiment: the whole game step runs with isMultiMode off (does any multiplayer branch cut the march short?)
public static class GlobalSolo {
  static P2.Game.GameSettingData S => P2.LaboCommon.pLaboCommonInstance_g?.laboSettingDataPtr_?.gameSettingData;
  public static void Pre(out bool __state) { __state = false; var s = S; if (s == null || !s.isMultiMode) return; s.isMultiMode = false; __state = true; }
  public static void Post(bool __state) { if (__state && S != null) S.isMultiMode = true; }
}
var h = new HarmonyLib.Harmony("globalsolo");
var m = HarmonyLib.AccessTools.Method(typeof(P2.Game.Game), "update", new[] { typeof(uint) });
h.Patch(m, prefix: new HarmonyLib.HarmonyMethod(typeof(GlobalSolo).GetMethod("Pre")) { priority = HarmonyLib.Priority.First + 200 }, postfix: new HarmonyLib.HarmonyMethod(typeof(GlobalSolo).GetMethod("Post")) { priority = HarmonyLib.Priority.Last });
Vars["globalsolo"] = typeof(GlobalSolo);
return $"[{Instance}] whole step on single-player rules";
