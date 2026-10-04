using HarmonyLib;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
// In-game side of the Jev bot: every few frames writes the battle state to tmp/jev<i>.json, plays the
// drum command found in tmp/jev<i>.cmd ("<seq> <commandId>"), and can fast-forward the battle.
public static class JevDriver {
  public static int Instance, Every = 12, Speed = 1;
  static int _next, _seq = -1, _cmd = -1, _lastMoveFrame; static float _lastBase = float.NaN;
  static bool _busy; static System.IntPtr _game; static uint _lastHalf;
  static readonly List<(string text, int frame)> Texts = new();
  static string Dir => System.IO.Path.Combine(System.Environment.GetEnvironmentVariable("PATACOOP_WORK_WIN") ?? System.IO.Path.Combine(System.Environment.GetFolderPath(System.Environment.SpecialFolder.UserProfile), "PataCoop"), "tmp") + "\\";

  public static void Text(string __result) {
    if (string.IsNullOrWhiteSpace(__result) || __result.Length < 2) return;
    int f = UnityEngine.Time.frameCount;
    for (int i = 0; i < Texts.Count; i++) if (Texts[i].text == __result) { Texts[i] = (__result, f); return; }
    Texts.Add((__result, f)); if (Texts.Count > 40) Texts.RemoveAt(0);
  }

  static bool Flag(int bit) {
    try { var fb = P2.LaboCommon.pLaboCommonInstance_g.laboGlobalDataPtr_.talkData.flagBuffer; return (fb[bit >> 3] >> (bit & 7) & 1) != 0; } catch { return false; }
  }

  public static void Post(P2.Game.Game __instance) {
    if (_busy) return;
    var g = __instance;
    if (g.Pointer != _game) { _game = g.Pointer; _seq = -1; _cmd = -1; Texts.Clear(); _lastBase = float.NaN; } // a new battle: replay the current order
    bool playing = g.gamePhase_ == P2.Game.Game.GamePhase.GamePhase_Play && !g.isGameEndOrder_;
    if (playing) MarchTrace(g);
    if (playing && Speed > 1) { _busy = true; try { for (int k = 1; k < Speed; k++) g.update(80); } finally { _busy = false; } }
    if (UnityEngine.Time.frameCount < _next) return;
    _next = UnityEngine.Time.frameCount + Every;
    try { Command(g); Dump(g, playing); } catch (System.Exception e) { BepInEx.Logging.Logger.CreateLogSource("jev").LogWarning(e.Message); }
  }

  // one line per half beat: half-beat count, troop base x, the action our own drum is playing (for march distance per cycle)
  static void MarchTrace(P2.Game.Game g) {
    var bt = g.soundDirector_?.getBeatTimer(); var tr = g.getUnitMng()?.unitTroopPtrArray_?[0];
    if (bt == null || tr == null || bt.halfBeatCount_ == _lastHalf) return;
    _lastHalf = bt.halfBeatCount_;
    var inst = tr.aInstructionParam_; int me = P2.LaboCommon.pLaboCommonInstance_g?.laboSettingDataPtr_?.gameSettingData?.playerId ?? 0;
    System.IO.File.AppendAllText(Dir + $"march{Instance}.txt", $"{bt.halfBeatCount_} {tr.troopBasePos_[0]:F1} {(int)(inst != null && me < inst.Length ? inst[me].lastActionCmd_ : 0)}\n");
  }

  static void Command(P2.Game.Game g) {
    var path = Dir + $"jev{Instance}.cmd";
    if (!System.IO.File.Exists(path)) return;
    var parts = System.IO.File.ReadAllText(path).Split(' ');
    if (parts.Length < 2 || !int.TryParse(parts[0], out int seq) || !int.TryParse(parts[1], out int cmd) || seq == _seq) return;
    _seq = seq;
    _cmd = cmd;
    g.soundDirector_?.setAutoCommand(cmd, 99, true);
  }

  static void Dump(P2.Game.Game g, bool playing) {
    var st = new Dictionary<string, object>();
    st["frame"] = UnityEngine.Time.frameCount; st["playing"] = playing; st["phase"] = g.gamePhase_.ToString();
    var s = P2.LaboCommon.pLaboCommonInstance_g?.laboSettingDataPtr_?.gameSettingData;
    st["mission"] = s?.missionName ?? ""; st["mission_file"] = s?.missionFileName ?? "";
    st["drums"] = new[] { "PON", "PATA", "CHAKA", "DON" }.Where((d, i) => Flag(1617 + i)).ToArray();
    st["command"] = _cmd;
    var um = g.getUnitMng();
    if (um != null && um.unitTroopPtrArray_ != null && um.unitTroopPtrArray_.Count > 1) {
      var tr = um.unitTroopPtrArray_[0];
      float bx = tr.troopBasePos_[0];
      if (float.IsNaN(_lastBase) || System.Math.Abs(bx - _lastBase) > 2f) { _lastBase = bx; _lastMoveFrame = UnityEngine.Time.frameCount; }
      var cd = g.gameCameraPtr_?.cameraData_;
      if (cd != null) { st["camera_x"] = (int)cd.mat.m30; st["camera_fov"] = cd.fovY; }
      var squads = new List<object>();
      foreach (var sq in tr.unitSquadPtrList_) {
        int shp = 0, smax = 0, n = 0;
        foreach (var u in sq.unitBasePtrList_) { if (u.isEnd()) continue; n++; var i2 = u.pActorStatus_?.TryCast<P2.GameSystem.GameStatus>()?.info_; if (i2 != null) { shp += i2.hitPoint; smax += i2.maxHitPoint; } }
        if (n > 0) squads.Add(new { units = n, hp = shp, max_hp = smax });
      }
      st["our_squads"] = squads.ToArray();
      st["army_x"] = (int)bx; st["seconds_since_army_moved"] = (UnityEngine.Time.frameCount - _lastMoveFrame) / 60f;
      int alive = 0, hp = 0, maxHp = 0; float front = bx, back = bx; bool target = false; var xs = new List<float>();
      foreach (var sq in tr.unitSquadPtrList_) {
        if (sq.pTargetSquad_ != null || sq.pTargetGimmick_ != null) target = true;
        foreach (var u in sq.unitBasePtrList_) {
          if (u.isEnd()) continue; alive++;
          var inf = u.pActorStatus_?.TryCast<P2.GameSystem.GameStatus>()?.info_;
          if (inf != null) { hp += inf.hitPoint; maxHp += inf.maxHitPoint; }
          var m = u.pActorModel_?.TryCast<P2.Game.Unit.UnitModel>(); if (m != null) { front = System.Math.Max(front, m.pos_.x); back = System.Math.Min(back, m.pos_.x); xs.Add(m.pos_.x); }
        }
      }
      xs.Sort(); st["army_mid_x"] = xs.Count > 0 ? (int)xs[xs.Count / 2] : (int)bx; // where most of the army stands
      st["army_back_x"] = (int)back; st["army_units"] = alive; st["army_hp_percent"] = maxHp > 0 ? 100 * hp / maxHp : 0; st["army_front_x"] = (int)front; st["army_has_target_in_range"] = target;
      var enemies = new List<(int dist, string kind, int hp, int max)>(); int enemyHp = 0; var enemyX = new List<int>();
      foreach (var sq in um.unitTroopPtrArray_[1].unitSquadPtrList_)
        foreach (var u in sq.unitBasePtrList_) {
          if (u.isEnd()) continue;
          var m = u.pActorModel_?.TryCast<P2.Game.Unit.UnitModel>(); float x = m != null ? m.pos_.x : float.NaN;
          var einf = u.pActorStatus_?.TryCast<P2.GameSystem.GameStatus>()?.info_;
          enemyHp += einf?.hitPoint ?? 0;
          enemies.Add((float.IsNaN(x) ? 9999 : (int)(x - front), sq.squadInfo_?.squadAddingParam?.unitParam?.name ?? "?", einf?.hitPoint ?? 0, einf?.maxHitPoint ?? 0));
          if (!float.IsNaN(x)) enemyX.Add((int)x);
        }
      st["enemies"] = enemies.OrderBy(e => e.dist).Take(8).Select(e => new { distance_ahead = e.dist, x = (int)front + e.dist, kind = e.kind, hp = e.hp, max_hp = e.max }).ToArray();
      st["enemy_total_hp"] = enemyHp;
      var obstacles = new List<object>(); float goal = float.NaN;
      foreach (var gm in g.map_.gimmickManager_.gimmickList_) {
        if (gm == null) continue; var n = gm.layoutParam_?.name ?? "";
        if (n == "GOAL") goal = gm.position_.x;
        if (!gm.isEnable_ || n.StartsWith("BASE_") || n == "GOAL") continue;
        int ghp = gm.status_?.getHitPoint() ?? 0; float d = gm.position_.x - front;
        if (ghp > 0 && d > -600 && d < 900) obstacles.Add(new { name = n, distance_ahead = (int)d, x = (int)gm.position_.x, hp = ghp, max_hp = gm.status_?.info_?.maxHitPoint ?? ghp });
      }
      st["objects_ahead"] = obstacles.ToArray();
      if (!float.IsNaN(goal)) st["goal_distance"] = (int)(goal - bx);
    }
    int now = UnityEngine.Time.frameCount;
    st["recent_screen_text"] = Texts.Where(t => now - t.frame < 600).OrderByDescending(t => t.frame).Take(6).Select(t => t.text).ToArray();
    var json = JsonSerializer.Serialize(st);
    var path = Dir + $"jev{Instance}.json";
    System.IO.File.WriteAllText(path + ".tmp", json);
    System.IO.File.Move(path + ".tmp", path, true);
  }
}
JevDriver.Instance = Instance;
if (Vars.TryGetValue("jevdriver", out var old)) ((Harmony)old).UnpatchSelf();
var h = new Harmony("jevdriver");
h.Patch(AccessTools.Method(typeof(P2.Game.Game), "update", new[] { typeof(uint) }), postfix: new HarmonyMethod(typeof(JevDriver).GetMethod("Post")));
foreach (var t in new[] { typeof(P2.Localize.Manager), typeof(P2.Talk.CommandMessage), typeof(P2.System.Font.Localize) })
  foreach (var m in t.GetMethods()) if (m.Name == "getStringFromIndex" && m.ReturnType == typeof(string)) h.Patch(m, postfix: new HarmonyMethod(typeof(JevDriver).GetMethod("Text")));
Vars["jevdriver"] = h;
return $"[{Instance}] jev driver armed";
