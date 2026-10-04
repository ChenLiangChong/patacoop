// Who hands the player troop a command: logs every UnitTroop.receiveCommand (player id, command, frame) and
// whether it runs inside Miracle.Controller.activate / update / startDance. Read: return string.Join("\n", (System.Collections.Generic.List<string>)Vars["troopCmdLog"]);
using HarmonyLib;
public static class TroopCmd {
  public static readonly System.Collections.Generic.List<string> Log = new();
  public static string Where = ""; static int _miss; public static int Risky;
  public static void Enter(System.Reflection.MethodBase __originalMethod) => Where += "/" + __originalMethod.Name;
  public static void Leave(System.Reflection.MethodBase __originalMethod) { int i = Where.LastIndexOf('/'); if (i >= 0) Where = Where.Substring(0, i); }
  public static void Receive(P2.Game.Unit.UnitTroop __instance, P2.Game.Unit.InstructionParam.CommandParam pCommandParam, int playerId) {
    string cmd; try { cmd = pCommandParam == null ? "null" : $"{pCommandParam.command} acc{pCommandParam.accuracyRatio:F2} hero{pCommandParam.heroState}"; } catch (System.Exception e) { cmd = "?" + e.GetType().Name; }
    if (cmd.StartsWith("Command_Miss") ? ++_miss > 20 : Log.Count > 4000) return;
    var inst = __instance.aInstructionParam_;
    // what the native code needs (it throws a null reference otherwise)
    string risk = "";
    if (playerId < -1 || playerId > 3) risk += " PLAYER-OUT-OF-RANGE";
    else if (inst == null) risk += " NO-INSTRUCTIONS";
    else if (playerId >= inst.Length) risk += " PLAYER-BEYOND-INSTRUCTIONS";
    else if (inst[playerId < 0 ? 0 : playerId] == null) risk += " NULL-INSTRUCTION";
    if (pCommandParam == null) risk += " NULL-PARAM";
    if (__instance.troopCtrl_ == null) risk += " NO-TROOPCTRL";
    if (__instance.pGame_ == null) risk += " NO-GAME";
    if (risk.Length > 0) Risky++;
    Log.Add($"f{UnityEngine.Time.frameCount} troop{(int)(__instance.troopInfo_?.troopType ?? (P2.Game.Unit.TroopType)(-1))} player{playerId} cmd={cmd} inst={(inst == null ? "null" : inst.Length.ToString())} in={Where}{risk}");
  }
}
TroopCmd.Log.Clear(); TroopCmd.Where = "";
if (Vars.TryGetValue("troopCmd", out var old)) ((Harmony)old).UnpatchSelf();
var h = new Harmony("troopCmd");
foreach (var name in new[] { "activate", "update", "startDance", "endMiracle" })
  h.Patch(AccessTools.Method(typeof(P2.Game.Miracle.Controller), name), prefix: new HarmonyMethod(typeof(TroopCmd).GetMethod("Enter")), finalizer: new HarmonyMethod(typeof(TroopCmd).GetMethod("Leave")));
h.Patch(AccessTools.Method(typeof(P2.Game.Unit.UnitTroop), "receiveCommand"), prefix: new HarmonyMethod(typeof(TroopCmd).GetMethod("Receive")));
Vars["troopCmd"] = h; Vars["troopCmdLog"] = TroopCmd.Log;
return $"[{Instance}] troop command probe armed";
