using HarmonyLib;
public static class FullArmy {
  public static int Writes;
  public static void Pre() {
    try {
      var lc = P2.LaboCommon.pLaboCommonInstance_g;
      var lay = lc.laboSettingDataPtr_.gameSettingData.unitSettingData.playerUnitLayoutParam;
      var roster = lc.laboGlobalDataPtr_.gameGlobalData.unitGlobalData.unitAddingParam;
      var squads = lay.squadAddingParam; var units = lay.unitAddingParam;
      // the hero (squad 0 of the save's formation, one unit) stays; the other squads are filled from the roster
      bool hero = squads[0].isHero != 0;
      int[][] groups = hero
        ? new[] { new[]{3,4,5,6,7,8}, new[]{9,10,11,12,13,14}, new[]{0,1,2,15,16,17} }
        : new[] { new[]{0,1,2,15,16,17}, new[]{3,4,5,6,7,8}, new[]{9,10,11,12,13,14}, new[]{18,19,20} };
      int first0 = hero ? 1 : 0, u = hero ? squads[0].unitNum : 0;
      for (int k = 0; k < groups.Length; k++) {
        int sq = first0 + k; var dst = squads[sq];
        if (sq > 0) { var src = squads[0]; dst.CopyFrom(ref src, false); }
        dst.id = sq; dst.isHero = 0; dst.unitNum = (short)groups[k].Length; dst.initUnitNum = (short)groups[k].Length;
        var first = roster[groups[k][0]];
        dst.unitParam.name = first.unitParam_dmy.name; dst.unitParam.crc = first.unitParam_dmy.crc;
        foreach (var r in groups[k]) { var ru = roster[r]; units[u].CopyFrom(ref ru, false); units[u].id = u; u++; }
      }
      lay.troopAddingParam.squadNum = first0 + groups.Length;
      if (Writes++ < 3) BepInEx.Logging.Logger.CreateLogSource("fullarmy").LogInfo($"full army written: {first0 + groups.Length} squads{(hero ? " (hero kept)" : "")}, {u} units");
    } catch (System.Exception e) { BepInEx.Logging.Logger.CreateLogSource("fullarmy").LogError(e.ToString()); }
  }
}
var t = typeof(PataCoop.CoopPlugin).Assembly.GetType("PataCoop.Coop.Armies");
var h = new Harmony("fullarmy"); h.Patch(AccessTools.Method(t, "PublishOwn"), new HarmonyMethod(typeof(FullArmy).GetMethod("Pre")) { priority = 800 });
h.Patch(AccessTools.Method(typeof(P2.Game.Game), "initialize"), new HarmonyMethod(typeof(FullArmy).GetMethod("Pre")) { priority = 800 });
Vars["fullarmy"] = h;
return $"[{Instance}] full army hook armed";
