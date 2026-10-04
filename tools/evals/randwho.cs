// Which script controllers roll the scripts' "rand" (CommandBasic.cmd_rand): their id_, the script pack they run and
// how often. Read: return string.Join("\n", ((System.Collections.Generic.Dictionary<string, int>)Vars["randWho"]).Select(kv => kv.Key + " x" + kv.Value));
using HarmonyLib;
public static class RandWho {
  public static readonly System.Collections.Generic.Dictionary<string, int> Seen = new();
  static string Fields(object o) {
    if (o == null) return "null"; var sb = new System.Text.StringBuilder();
    foreach (var p in o.GetType().GetProperties(System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.DeclaredOnly)) {
      object v = null; try { v = p.GetValue(o); } catch { } var s = v?.ToString() ?? "null"; if (s.Length > 24) s = s.Substring(0, 24); sb.Append($"{p.Name}={s} ");
    }
    return sb.ToString();
  }
  public static void Roll(P2.Pyd.Script.Talk.Controller talkController) {
    if (talkController == null) return;
    var k = $"id{talkController.id_} pack[{Fields(talkController.pack_)}]";
    Seen[k] = Seen.TryGetValue(k, out var n) ? n + 1 : 1;
  }
}
RandWho.Seen.Clear();
if (Vars.TryGetValue("randWhoHooks", out var old)) ((Harmony)old).UnpatchSelf();
var h = new Harmony("randWhoHooks");
h.Patch(AccessTools.Method(typeof(P2.Pyd.Script.Talk.CommandBasic), "cmd_rand"), prefix: new HarmonyMethod(typeof(RandWho).GetMethod("Roll")));
Vars["randWhoHooks"] = h; Vars["randWho"] = RandWho.Seen;
return $"[{Instance}] rand caller probe armed";
