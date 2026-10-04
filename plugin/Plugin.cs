using System;
using BepInEx;
using BepInEx.Configuration;
using BepInEx.Logging;
using BepInEx.Unity.IL2CPP;
using HarmonyLib;
using Il2CppInterop.Runtime.Injection;
using UnityEngine;

namespace PataCoop;

/// <summary>
/// PataCoop: play Patapon 2 story missions together (up to 4 players). One player hosts
/// (the relay server runs inside their game), the others join over Radmin VPN or LAN.
/// </summary>
[BepInPlugin(Guid, "PataCoop", Version)]
public sealed class CoopPlugin : BasePlugin
{
    public const string Guid = "com.patacoop.coop";
    public const string Version = "0.4.0";

    internal static ManualLogSource L = null!;
    public static ConfigEntry<string> PlayerName = null!;
    public static ConfigEntry<string> HostAddress = null!;
    public static ConfigEntry<int> Port = null!;
    public static ConfigEntry<float> HpPerExtraPlayer = null!;
    public static ConfigEntry<float> DamagePerExtraPlayer = null!;
    public static ConfigEntry<string> Language = null!;

    public override void Load()
    {
        L = Log;
        PlayerName = Config.Bind("Coop", "PlayerName", Environment.UserName, "Name shown to the other players.");
        HostAddress = Config.Bind("Coop", "HostAddress", "",
            "Radmin VPN (or LAN) IP address of the host, for example 26.12.34.56. Leave empty to search the network.");
        Port = Config.Bind("Coop", "Port", 27015, "UDP port the host listens on.");
        Language = Config.Bind("Coop", "Language", "zh", "Panel and message language: zh (Traditional Chinese) or en (English).");
        HpPerExtraPlayer = Config.Bind("Difficulty", "EnemyHpPerExtraPlayer", 0.75f,
            "Enemy hit points grow by this much per player beyond the first (2 players = x1.75, 4 = x3.25). The host's value is used.");
        DamagePerExtraPlayer = Config.Bind("Difficulty", "EnemyDamagePerExtraPlayer", 0.15f,
            "Damage enemies deal grows by this much per extra player (4 players = x1.45). The host's value is used.");

        ClassInjector.RegisterTypeInIl2Cpp<CoopDriver>();
        AddComponent<CoopDriver>();
        new Harmony(Guid).PatchAll(typeof(CoopPlugin).Assembly);
        L.LogMessage($"PataCoop {Version} loaded. Panel in the top-right corner (F10 folds it); F7 = host, F8 = join, F9 = leave.");
    }
}

/// <summary>Per-frame driver: network polling, session logic and the status panel.</summary>
public sealed class CoopDriver : MonoBehaviour
{
    public CoopDriver(IntPtr ptr) : base(ptr) { }

    private void Update()
    {
        // the network first and on its own: a failing panel must never stop a battle's traffic
        try
        {
            Net.CoopNet.Poll();
            Coop.Session.Tick();
        }
        catch (Exception e)
        {
            CoopPlugin.L.LogError("tick failed: " + e);
        }
        try
        {
            Ui.Tick();
            PanelCursor.Tick(Coop.Lobby.InCamp);
            Hotkeys.Tick();
            Net.Networks.Tick();
            Net.RoomFinder.Tick(Overlay.WantsRooms);
        }
        catch (Exception e)
        {
            Overlay.Fail(e);
        }
    }

    private void OnGUI()
    {
        try { Overlay.Draw(); }
        catch (Exception e) { Overlay.Fail(e); }
    }
}
