// Who sets the battle camera's x (TrackingCamera.setX): the mission script's setCameraX, the march watcher, or
// something else; with our own flag bearer's x at the time. Read: return string.Join("\n", (System.Collections.Generic.List<string>)Vars["camSrcLog"]);
using HarmonyLib;
public static class CamSrc {
  public static readonly System.Collections.Generic.List<string> Log = new();
  public static string Ctx = "other"; static int _n; static string _last = "";
  public static void Script() => Ctx = "script"; public static void Watch() => Ctx = "watch"; public static void Done() => Ctx = "other";
  public static void SetX(float __0) {
    var flag = P2.Game.Game.pGame_g?.getUnitMng()?.unitTroopPtrArray_?[0]?.troopCtrl_?.flagUnit_?.flagUnitModel_?.pos_.x ?? float.NaN;
    var line = $"{Ctx} x={__0:F0} flag={flag:F0} diff={__0 - flag:F0}";
    if (line == _last) { _n++; return; }
    if (_n > 0) Log.Add($"   (x{_n} more)"); _n = 0; _last = line;
    if (Log.Count < 400) Log.Add($"f{UnityEngine.Time.frameCount} " + line);
  }
}
CamSrc.Log.Clear();
if (Vars.TryGetValue("camSrc", out var old)) ((Harmony)old).UnpatchSelf();
var h = new Harmony("camSrc");
h.Patch(AccessTools.Method(typeof(P2.Game.Talk.CommandGame), "setCameraX"), prefix: new HarmonyMethod(typeof(CamSrc).GetMethod("Script")), finalizer: new HarmonyMethod(typeof(CamSrc).GetMethod("Done")));
h.Patch(AccessTools.Method(typeof(P2.Game.Mission.WatchGameMarch), "mainNormal"), prefix: new HarmonyMethod(typeof(CamSrc).GetMethod("Watch")), finalizer: new HarmonyMethod(typeof(CamSrc).GetMethod("Done")));
h.Patch(AccessTools.Method(typeof(P2.Game.TrackingCamera), "setX"), prefix: new HarmonyMethod(typeof(CamSrc).GetMethod("SetX")));
Vars["camSrc"] = h; Vars["camSrcLog"] = CamSrc.Log;
return $"[{Instance}] camera source probe armed";
