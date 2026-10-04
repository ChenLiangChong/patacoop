using HarmonyLib;
using System.Collections.Generic;
using System.Linq;
// Drum timing probe. Plays one PATA PATA PATA PON through the game's own pad (the way a player's keys
// arrive), starting OFFSET beats after a bar line where drums can start (DrumProbe.Offset, -1 = do not
// drum), and writes one line
// per battle step to tmp/drum<i>.txt: beat, army base x, where the units stand, the troop's commands, and
// the drum hits and command events as they happen.
public static class DrumProbe {
  public static int Instance, Offset = -1, Steps, Hold = 1;
  public static bool On;
  static readonly List<string> Lines = new();
  static readonly List<string> Events = new();
  static readonly List<string> SquadLines = new();
  static uint _lastHalf = uint.MaxValue; static int _barHalf = -1, _startHalf = -1, _pressed; static string _lastState = "";
  static string Dir => System.IO.Path.Combine(System.Environment.GetEnvironmentVariable("PATACOOP_WORK_WIN") ?? System.IO.Path.Combine(System.Environment.GetFolderPath(System.Environment.SpecialFolder.UserProfile), "PataCoop"), "tmp") + "\\";

  // a drum plan: (half beats after the start, pad bit), pressed one frame each, the start being the next drum-start bar
  static readonly List<(int Half, uint Key)> Plan = new();
  static int _planStart = -1;
  // keys held for the next battle step only (read through P2.System.Pad.Pad.stand, as real keys are)
  static uint _inject; static int _step, _halfStep, _lastInjectStep = -1;
  static int _standCalls, _standFrame = -1;
  public static void Stand(uint key, ref bool __result) {
    if (UnityEngine.Time.frameCount != _standFrame) { _standFrame = UnityEngine.Time.frameCount; _standCalls = 0; }
    _standCalls++;
    if (_inject != 0) Events.Add($"stand{key:X}@{_standCalls}:{((_inject & key) != 0 ? "DOWN" : "-")}");
    if ((_inject & key) != 0) __result = true;
  }
  /// <summary>plan: "h:K,h:K,..." with K one of A (PATA) S (DON) W (CHAKA) D (PON); returns how many presses</summary>
  public static int Play(string plan) {
    Plan.Clear(); _planStart = -1;
    foreach (var part in plan.Split(',')) {
      var kv = part.Split(':'); if (kv.Length != 2) continue;
      uint key = kv[1] switch { "A" => PataCoop.Dev.VirtualPad.Pata, "S" => PataCoop.Dev.VirtualPad.Don, "W" => PataCoop.Dev.VirtualPad.Chaka, _ => PataCoop.Dev.VirtualPad.Pon };
      Plan.Add((int.Parse(kv[0]), key));
    }
    return Plan.Count;
  }
  // drums for the miracle rhythm game: half beats on the miracle's own beat timer (it starts again from 0)
  static readonly List<(int Half, uint Key)> MiraclePlan = new();
  public static int PlayMiracle(string plan) {
    MiraclePlan.Clear();
    foreach (var part in plan.Split(',')) {
      var kv = part.Split(':'); if (kv.Length != 2) continue;
      uint key = kv[1] switch { "A" => PataCoop.Dev.VirtualPad.Pata, "S" => PataCoop.Dev.VirtualPad.Don, "W" => PataCoop.Dev.VirtualPad.Chaka, _ => PataCoop.Dev.VirtualPad.Pon };
      MiraclePlan.Add((int.Parse(kv[0]), key));
    }
    return MiraclePlan.Count;
  }
  public static void Arm(int offset) { Lines.Clear(); Events.Clear(); SquadLines.Clear(); _barHalf = _startHalf = -1; _pressed = 0; _lastState = ""; Offset = offset; Steps = 0; On = true; }
  public static void Note(string e) { if (On) Events.Add(e); }
  public static string Dump() { On = false; System.IO.File.WriteAllLines(Dir + $"drum{Instance}.txt", Lines); System.IO.File.WriteAllLines(Dir + $"squads{Instance}.txt", SquadLines); return $"[{Instance}] {Lines.Count} steps, start half beat {_startHalf}, pressed {_pressed}"; }

  public static void Post(P2.Game.Game __instance) {
    if (!On) return;
    var g = __instance; var bt = g.soundDirector_?.getBeatTimer(); var um = g.getUnitMng();
    if (bt == null || um == null || g.gamePhase_ != P2.Game.Game.GamePhase.GamePhase_Play) return;
    var tr = um.unitTroopPtrArray_[0];
    uint hb = bt.halfBeatCount_;
    // with a fixed rhythm the drums can only start on every other bar line: the one the drum state
    // machine waits for ("None", from half a beat before it)
    var bcs = g.soundDirector_.beatCommander_.getState().ToString().Replace("State_", "");
    if (bcs == "None" && _lastState != "None") _barHalf = (int)hb + 1;
    if (bcs != _lastState) Events.Add("state:" + bcs);
    _lastState = bcs;
    if (Offset >= 0 && _startHalf < 0 && _barHalf >= 0) _startHalf = _barHalf + 16 + 2 * Offset;
    // start on a beat (an even half beat) a couple of beats from now
    _step++; _inject = 0;
    if (hb != _lastHalf) _halfStep = _step;
    if (Plan.Count > 0 && _planStart < 0) _planStart = ((int)hb + 4) & ~1;
    // the last step of the half beat before (the beat timer knows when the next line comes): the key is
    // down in the step that reaches the line
    bool miracle = g.soundDirector_.beatCommander_.getMode() == P2.Sound.BeatCommander.Mode.Mode_Miracl;
    if (miracle && MiraclePlan.Count > 0 && bt.getNextAcrossHalfFrame() == 1)
      foreach (var p in MiraclePlan) if ((int)hb == p.Half - 1) { _inject |= p.Key; Events.Add($"miracle press->{p.Half}"); }
    if (!miracle && _planStart >= 0 && bt.getNextAcrossHalfFrame() == 1 && _step != _lastInjectStep && (_lastInjectStep = _step) >= 0)
      foreach (var p in Plan) if ((int)hb == _planStart + p.Half - 1) { _inject |= p.Key; Events.Add($"press->{p.Half}"); }
    if (hb != _lastHalf && _startHalf >= 0 && _pressed < 4 && (int)hb == _startHalf + 2 * _pressed - 1) {
      // a half beat (15 steps) before the beat line: the press is read in the step that reaches it
      uint key = _pressed < 3 ? PataCoop.Dev.VirtualPad.Pata : PataCoop.Dev.VirtualPad.Pon;
      PataCoop.Dev.VirtualPad.Press(key, Hold, 15);
      Events.Add($"press{_pressed + 1}->hb{_startHalf + 2 * _pressed}");
      _pressed++;
    }
    if (hb != _lastHalf) {
      // every squad once per half beat: owner, its control, the troop position it walks by, where its units stand
      var sb = new System.Text.StringBuilder($"{hb}");
      foreach (var sq in tr.unitSquadPtrList_) {
        int tag = (int)(sq?.squadInfo_?.squadAddingParam?.rsv1 ?? 0); int owner = (tag & ~0xF) == 0x50430000 ? tag & 0xF : 9;
        float us = 0; int un = 0;
        foreach (var u in sq.unitBasePtrList_) { if (u == null || u.isEnd()) continue; var m = u.pActorModel_?.TryCast<P2.Game.Unit.UnitModel>(); if (m == null) continue; us += m.pos_.x; un++; }
        var t = sq.squadCtrl_?.tpdSquadCtrlTarget_; var tb = t?.tpdTroopBasePos;
        sb.Append($" p{owner}:{(t == null ? "-" : t.ctrlType.ToString().Replace("SquadCtrlType_", ""))}:{(tb != null && tb.Length > 0 ? tb[0] : float.NaN):F0}:{(un > 0 ? us / un : float.NaN):F0}:{(int)(sq.pInstParam_?.lastActionCmd_ ?? 0)}");
      }
      SquadLines.Add(sb.ToString());
    }
    _lastHalf = hb;
    float sum = 0; int n = 0;
    foreach (var sq in tr.unitSquadPtrList_) foreach (var u in sq.unitBasePtrList_) {
      if (u == null || u.isEnd()) continue;
      var m = u.pActorModel_?.TryCast<P2.Game.Unit.UnitModel>(); if (m == null) continue;
      sum += m.pos_.x; n++;
    }
    var inst = tr.aInstructionParam_;
    string cmds = inst == null ? "-" : string.Join(",", Enumerable.Range(0, System.Math.Min(2, inst.Length)).Select(p => inst[p] == null ? "-" : ((int)inst[p].lastActionCmd_).ToString()));
    Lines.Add($"{UnityEngine.Time.frameCount} {hb} {bt.tick_} {(bt.isAcrossBarLine_ ? "BAR" : bt.isAcrossBeatLine_ ? "beat" : "-")}{bt.getNextAcrossHalfFrame()} {tr.troopBasePos_[0]:F2} {(n > 0 ? sum / n : 0):F2} {cmds} {Armies(tr)} {Counts()} {string.Join(";", Events)}");
    Events.Clear();
    Steps++;
  }

  // where each player's army stands (mean x of its units), "p0:x,p1:x"
  static string Armies(P2.Game.Unit.UnitTroop tr) {
    var sum = new float[4]; var cnt = new int[4];
    foreach (var sq in tr.unitSquadPtrList_) {
      int tag = (int)(sq?.squadInfo_?.squadAddingParam?.rsv1 ?? 0), p = tag & 0xF;
      if ((tag & ~0xF) != 0x50430000 || p > 3) continue; // Armies.OwnerTag | player
      foreach (var u in sq.unitBasePtrList_) { if (u == null || u.isEnd()) continue; var m = u.pActorModel_?.TryCast<P2.Game.Unit.UnitModel>(); if (m == null) continue; sum[p] += m.pos_.x; cnt[p]++; }
    }
    return string.Join(",", Enumerable.Range(0, 2).Select(p => cnt[p] > 0 ? (sum[p] / cnt[p]).ToString("F2") : "-"));
  }
  public static void Hit(P2.System.Sound.Percussion hit, uint halfCount, uint beatCounts) => Note($"hit:{hit}:hc{halfCount}:bc{beatCounts}");
  public static void Fix(P2.System.Sound.CommandEvent commandEvent) => Note($"FIX:{Cmd(commandEvent)}");
  public static void Exec(P2.System.Sound.CommandEvent commandEvent) => Note($"EXEC:{Cmd(commandEvent)}");
  public static void Finish(P2.System.Sound.CommandEvent commandEvent) => Note($"FINISH:{Cmd(commandEvent)}");
  public static void Receive(P2.Game.Unit.UnitTroop __instance, P2.Game.Unit.InstructionParam.CommandParam pCommandParam, int playerId) {
    if ((int)(__instance.troopInfo_?.troopType ?? (P2.Game.Unit.TroopType)(-1)) == 0) Note($"recv:p{playerId}:{(int)(pCommandParam?.command ?? 0)}");
  }
  // game packets: the drum ones by name, the rest counted per battle step
  static int _added, _taken;
  public static void Add(P2.Game.Packet.GamePacket pGamePacket) { int id = pGamePacket?.header?.id ?? 0; if (id == 1 || id == 2) Note($"send:{id}"); else _added++; }
  public static void Proc(P2.Game.Packet.GamePacket pGamePacket) { int id = pGamePacket?.header?.id ?? 0; if (id == 1 || id == 2) Note($"got:{id}"); else _taken++; }
  public static string Counts() { var c = $"+{_added}/-{_taken}"; _added = _taken = 0; return c; }
  static string Cmd(P2.System.Sound.CommandEvent e) {
    try { var c = e?.command_; return c == null ? "?" : string.Join("/", Enumerable.Range(0, c.Length).Select(i => c[i] == null ? "-" : c[i].id.ToString())) + $" type{e.type_}"; } catch { return "?"; }
  }
}
DrumProbe.Instance = Instance;
if (Vars.TryGetValue("drumprobe", out var old)) ((Harmony)old).UnpatchSelf();
var h = new Harmony("drumprobe");
h.Patch(AccessTools.Method(typeof(P2.Game.Game), "update", new[] { typeof(uint) }), postfix: new HarmonyMethod(typeof(DrumProbe).GetMethod("Post")));
h.Patch(AccessTools.Method(typeof(P2.Sound.BeatCommander), "procHit"), postfix: new HarmonyMethod(typeof(DrumProbe).GetMethod("Hit")));
h.Patch(AccessTools.Method(typeof(P2.System.Sound.BeatCommandTransmitter), "notifyFixCommand"), postfix: new HarmonyMethod(typeof(DrumProbe).GetMethod("Fix")));
h.Patch(AccessTools.Method(typeof(P2.System.Sound.BeatCommandTransmitter), "notifyExecCommand"), postfix: new HarmonyMethod(typeof(DrumProbe).GetMethod("Exec")));
h.Patch(AccessTools.Method(typeof(P2.System.Sound.BeatCommandTransmitter), "notifyFinishCommand"), postfix: new HarmonyMethod(typeof(DrumProbe).GetMethod("Finish")));
h.Patch(AccessTools.Method(typeof(P2.Game.Unit.UnitTroop), "receiveCommand"), postfix: new HarmonyMethod(typeof(DrumProbe).GetMethod("Receive")));
h.Patch(AccessTools.Method(typeof(P2.Game.Packet.PacketMng), "addPacket"), postfix: new HarmonyMethod(typeof(DrumProbe).GetMethod("Add")));
h.Patch(AccessTools.Method(typeof(P2.Game.Packet.PacketMng), "procGamePacket"), prefix: new HarmonyMethod(typeof(DrumProbe).GetMethod("Proc")));
h.Patch(AccessTools.Method(typeof(P2.System.Pad.Pad), "stand"), postfix: new HarmonyMethod(typeof(DrumProbe).GetMethod("Stand")));
Vars["drumprobe"] = h;
Vars["DrumProbe"] = typeof(DrumProbe); // the current one (every reload compiles a new type)
return $"[{Instance}] drum probe armed";
