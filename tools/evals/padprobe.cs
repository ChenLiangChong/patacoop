// Which pad reads the game makes, per drum mode (Battle / Miracl ...): counts calls of P2.System.Pad.Pad's
// key checks by method and argument. Read: return string.Join("\n", (System.Collections.Generic.Dictionary<string, int>)Vars["padProbe"]);
using HarmonyLib;
using System.Collections.Generic;
public static class PadProbe {
  public static readonly Dictionary<string, int> Calls = new();
  static string Mode() { try { return P2.Game.Game.pGame_g?.soundDirector_?.beatCommander_?.getMode().ToString().Replace("Mode_", "") ?? "-"; } catch { return "?"; } }
  public static void Key(System.Reflection.MethodBase __originalMethod, uint key) { var k = $"{Mode()} {__originalMethod.Name}({key:X})"; Calls[k] = Calls.TryGetValue(k, out var n) ? n + 1 : 1; }
  public static void Flag(System.Reflection.MethodBase __originalMethod, object[] __args) { var k = $"{Mode()} {__originalMethod.Name}({__args[0]})"; Calls[k] = Calls.TryGetValue(k, out var n) ? n + 1 : 1; }
  public static void None(System.Reflection.MethodBase __originalMethod) { var k = $"{Mode()} {__originalMethod.Name}()"; Calls[k] = Calls.TryGetValue(k, out var n) ? n + 1 : 1; }
}
PadProbe.Calls.Clear();
if (Vars.TryGetValue("padProbeHooks", out var old)) ((Harmony)old).UnpatchSelf();
var h = new Harmony("padProbeHooks");
foreach (var m in typeof(P2.System.Pad.Pad).GetMethods(System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.DeclaredOnly)) {
  var ps = m.GetParameters();
  if (ps.Length == 1 && ps[0].ParameterType == typeof(uint)) h.Patch(m, postfix: new HarmonyMethod(typeof(PadProbe).GetMethod("Key")));
  else if (ps.Length == 1) h.Patch(m, postfix: new HarmonyMethod(typeof(PadProbe).GetMethod("Flag")));
  else if (ps.Length == 0 && !m.Name.StartsWith("get_")) h.Patch(m, postfix: new HarmonyMethod(typeof(PadProbe).GetMethod("None")));
}
Vars["padProbeHooks"] = h; Vars["padProbe"] = PadProbe.Calls;
return $"[{Instance}] pad probe armed";
