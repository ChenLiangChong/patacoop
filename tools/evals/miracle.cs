// Miracle test, one copy: waits for fever and a finished command, stops the auto drum, drums the miracle
// (DON - DONDON - DONDON) in the next drum bar through the game's pad, and logs every half beat: the drum
// mode, the beat timer (tick, half beats), the drum state and the miracle rhythm game's cursor and score.
// The rhythm game is then played as its script asks: every 16 half beats (on its own beat timer, from 0) a round
// whose second half lists the drums to repeat (hit bits: 1 PON, 2 DON, 4 PATA, 8 CHAKA). Vars["miraclePlan"] =
// "h:K,..." (K: A PATA, S DON, W CHAKA, D PON) drums that instead, "none" drums nothing. Log: return string.Join("\n", (System.Collections.Generic.List<string>)Vars["miracleTestLog"]);
using HarmonyLib;
using System.Collections.Generic;
public static class MiracleTest {
  public static readonly List<string> Log = new();
  public static readonly List<(int Half, uint Key)> Don = new(), Rhythm = new();
  public static int Target = -1, Steps; public static bool Armed, AutoRhythm;
  /// <summary>Called when the miracle drums are planned (to stop whatever else drums).</summary>
  public static System.Action OnTrigger;
  static uint _inject; static uint _lastHb = uint.MaxValue; static string _lastMode = "", _lastCursor = "";
  static readonly List<string> Events = new();
  static P2.Sound.Director D => P2.Game.Game.pGame_g?.soundDirector_;
  public static List<(int, uint)> Parse(string plan) {
    var l = new List<(int, uint)>();
    foreach (var part in (plan ?? "").Split(',')) {
      var kv = part.Split(':'); if (kv.Length != 2) continue;
      l.Add((int.Parse(kv[0]), kv[1] switch { "A" => PataCoop.Dev.VirtualPad.Pata, "S" => PataCoop.Dev.VirtualPad.Don, "W" => PataCoop.Dev.VirtualPad.Chaka, _ => PataCoop.Dev.VirtualPad.Pon }));
    }
    return l;
  }
  public static void Stand(uint key, ref bool __result) { if ((_inject & key) != 0) __result = true; }
  public static void Exec(P2.System.Sound.CommandEvent commandEvent) {
    var d = D; if (d == null) return;
    var c = commandEvent?.command_; uint hb = d.getBeatTimer().halfBeatCount_;
    Events.Add($"exec:{(c == null || c.Length == 0 || c[0] == null ? "?" : c[0].id.ToString())}");
    if (Armed && Target < 0 && d.beatCommander_.evaluator_.bgmMood_.ToString() == "Mood_High") {
      Target = (int)hb + 8; // the response bar, then the next drum bar
      d.autoKey_.cancel();
      System.AppDomain.CurrentDomain.SetData("jevHold", true); // the Jev bot's drums wait until the miracle is over
      OnTrigger?.Invoke();
      Events.Add($"miracle drums at hb{Target}");
    }
  }
  public static void Hit(P2.System.Sound.Percussion hit, uint halfCount) => Events.Add($"hit:{hit.ToString().Replace("Percussion_", "")}@{halfCount}");
  // the miracle rhythm game's own judgment of each drum (typed arguments only: boxing every argument of these
  // hot native methods is not safe through the interop layer)
  public static void AnalyzerHit(int percussion, P2.Sound.SubGame.Script.Analyzer.HitResult __result) { if (percussion != 0 || __result != P2.Sound.SubGame.Script.Analyzer.HitResult.HitResult_None) Events.Add($"acheck:{percussion}={__result}"); }
  static string Fields(object o) {
    if (o == null) return "-";
    var sb = new System.Text.StringBuilder();
    foreach (var p in o.GetType().GetProperties(System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.DeclaredOnly)) {
      object v = null; try { v = p.GetValue(o); } catch { }
      var s = v?.ToString() ?? "null"; if (s.Length > 24) s = s.Substring(0, 24); sb.Append($"{p.Name}={s} ");
    }
    return sb.ToString();
  }
  // a deep look at an object: its properties, arrays expanded (a few levels)
  public static string Deep(object o, int depth) {
    if (o == null) return "null";
    var t = o.GetType();
    if (o is string || t.IsPrimitive || t.IsEnum) return o.ToString();
    if (depth <= 0) return t.Name;
    var len = t.GetProperty("Length") ?? t.GetProperty("Count");
    var item = t.GetProperty("Item");
    if (len != null && item != null && item.GetIndexParameters().Length == 1) {
      int n = System.Convert.ToInt32(len.GetValue(o)); var sb2 = new System.Text.StringBuilder($"[{n}: ");
      for (int i = 0; i < System.Math.Min(n, 24); i++) { object v = null; try { v = item.GetValue(o, new object[] { i }); } catch { } sb2.Append(Deep(v, depth - 1)).Append(", "); }
      return sb2.Append("]").ToString();
    }
    var sb = new System.Text.StringBuilder(t.Name + "{");
    foreach (var p in t.GetProperties(System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.DeclaredOnly)) {
      if (p.GetIndexParameters().Length > 0) continue;
      object v = null; try { v = p.GetValue(o); } catch { }
      sb.Append($"{p.Name}={Deep(v, depth - 1)} ");
    }
    return sb.Append("}").ToString();
  }
  static bool _dumped, _seen;
  // every step's beat timer for a few steps around a change of drum mode
  static readonly System.Collections.Generic.Queue<string> Recent = new(); static int _after;
  // Each battle step decides its own presses before it runs: a key is down for exactly the step that reaches
  // the half-beat line, also when a frame runs two steps (catch-up, a fast-forwarding tool).
  public static void Pre(P2.Game.Game __instance) {
    var g = __instance; var d = g.soundDirector_; var bt = d?.getBeatTimer();
    _inject = 0;
    if (bt == null || g.gamePhase_ != P2.Game.Game.GamePhase.GamePhase_Play || bt.getNextAcrossHalfFrame() != 1) return;
    uint hb = bt.halfBeatCount_;
    bool miracle = d.beatCommander_.getMode() == P2.Sound.BeatCommander.Mode.Mode_Miracl;
    if (!miracle && Target >= 0)
      foreach (var p in Don) if ((int)hb == Target + p.Half - 1) { _inject |= p.Key; Events.Add($"don->{Target + p.Half}"); }
    if (miracle && Rhythm.Count > 0)
      foreach (var p in Rhythm) if ((int)hb == p.Half - 1) { _inject |= p.Key; Events.Add($"rhythm->{p.Half}"); }
    if (miracle && AutoRhythm) {
      // the script's drum for the next half beat: round (hb + 1) / 16, its command (hb + 1) % 16
      var lists = d.getMiracle()?.TryCast<P2.Sound.SubGame.Miracle.Miracle>()?.command_?.key_?.cursor?.commandGrup?.commandList;
      int next = (int)hb + 1, round = next / 16, at = next % 16;
      var cmd = lists != null && round < lists.Length ? lists[round]?.command : null;
      if (cmd != null && at < cmd.Length && cmd[at] != null && cmd[at].act2 == 1 && cmd[at].hit[0] > 0) {
        int bits = cmd[at].hit[0];
        uint key = (bits & 2) != 0 ? PataCoop.Dev.VirtualPad.Don : (bits & 1) != 0 ? PataCoop.Dev.VirtualPad.Pon : (bits & 4) != 0 ? PataCoop.Dev.VirtualPad.Pata : PataCoop.Dev.VirtualPad.Chaka;
        _inject |= key; Events.Add($"auto->{next}:{bits}");
      }
    }
  }
  public static void Post(P2.Game.Game __instance) {
    var g = __instance; var d = g.soundDirector_; var bt = d?.getBeatTimer();
    if (bt == null || g.gamePhase_ != P2.Game.Game.GamePhase.GamePhase_Play) return;
    Steps++; _inject = 0; // the step that read the presses has run
    uint hb = bt.halfBeatCount_;
    var bc = d.beatCommander_; var mode = bc.getMode().ToString().Replace("Mode_", "");
    bool miracle = mode == "Miracl";
    var stepLine = $"step{Steps} {mode} tick{bt.tick_} hb{hb}";
    if (miracle) _seen = true;
    // the bot drums again once the miracle is over, or if it never started
    if ((_lastMode == "Miracl" && !miracle) || (!_seen && Target >= 0 && hb > Target + 16)) System.AppDomain.CurrentDomain.SetData("jevHold", false);
    if (mode != _lastMode && _lastMode != "") { Log.Add("AROUND " + string.Join(" | ", Recent) + " || " + stepLine); _after = 4; }
    else if (_after > 0) { _after--; Log.Add("AROUND+ " + stepLine); }
    Recent.Enqueue(stepLine); if (Recent.Count > 4) Recent.Dequeue();
    string cursor = "";
    if (miracle) {
      var m = d.getMiracle()?.TryCast<P2.Sound.SubGame.Miracle.Miracle>(); var c = m?.command_;
      cursor = $"state={m?.state_} cur[{Fields(c?.key_?.cursor?.commandList?.header)}] score[{Fields(c?.mainScorer_)}]";
      if (!_dumped) { _dumped = true; try { Log.Add("DUMP cursor " + Deep(c?.key_?.cursor, 4)); Log.Add("DUMP key " + Deep(c?.key_, 2)); Log.Add("DUMP command " + Deep(c, 2)); } catch (System.Exception e) { Log.Add("DUMP failed " + e.Message); } }
    }
    if (hb != _lastHb || mode != _lastMode || cursor != _lastCursor || Events.Count > 0) {
      Log.Add($"s{Steps} {mode} hb{hb} tick{bt.tick_} {bc.getState().ToString().Replace("State_", "")} mood={bc.evaluator_.bgmMood_.ToString().Replace("Mood_", "")} {string.Join(" ", Events)}{(cursor != _lastCursor ? " " + cursor : "")}");
      Events.Clear();
      if (Log.Count > 4000) Log.RemoveRange(0, 1000);
    }
    _lastHb = hb; _lastMode = mode; _lastCursor = cursor;
  }
}
MiracleTest.OnTrigger = () => Vars["cycleStop"] = true; // the drum cycle (tools/evals/cycle.cs) stops
typeof(MiracleTest).GetField("_seen", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static).SetValue(null, false);
MiracleTest.Log.Clear(); MiracleTest.Target = -1; typeof(MiracleTest).GetField("_dumped", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static).SetValue(null, false); MiracleTest.Steps = 0; MiracleTest.Armed = true;
MiracleTest.Don.Clear(); MiracleTest.Don.AddRange(MiracleTest.Parse("0:S,2:S,3:S,5:S,6:S"));
MiracleTest.Rhythm.Clear(); MiracleTest.AutoRhythm = !Vars.TryGetValue("miraclePlan", out var mp);
if (mp is string plan && plan != "none") MiracleTest.Rhythm.AddRange(MiracleTest.Parse(plan));
if (Vars.TryGetValue("miracleTest", out var old)) ((Harmony)old).UnpatchSelf();
var h = new Harmony("miracleTest");
h.Patch(AccessTools.Method(typeof(P2.Game.Game), "update", new[] { typeof(uint) }), prefix: new HarmonyMethod(typeof(MiracleTest).GetMethod("Pre")), postfix: new HarmonyMethod(typeof(MiracleTest).GetMethod("Post")));
h.Patch(AccessTools.Method(typeof(P2.System.Pad.Pad), "stand"), postfix: new HarmonyMethod(typeof(MiracleTest).GetMethod("Stand")));
h.Patch(AccessTools.Method(typeof(P2.System.Sound.BeatCommandTransmitter), "notifyExecCommand"), postfix: new HarmonyMethod(typeof(MiracleTest).GetMethod("Exec")));
h.Patch(AccessTools.Method(typeof(P2.Sound.BeatCommander), "procHit"), postfix: new HarmonyMethod(typeof(MiracleTest).GetMethod("Hit")));
h.Patch(AccessTools.Method(typeof(P2.Sound.SubGame.Script.Analyzer), "hitCheck"), postfix: new HarmonyMethod(typeof(MiracleTest).GetMethod("AnalyzerHit")));
Vars["miracleTest"] = h; Vars["miracleTestLog"] = MiracleTest.Log;
return $"[{Instance}] miracle test armed ({(MiracleTest.AutoRhythm ? "rhythm game played as its script asks" : $"{MiracleTest.Rhythm.Count} rhythm drums planned")})";
