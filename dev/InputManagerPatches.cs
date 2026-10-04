using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using HarmonyLib;
using MenuAction = MultiPlatformInputManager.EDigitalActions_MenuControls;
using GameAction = MultiPlatformInputManager.EDigitalActions_InGameControls;

namespace PataCoop.Dev;

/// <summary>
/// Second injection layer: the action-based queries on MultiPlatformInputManager.
/// Some screens ask it directly instead of going through P2.System.Pad.Pad.
/// Also counts calls per query so we can see which layer a screen uses.
/// </summary>
public static class InputStats
{
    private static readonly Dictionary<string, int> Calls = new();
    internal static void Hit(string name) { lock (Calls) Calls[name] = Calls.TryGetValue(name, out var n) ? n + 1 : 1; }
    public static string Take()
    {
        lock (Calls)
        {
            var s = string.Join("\n", Calls.OrderByDescending(kv => kv.Value).Select(kv => $"{kv.Value,7} {kv.Key}"));
            Calls.Clear();
            return s.Length == 0 ? "(no input queries)" : s;
        }
    }

    internal static uint Bit(MenuAction a) => a switch
    {
        MenuAction.Menu_Select => 0x1, MenuAction.Menu_Start => 0x8,
        MenuAction.Menu_SelectUp => 0x10, MenuAction.Menu_SelectRight => 0x20,
        MenuAction.Menu_SelectDown => 0x40, MenuAction.Menu_SelectLeft => 0x80,
        MenuAction.Menu_LeftShoulder => 0x100, MenuAction.Menu_RightShoulder => 0x200,
        MenuAction.Menu_North => 0x1000, MenuAction.Menu_East => 0x2000,
        MenuAction.Menu_South => 0x4000, MenuAction.Menu_West => 0x8000,
        MenuAction.Menu_GotoBattle => VirtualPad.GotoBattle,
        MenuAction.Menu_Any => 0xFFFF, _ => 0
    };

    internal static uint Bit(GameAction a) => a switch
    {
        GameAction.InGame_Help => 0x1, GameAction.InGame_Retire => 0x8,
        GameAction.InGame_CursorUp => 0x10, GameAction.InGame_CameraRight => 0x20,
        GameAction.InGame_CursorDown => 0x40, GameAction.InGame_CameraLeft => 0x80,
        GameAction.InGame_Chaka => 0x1000, GameAction.InGame_Pon => 0x2000,
        GameAction.InGame_Don => 0x4000, GameAction.InGame_Pata => 0x8000,
        GameAction.InGame_Any => 0xFFFF, _ => 0
    };

    internal static IEnumerable<MethodBase> Overloads(string name, Type firstParam) =>
        typeof(MultiPlatformInputManager).GetMethods(BindingFlags.Public | BindingFlags.Instance)
            .Where(m => m.Name == name && m.GetParameters().Length > 0 && m.GetParameters()[0].ParameterType == firstParam);
}

[HarmonyPatch] internal static class MenuDirectPatch
{
    static IEnumerable<MethodBase> TargetMethods() => InputStats.Overloads("IsActionThisFrame", typeof(MenuAction));
    static void Postfix(MenuAction action, ref bool __result)
    {
        InputStats.Hit("MPIM.IsActionThisFrame(menu)");
        if ((VirtualPad.Direct(InputStats.Bit(action)) & InputStats.Bit(action)) != 0) __result = true;
    }
}

[HarmonyPatch] internal static class MenuStandPatch
{
    static IEnumerable<MethodBase> TargetMethods() => InputStats.Overloads("IsAction", typeof(MenuAction));
    static void Postfix(MenuAction action, ref bool __result)
    {
        InputStats.Hit("MPIM.IsAction(menu)");
        if ((VirtualPad.Stand() & InputStats.Bit(action)) != 0) __result = true;
    }
}

[HarmonyPatch] internal static class MenuReleasePatch
{
    static IEnumerable<MethodBase> TargetMethods() => InputStats.Overloads("IsRelease", typeof(MenuAction));
    static void Postfix(MenuAction action, ref bool __result)
    {
        InputStats.Hit("MPIM.IsRelease(menu)");
        if ((VirtualPad.Release() & InputStats.Bit(action)) != 0) __result = true;
    }
}

[HarmonyPatch] internal static class MenuRepeatPatch
{
    static IEnumerable<MethodBase> TargetMethods() => InputStats.Overloads("IsRepeat", typeof(MenuAction));
    static void Postfix(MenuAction action, ref bool __result)
    {
        InputStats.Hit("MPIM.IsRepeat(menu)");
        if ((VirtualPad.Direct(InputStats.Bit(action)) & InputStats.Bit(action)) != 0) __result = true;
    }
}

[HarmonyPatch] internal static class GameDirectPatch
{
    static IEnumerable<MethodBase> TargetMethods() => InputStats.Overloads("IsActionThisFrame", typeof(GameAction));
    static void Postfix(GameAction action, ref bool __result)
    {
        InputStats.Hit("MPIM.IsActionThisFrame(game)");
        if ((VirtualPad.Direct(InputStats.Bit(action)) & InputStats.Bit(action)) != 0) __result = true;
    }
}

[HarmonyPatch] internal static class GameStandPatch
{
    static IEnumerable<MethodBase> TargetMethods() => InputStats.Overloads("IsAction", typeof(GameAction));
    static void Postfix(GameAction action, ref bool __result)
    {
        InputStats.Hit("MPIM.IsAction(game)");
        if ((VirtualPad.Stand() & InputStats.Bit(action)) != 0) __result = true;
    }
}

[HarmonyPatch] internal static class GameReleasePatch
{
    static IEnumerable<MethodBase> TargetMethods() => InputStats.Overloads("IsRelease", typeof(GameAction));
    static void Postfix(GameAction action, ref bool __result)
    {
        InputStats.Hit("MPIM.IsRelease(game)");
        if ((VirtualPad.Release() & InputStats.Bit(action)) != 0) __result = true;
    }
}

[HarmonyPatch] internal static class GameRepeatPatch
{
    static IEnumerable<MethodBase> TargetMethods() => InputStats.Overloads("IsRepeat", typeof(GameAction));
    static void Postfix(GameAction action, ref bool __result)
    {
        InputStats.Hit("MPIM.IsRepeat(game)");
        if ((VirtualPad.Direct(InputStats.Bit(action)) & InputStats.Bit(action)) != 0) __result = true;
    }
}

[HarmonyPatch(typeof(MultiPlatformInputManager), nameof(MultiPlatformInputManager.IsActionThisFrameMenuAny))]
internal static class MenuAnyPatch
{
    static void Postfix(ref bool __result)
    {
        InputStats.Hit("MPIM.IsActionThisFrameMenuAny");
        if (VirtualPad.Direct() != 0) __result = true;
    }
}

[HarmonyPatch(typeof(MultiPlatformInputManager), nameof(MultiPlatformInputManager.IsActionThisFrameBattleStart))]
internal static class BattleStartPatch
{
    static void Postfix(ref bool __result)
    {
        InputStats.Hit("MPIM.IsActionThisFrameBattleStart");
        if ((VirtualPad.Direct(VirtualPad.GotoBattle) & VirtualPad.GotoBattle) != 0) __result = true;
    }
}
