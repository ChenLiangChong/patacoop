using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using HarmonyLib;

namespace PataCoop.Dev;

/// <summary>
/// Research hooks around mission start. Logs the launch settings the game filled in,
/// optionally rewrites them (set from scripts via MissionOverride), and records which
/// mission phases actually run.
/// </summary>
public static class MissionOverride
{
    public static bool Enabled;
    public static bool IsMulti = true;
    public static bool EnableNet;
    public static bool Host = true;
    public static int PlayerId;
    public static int PlayerNum = 2;
    /// <summary>P2.Game.PlayerType values: 0 self, 1 other (network), 2 com (AI), 3 sharing, 4 none.</summary>
    public static int[] Types = { 0, 2, 4, 4 };
    public static int[] ComponIds = { 0, 0, 0, 0 };

    public static readonly List<string> Log = new();
    public static string Take() { lock (Log) { var s = string.Join("\n", Log); Log.Clear(); return s.Length == 0 ? "(nothing logged)" : s; } }
    internal static void Note(string s) { lock (Log) Log.Add($"[f{UnityEngine.Time.frameCount}] {s}"); DevPlugin.L.LogInfo("[mission] " + s); }

    internal static string Describe(P2.Game.GameSettingData s) =>
        $"mission {s.missionId} '{s.missionFileName}' multi={s.isMultiMode} net={s.enableNet} host={s.hostPlayer} " +
        $"id={s.playerId} num={s.playerNum} types=[{string.Join(",", s.aPlayerType.Select(t => (int)t))}] " +
        $"compon=[{string.Join(",", s.aComponId)}] egg={s.eggId} end={s.gameEndType}";
}

[HarmonyPatch(typeof(P2.Game.Game), nameof(P2.Game.Game.initialize))]
internal static class GameInitHook
{
    static void Prefix()
    {
        try
        {
            var s = P2.LaboCommon.pLaboCommonInstance_g.laboSettingDataPtr_.gameSettingData; // the one the mission uses
            MissionOverride.Note("Game.initialize, game set: " + MissionOverride.Describe(s));
            if (!MissionOverride.Enabled) return;
            s.isMultiMode = MissionOverride.IsMulti;
            s.enableNet = MissionOverride.EnableNet;
            s.hostPlayer = MissionOverride.Host;
            s.playerId = MissionOverride.PlayerId;
            s.playerNum = MissionOverride.PlayerNum;
            for (int i = 0; i < 4; i++)
            {
                s.aPlayerType[i] = (P2.Game.PlayerType)MissionOverride.Types[i];
                s.aComponId[i] = MissionOverride.ComponIds[i];
            }
            MissionOverride.Note("override applied:   " + MissionOverride.Describe(s));
        }
        catch (Exception e) { MissionOverride.Note("init hook failed: " + e); }
    }
}

/// <summary>Logs the first call of each mission phase function, so we see which path runs.</summary>
[HarmonyPatch]
internal static class PhaseTrace
{
    private static readonly HashSet<string> Seen = new();

    static IEnumerable<MethodBase> TargetMethods() =>
        typeof(P2.Game.Game).GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly)
            .Where(m => m.Name.StartsWith("gpf") && m.GetParameters().Length == 1);

    static void Prefix(MethodBase __originalMethod)
    {
        string n = __originalMethod.Name;
        bool first;
        lock (Seen) first = Seen.Add(n);
        if (first) MissionOverride.Note("phase " + n);
    }

    public static void Reset() { lock (Seen) Seen.Clear(); }
}
