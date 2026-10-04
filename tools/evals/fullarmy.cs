using HarmonyLib;
public static class FullArmy {
  public static int Writes;
  public static void Pre() {
    try {
      var lc = P2.LaboCommon.pLaboCommonInstance_g;
      var lay = lc.laboSettingDataPtr_.gameSettingData.unitSettingData.playerUnitLayoutParam;
      var roster = lc.laboGlobalDataPtr_.gameGlobalData.unitGlobalData.unitAddingParam;
      int[][] groups = { new[]{0,1,2,15,16,17}, new[]{3,4,5,6,7,8}, new[]{9,10,11,12,13,14}, new[]{18,19,20} };
      var squads = lay.squadAddingParam; var units = lay.unitAddingParam; int u = 0;
      for (int k = 0; k < groups.Length; k++) {
        var dst = squads[k];
        if (k > 0) { var src = squads[0]; dst.CopyFrom(ref src, false); }
        dst.id = k; dst.isHero = 0; dst.unitNum = (short)groups[k].Length; dst.initUnitNum = (short)groups[k].Length;
        var first = roster[groups[k][0]];
        dst.unitParam.name = first.unitParam_dmy.name; dst.unitParam.crc = first.unitParam_dmy.crc;
        foreach (var r in groups[k]) { var ru = roster[r]; units[u].CopyFrom(ref ru, false); units[u].id = u; u++; }
      }
      lay.troopAddingParam.squadNum = groups.Length;
      if (Writes++ < 3) BepInEx.Logging.Logger.CreateLogSource("fullarmy").LogInfo($"full army written: {groups.Length} squads, {u} units");
    } catch (System.Exception e) { BepInEx.Logging.Logger.CreateLogSource("fullarmy").LogError(e.ToString()); }
  }
}
var t = typeof(PataCoop.CoopPlugin).Assembly.GetType("PataCoop.Coop.Armies");
var h = new Harmony("fullarmy"); h.Patch(AccessTools.Method(t, "PublishOwn"), new HarmonyMethod(typeof(FullArmy).GetMethod("Pre")) { priority = 800 });
h.Patch(AccessTools.Method(typeof(P2.Game.Game), "initialize"), new HarmonyMethod(typeof(FullArmy).GetMethod("Pre")) { priority = 800 });
Vars["fullarmy"] = h;
return $"[{Instance}] full army hook armed";
