// counts squad-control functions per troop/function/unit (enemy AI behaviour), read with Vars["ctrlprobe"]
public static class CtrlProbe {
  public static System.Collections.Generic.Dictionary<string, int> Seen = new();
  public static void Pre(ref P2.Game.Unit.UnitSquad pUnitSquad, ref P2.Game.Unit.SquadCtrl.SquadCtrlTarget pSquadCtrlTarget) {
    var w = pSquadCtrlTarget?.workData; if (w == null) return;
    int tt = (int)(pUnitSquad?.pUnitTroop_?.troopInfo_?.troopType ?? (P2.Game.Unit.TroopType)(-1));
    if (tt != 1) return;
    string k = $"troop{tt} func{w.squadCtrlFuncId} {pUnitSquad?.squadInfo_?.squadAddingParam?.unitParam?.name}";
    lock (Seen) Seen[k] = Seen.TryGetValue(k, out var n) ? n + 1 : 1;
  }
  public static string Dump() { var sb = new System.Text.StringBuilder(); lock (Seen) foreach (var kv in Seen) sb.Append(kv.Key + " x" + kv.Value + "\n"); return sb.ToString(); }
}
var h = new HarmonyLib.Harmony("ctrlprobe");
h.Patch(HarmonyLib.AccessTools.Method(typeof(P2.Game.Unit.SquadCtrl), "callSquadCtrlFunc"), prefix: new HarmonyLib.HarmonyMethod(typeof(CtrlProbe).GetMethod("Pre")) { priority = HarmonyLib.Priority.First + 100 });
Vars["ctrlprobe"] = typeof(CtrlProbe);
return $"[{Instance}] ctrl probe armed";
