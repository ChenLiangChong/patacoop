// Plays drum commands in blocks with the game's own auto drum, over and over (a fixed plan, no decisions):
// Vars["cycle"] = "3x3,2x3" before running means attack three times, then defend three times, and again
// (command ids: 1 march, 2 defend, 3 attack, 4 retreat). Holds the Jev driver's drums meanwhile.
// Vars["cycleStartText"] = "攻擊開始" waits for a text containing that before the first block, and starts again
// from the first block whenever it shows again (to keep in step with a mission's own rounds).
// Log: return string.Join("\n", (System.Collections.Generic.List<string>)Vars["cycleLog"]);  stop: Vars["cycleStop"] = true;
using HarmonyLib;
var plan = Vars.TryGetValue("cycle", out var c) ? (string)c : "3x3,2x3";
var blocks = new System.Collections.Generic.List<(int Cmd, uint Count)>();
foreach (var part in plan.Split(',')) { var kv = part.Split('x'); blocks.Add((int.Parse(kv[0]), uint.Parse(kv[1]))); }
// the Jev driver's drums wait (miracle.cs lets them go after its miracle; stopping the cycle by hand does not:
// System.AppDomain.CurrentDomain.SetData("jevHold", false))
System.AppDomain.CurrentDomain.SetData("jevHold", true);
var log = new System.Collections.Generic.List<string>(); Vars["cycleLog"] = log; Vars["cycleStop"] = false;
CycleEvents.Log = log; CycleEvents.StartText = Vars.TryGetValue("cycleStartText", out var st) ? (string)st : null; CycleEvents.Restart = false;
if (Vars.TryGetValue("cycleHooks", out var old)) ((Harmony)old).UnpatchSelf();
var h = new Harmony("cycleHooks");
h.Patch(AccessTools.Method(typeof(P2.System.Sound.BeatCommandTransmitter), "notifyExecCommand"), postfix: new HarmonyMethod(typeof(CycleEvents).GetMethod("Exec")));
foreach (var t in new[] { typeof(P2.Localize.Manager), typeof(P2.Talk.CommandMessage), typeof(P2.System.Font.Localize) })
  foreach (var m in t.GetMethods()) if (m.Name == "getStringFromIndex" && m.ReturnType == typeof(string)) h.Patch(m, postfix: new HarmonyMethod(typeof(CycleEvents).GetMethod("Text")));
Vars["cycleHooks"] = h;
int at = -1, waiting = 0, lastHp = -1; var game = P2.Game.Game.pGame_g;
if (CycleEvents.StartText == null) game.soundDirector_.autoKey_.cancel(); // whatever the auto drum was playing (a march to the start may go on)
PataCoop.Dev.Pending.Add(() => {
  var g = P2.Game.Game.pGame_g;
  if (g == null || g.Pointer != game.Pointer || (bool)Vars["cycleStop"]) { h.UnpatchSelf(); return true; }
  if (g.gamePhase_ != P2.Game.Game.GamePhase.GamePhase_Play) return false;
  var d = g.soundDirector_; var a = d.autoKey_;
  // our army's hit points: when it gets hurt (to see the enemy's rhythm)
  int hp = 0; foreach (var sq in g.getUnitMng().unitTroopPtrArray_[0].unitSquadPtrList_) foreach (var u in sq.unitBasePtrList_) if (!u.isEnd()) hp += u.pActorStatus_?.getHitPoint() ?? 0;
  if (lastHp >= 0 && hp < lastHp) log.Add($"{System.DateTime.Now:HH:mm:ss.f} hb{d.getBeatTimer().halfBeatCount_} hurt {lastHp - hp} (left {hp})");
  lastHp = hp;
  if (CycleEvents.StartText != null && at < 0 && !CycleEvents.Restart) return false; // not yet: the mission's round has not begun
  if (CycleEvents.Restart) { CycleEvents.Restart = false; a.cancel(); at = -1; waiting = 0; log.Add($"{System.DateTime.Now:HH:mm:ss.f} hb{d.getBeatTimer().halfBeatCount_} round starts: first block"); }
  // a new order starts playing only at the next bar where drums may start
  if (a.isPlay_) { waiting = 0; return false; }
  if (waiting > 0) { waiting--; return false; }
  at = (at + 1) % blocks.Count;
  d.setAutoCommand(blocks[at].Cmd, blocks[at].Count + 1, true); // its count includes the order itself
  waiting = 600;
  log.Add($"{System.DateTime.Now:HH:mm:ss.f} hb{d.getBeatTimer().halfBeatCount_} play {blocks[at].Cmd}x{blocks[at].Count}");
  return false;
});
return $"[{Instance}] cycle {plan}";
public static class CycleEvents {
  public static System.Collections.Generic.List<string> Log;
  public static string StartText; public static bool Restart;
  static readonly System.Collections.Generic.Dictionary<string, int> Seen = new();
  // texts the game shows (once each until they have been gone for five seconds)
  public static void Text(string __result) {
    if (string.IsNullOrWhiteSpace(__result) || __result.Length < 2) return;
    int f = UnityEngine.Time.frameCount;
    if (!Seen.TryGetValue(__result, out int last) || f - last > 300) {
      Log?.Add($"{System.DateTime.Now:HH:mm:ss.f} hb{P2.Game.Game.pGame_g?.soundDirector_?.getBeatTimer()?.halfBeatCount_} text {__result.Replace("\n", " ")}");
      if (StartText != null && __result.Contains(StartText)) Restart = true;
    }
    Seen[__result] = f;
  }
  public static void Exec(P2.System.Sound.CommandEvent commandEvent) {
    try {
      var c = commandEvent?.command_; var hb = P2.Game.Game.pGame_g?.soundDirector_?.getBeatTimer()?.halfBeatCount_;
      Log?.Add($"{System.DateTime.Now:HH:mm:ss.f} hb{hb} exec {(c == null || c.Length == 0 || c[0] == null ? "?" : c[0].id.ToString())}");
    } catch { }
  }
}
