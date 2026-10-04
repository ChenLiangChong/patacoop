using HarmonyLib;
using System.Reflection;
public static class GimTrace {
  static readonly BepInEx.Logging.ManualLogSource L = BepInEx.Logging.Logger.CreateLogSource("trace");
  static readonly FieldInfo Applying = typeof(PataCoop.CoopPlugin).Assembly.GetType("PataCoop.Coop.HitSync").GetField("Applying", BindingFlags.NonPublic | BindingFlags.Static);
  static readonly PropertyInfo Running = typeof(PataCoop.CoopPlugin).Assembly.GetType("PataCoop.Coop.MissionScripts")?.GetProperty("Running", BindingFlags.NonPublic | BindingFlags.Static | BindingFlags.Public);
  static string Ctx() => $"f{UnityEngine.Time.frameCount}{((bool)Applying.GetValue(null) ? " applying-host" : "")}{(_event >= 0 ? $" in-event-of-{_event}" : "")}";
  static int _event = -1, _hpBefore; static bool _enBefore;
  static P2.Game.Gimmick.Gimmick Find(int id) { foreach (var g in P2.Game.Game.pGame_g.map_.gimmickManager_.gimmickList_) if (g != null && g.id_ == id) return g; return null; }
  static string Name(P2.Game.Gimmick.Gimmick g) => g?.layoutParam_?.name ?? "?";
  public static void ExecPre(int __0, out int __state) { __state = _event; _event = __0; var g = Find(__0); _hpBefore = g?.status_?.getHitPoint() ?? -1; _enBefore = g?.isEnable_ ?? false; }
  public static void ExecPost(int __0, object __1, int __state) {
    var g = Find(__0); int hp = g?.status_?.getHitPoint() ?? -1;
    if (_hpBefore <= 0 || hp <= 0 || _enBefore != (g?.isEnable_ ?? false)) L.LogInfo($"event script of gimmick {__0} {Name(g)} ({__1}) hp {_hpBefore}->{hp} enabled {_enBefore}->{g?.isEnable_} {Ctx()}");
    _event = __state; }
  public static void KillPost(P2.Game.Gimmick.Gimmick __instance, bool __runOriginal) => L.LogInfo($"kill gimmick {__instance.id_} {Name(__instance)} hp={__instance.status_?.getHitPoint()} {(__runOriginal ? "RAN" : "blocked")} {Ctx()}");
  public static void ForceKillPost(P2.Game.Gimmick.Gimmick __instance, bool __runOriginal) => L.LogInfo($"forceKill gimmick {__instance.id_} {Name(__instance)} hp={__instance.status_?.getHitPoint()} {(__runOriginal ? "RAN" : "blocked")} {Ctx()}");
  public static void Drop(MethodBase __originalMethod) => L.LogInfo($"drop via {__originalMethod.Name} {Ctx()}");
  public static void Cmd(MethodBase __originalMethod) => L.LogInfo($"script command {__originalMethod.DeclaringType.Name}.{__originalMethod.Name} {Ctx()}");
  public static void SquadKill(P2.Game.Unit.UnitSquad __instance) { var a = __instance.squadInfo_?.squadAddingParam; int tag = a?.rsv1 ?? 0; L.LogInfo($"squad kill uid={__instance.squadInfo_?.uniqueId} {((tag & unchecked((int)0xFFFF0000)) == 0x50430000 ? "P" + ((tag & 0xFF) + 1) : "mission")} {a?.unitParam?.name} alive={__instance.unitBasePtrList_?.Count} troop={__instance.pUnitTroop_?.troopInfo_?.troopType} {Ctx()}"); }
  public static void UnitKill(P2.Game.Unit.UnitBase __instance) { var sq = __instance.pUnitSquad_; if (sq?.pUnitTroop_?.troopInfo_?.troopType != P2.Game.Unit.TroopType.TroopType_Player) return; L.LogInfo($"unit kill id={__instance.info_?.uniqueId} squad={sq?.squadInfo_?.uniqueId} {sq?.squadInfo_?.squadAddingParam?.unitParam?.name} hp={__instance.pActorStatus_?.getHitPoint()} {Ctx()}"); }
}
var h = new Harmony("gimtrace"); var T = typeof(GimTrace);
HarmonyMethod M(string n) => new HarmonyMethod(T.GetMethod(n));
h.Patch(AccessTools.Method(typeof(P2.Game.Map.Event.Script), "execute"), M("ExecPre"), M("ExecPost"));
h.Patch(AccessTools.Method(typeof(P2.Game.Gimmick.Gimmick), "kill"), postfix: M("KillPost"));
h.Patch(AccessTools.Method(typeof(P2.Game.Gimmick.Gimmick), "forceKill"), postfix: M("ForceKillPost"));
h.Patch(AccessTools.Method(typeof(P2.Game.Talk.CommandMap), "itemAdd"), M("Drop"));
h.Patch(AccessTools.Method(typeof(P2.Talk.CommandEffect), "generateItemEffect"), M("Drop"));
foreach (var (t, n) in new[] { (typeof(P2.Game.Talk.CommandGame), "killSquad"), (typeof(P2.Game.Talk.CommandGame), "killUnit"), (typeof(P2.Game.Talk.CommandMap), "killSquad"), (typeof(P2.Game.Talk.CommandMap), "killGimmick") })
  h.Patch(AccessTools.Method(t, n), M("Cmd"));
h.Patch(AccessTools.Method(typeof(P2.Game.Unit.UnitSquad), "kill"), M("SquadKill"));
h.Patch(AccessTools.Method(typeof(P2.Game.Unit.UnitBase), "kill"), M("UnitKill"));
Vars["gimtrace"] = h;
return $"[{Instance}] trace armed";
