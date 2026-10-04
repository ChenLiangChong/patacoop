using HarmonyLib;
using System.Linq;
using System.Reflection;
public static class NpcTrace {
  static readonly BepInEx.Logging.ManualLogSource L = BepInEx.Logging.Logger.CreateLogSource("npc");
  public static bool Window; public static int Lines;
  static string F => $"f{UnityEngine.Time.frameCount}";
  static void Log(string s) { if (Lines++ < 600) L.LogInfo(s); }
  public static void Cmd(MethodBase __originalMethod) {
    var n = __originalMethod.Name;
    if (!Window || n.StartsWith("get") || n.StartsWith("watch") || n.StartsWith("is") || n.StartsWith("check") || n == "callUnitWorkBlockLabel") return;
    Log($"cmd {__originalMethod.DeclaringType.Name}.{n} {F}"); }
  static readonly System.Collections.Generic.HashSet<System.IntPtr> Dying = new();
  public static void Exec(int __0) {
    if (__0 != 14 || Window) return;
    Window = true; Lines = 0;
    var sb = new System.Text.StringBuilder($"window opens: stele event {F} | troop:");
    foreach (var sq in P2.Game.Game.pGame_g.getUnitMng().unitTroopPtrArray_[0].unitSquadPtrList_) { int tag = sq.squadInfo_?.squadAddingParam?.rsv1 ?? 0; sb.Append($" {sq.squadInfo_?.uniqueId}:{sq.squadInfo_?.squadAddingParam?.unitParam?.name?.Replace("_01_01", "")}x{sq.unitBasePtrList_.Count}{((tag & unchecked((int)0xFFFF0000)) == 0x50430000 ? "/P" + ((tag & 0xFF) + 1) : "/mission")}"); }
    L.LogInfo(sb.ToString()); }
  public static void SquadKill(P2.Game.Unit.UnitSquad __instance) {
    if (__instance.pUnitTroop_?.troopInfo_?.troopType != P2.Game.Unit.TroopType.TroopType_Player || !Dying.Add(__instance.Pointer)) return;
    int tag = __instance.squadInfo_?.squadAddingParam?.rsv1 ?? 0;
    L.LogInfo($"player-side squad starts dying: uid {__instance.squadInfo_?.uniqueId} {__instance.squadInfo_?.squadAddingParam?.unitParam?.name} x{__instance.unitBasePtrList_?.Count} {((tag & unchecked((int)0xFFFF0000)) == 0x50430000 ? "P" + ((tag & 0xFF) + 1) : "mission")} {F}"); }
  public static void Lookup(int uniqueId, P2.Game.Unit.UnitSquad __result) { if (Window && uniqueId == 1) Log($"getSquad_UniqueId(1) -> {__result?.squadInfo_?.squadAddingParam?.unitParam?.name} tag={__result?.squadInfo_?.squadAddingParam?.rsv1:X} {F}"); }
  public static void Hp(P2.GameSystem.GameStatus __instance, MethodBase __originalMethod) {
    if (!Window) return;
    var actor = __instance.TryCast<P2.GameSystem.Actor.Status.ActorStatusObj>()?.pActorObj_?.TryCast<P2.Game.Unit.UnitBase>();
    var sq = actor?.pUnitSquad_; if (sq?.squadInfo_?.uniqueId != 1 || sq.pUnitTroop_?.troopInfo_?.troopType != P2.Game.Unit.TroopType.TroopType_Player) return;
    Log($"{__originalMethod.Name} on squad 1 ({sq.squadInfo_?.squadAddingParam?.unitParam?.name}) unit {actor.info_?.uniqueId}: hp now {__instance.getHitPoint()} {F}"); }
}
var h = new Harmony("npctrace"); var T = typeof(NpcTrace);
int hooked = 0, skipped = 0;
foreach (var cls in new[] { "P2.Game.Talk.CommandUnitBase", "P2.Game.Talk.CommandUnitSquad", "P2.Game.Talk.CommandGame", "P2.Game.Talk.CommandMap" }) {
  var shared = new System.Collections.Generic.HashSet<string>(PataCoop.Dev.Native.Find(cls + "::", 400).Split('\n').Where(l => l.Contains("(shared by")).Select(l => l.Substring(l.IndexOf("::") + 2).Split('/')[0]));
  var type = typeof(P2.Game.Talk.CommandGame).Assembly.GetType(cls);
  foreach (var m in type.GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly)) {
    if (m.GetParameters().Length != 1 || m.IsSpecialName || shared.Contains(m.Name)) { skipped++; continue; }
    try { h.Patch(m, new HarmonyMethod(T.GetMethod("Cmd"))); hooked++; } catch { skipped++; }
  }
}
h.Patch(AccessTools.Method(typeof(P2.Game.Map.Event.Script), "execute"), new HarmonyMethod(T.GetMethod("Exec")));
h.Patch(AccessTools.Method(typeof(P2.Game.Unit.UnitSquad), "kill"), new HarmonyMethod(T.GetMethod("SquadKill")));
h.Patch(AccessTools.Method(typeof(P2.Game.Unit.UnitTroop), "getSquad_UniqueId"), postfix: new HarmonyMethod(T.GetMethod("Lookup")));
h.Patch(AccessTools.Method(typeof(P2.GameSystem.GameStatus), "addHitPoint"), postfix: new HarmonyMethod(T.GetMethod("Hp")));
h.Patch(AccessTools.Method(typeof(P2.GameSystem.GameStatus), "setHitPoint"), postfix: new HarmonyMethod(T.GetMethod("Hp")));
Vars["npctrace"] = h;
return $"[{Instance}] npc trace armed: {hooked} script commands hooked, {skipped} skipped";
