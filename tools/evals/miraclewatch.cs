// While the miracle rhythm game runs (BeatCommander Mode_Miracl), log what its script expects at each
// half beat: the script analyzer's cursor command (header fields), the scorers' state and the drum hits.
// Read the log with: return (string)Vars["miracleLog"];
var log = new System.Collections.Generic.List<string>(); Vars["miracleLog"] = "waiting";
var d = P2.Game.Game.pGame_g.soundDirector_; var bc = d.beatCommander_; int frames = 0; bool seen = false; string last = "";
string Fields(object o) {
  if (o == null) return "null";
  var sb = new System.Text.StringBuilder();
  foreach (var p in o.GetType().GetProperties(System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.DeclaredOnly)) {
    object v = null; try { v = p.GetValue(o); } catch { }
    var s = v?.ToString() ?? "null"; if (s.Length > 40) s = s.Substring(0, 40);
    sb.Append($"{p.Name}={s} ");
  }
  return sb.ToString();
}
if (Vars.TryGetValue("miracleHits", out var oh)) ((HarmonyLib.Harmony)oh).UnpatchSelf();
var h = new HarmonyLib.Harmony("miracleHits");
h.Patch(HarmonyLib.AccessTools.Method(typeof(P2.Sound.BeatCommander), "procHit"), postfix: new HarmonyLib.HarmonyMethod(typeof(MiracleHits).GetMethod("Hit")));
Vars["miracleHits"] = h;
PataCoop.Dev.Pending.Add(() => {
  if (++frames > 60 * 240) { Vars["miracleLog"] = seen ? string.Join("\n", log) : "never entered"; return true; }
  bool on = bc.getMode() == P2.Sound.BeatCommander.Mode.Mode_Miracl;
  if (!on) { if (seen) { Vars["miracleLog"] = string.Join("\n", log); return true; } return false; }
  seen = true;
  var m = d.getMiracle()?.TryCast<P2.Sound.SubGame.Miracle.Miracle>(); var c = m?.command_; var bt = d.getBeatTimer();
  var cur = c?.key_?.cursor; var hdr = cur?.commandList?.header;
  string now = $"cursor[{Fields(hdr)}] main[{Fields(c?.mainScorer_)}] state={m?.state_}";
  if (now != last || MiracleHits.Pending.Count > 0) { log.Add($"hb{bt.halfBeatCount_} t{bt.tick_} {string.Join(" ", MiracleHits.Pending)} {now}"); last = now; MiracleHits.Pending.Clear(); }
  return false;
});
return "watching the miracle";
public static class MiracleHits {
  public static readonly System.Collections.Generic.List<string> Pending = new();
  public static void Hit(P2.System.Sound.Percussion hit, uint halfCount) => Pending.Add($"HIT:{hit.ToString().Replace("Percussion_", "")}@{halfCount}");
}
