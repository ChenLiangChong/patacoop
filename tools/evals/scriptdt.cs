// The mission script manager's time steps: every ScriptMngBase.update(dt) with its frame, to see how scripts keep
// time around the battle's start. Read: return string.Join("\n", (System.Collections.Generic.List<string>)Vars["scriptDt"]);
using HarmonyLib;
public static class ScriptDt {
  public static readonly System.Collections.Generic.List<string> Log = new();
  public static void Pre(uint dt) { if (Log.Count < 3000) Log.Add($"f{UnityEngine.Time.frameCount} dt{dt}"); }
}
ScriptDt.Log.Clear();
if (Vars.TryGetValue("scriptDtHooks", out var old)) ((Harmony)old).UnpatchSelf();
var h = new Harmony("scriptDtHooks");
h.Patch(AccessTools.Method(typeof(P2.GameSystem.ScriptMngBase), "update"), prefix: new HarmonyMethod(typeof(ScriptDt).GetMethod("Pre")));
Vars["scriptDtHooks"] = h; Vars["scriptDt"] = ScriptDt.Log;
return $"[{Instance}] script time probe armed";
