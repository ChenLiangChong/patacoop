using System;
using System.Collections.Generic;
using HarmonyLib;
using UnityEngine;

namespace PataCoop.Dev;

/// <summary>
/// Injects scripted button presses into the game's own pad queries, per process,
/// so each game copy can be driven without window focus. Bits use the game's
/// PSP-style layout (0x2000 = circle/Pon, 0x8000 = square/Pata, ...).
/// </summary>
public static class VirtualPad
{
    public const uint Select = 0x1, Start = 0x8, Up = 0x10, Right = 0x20, Down = 0x40, Left = 0x80,
        L = 0x100, R = 0x200, Triangle = 0x1000, Circle = 0x2000, Cross = 0x4000, Square = 0x8000;
    // drum aliases (in-game meaning of the same bits)
    public const uint Pata = Square, Pon = Circle, Chaka = Triangle, Don = Cross;
    /// <summary>Not a PSP bit: the remaster's dedicated "sortie" action (Menu_GotoBattle).</summary>
    public const uint GotoBattle = 0x10000;

    private sealed class Hold
    {
        public readonly uint Mask; public readonly int Start; public readonly int End;
        /// <summary>Frame in which the game first read this press as "just pressed"; -1 = not yet.</summary>
        public int ConsumedAt = -1;
        public Hold(uint mask, int start, int end) { Mask = mask; Start = start; End = end; }
    }

    private static readonly List<Hold> Holds = new();
    private static int Now => Time.frameCount;

    /// <summary>Press <paramref name="mask"/> starting next frame (or after <paramref name="delayFrames"/>) and hold it.</summary>
    public static void Press(uint mask, int holdFrames = 6, int delayFrames = 1)
    {
        lock (Holds)
        {
            Holds.RemoveAll(h => h.End < Now - 2);
            int start = Now + Math.Max(1, delayFrames);
            Holds.Add(new Hold(mask, start, start + Math.Max(1, holdFrames)));
        }
    }

    /// <summary>Queue several presses, one after another, <paramref name="gapFrames"/> apart.</summary>
    public static void Sequence(uint[] masks, int holdFrames = 6, int gapFrames = 12)
    {
        for (int i = 0; i < masks.Length; i++) Press(masks[i], holdFrames, 1 + i * (holdFrames + gapFrames));
    }

    public static void Clear() { lock (Holds) Holds.Clear(); }

    /// <summary>
    /// "Just pressed" bits for <paramref name="query"/>. A press stays fresh until the game
    /// first reads it (game logic may tick every other frame), then reports true for the
    /// rest of that frame only, so each press is seen exactly once per logic tick.
    /// </summary>
    internal static uint Direct(uint query = 0xFFFFFFFF)
    {
        uint m = 0;
        lock (Holds)
            foreach (var h in Holds)
            {
                if (Now < h.Start || Now >= h.End || (h.Mask & query) == 0) continue;
                if (h.ConsumedAt == -1) h.ConsumedAt = Now;
                if (h.ConsumedAt == Now) m |= h.Mask;
            }
        return m;
    }

    internal static uint Stand() => Collect(h => Now >= h.Start && Now < h.End);
    internal static uint Release() => Collect(h => h.End == Now);

    private static uint Collect(Func<Hold, bool> when)
    {
        uint m = 0;
        lock (Holds) foreach (var h in Holds) if (when(h)) m |= h.Mask;
        return m;
    }

    internal static uint ForCheck(int checkType) => checkType switch
    {
        0 => Direct(),   // CheckType_Direct
        1 => Stand(),    // CheckType_Stand
        2 => Release(),  // CheckType_Release
        3 => Direct(),   // CheckType_Repeat: a fresh press also counts as a repeat tick
        _ => 0
    };
}

[HarmonyPatch]
internal static class PadPatches
{
    // in-game queries
    [HarmonyPostfix, HarmonyPatch(typeof(P2.System.Pad.Pad), nameof(P2.System.Pad.Pad.direct))]
    private static void Direct(uint key, ref bool __result) { InputStats.Hit($"Pad.Direct 0x{key:X}"); if ((VirtualPad.Direct(key) & key) != 0) __result = true; }

    [HarmonyPostfix, HarmonyPatch(typeof(P2.System.Pad.Pad), nameof(P2.System.Pad.Pad.stand))]
    private static void Stand(uint key, ref bool __result) { InputStats.Hit($"Pad.Stand 0x{key:X}"); if ((VirtualPad.Stand() & key) != 0) __result = true; }

    [HarmonyPostfix, HarmonyPatch(typeof(P2.System.Pad.Pad), nameof(P2.System.Pad.Pad.repeat))]
    private static void Repeat(uint key, ref bool __result) { InputStats.Hit($"Pad.Repeat 0x{key:X}"); if ((VirtualPad.Direct(key) & key) != 0) __result = true; }

    [HarmonyPostfix, HarmonyPatch(typeof(P2.System.Pad.Pad), nameof(P2.System.Pad.Pad.release))]
    private static void Release(uint key, ref bool __result) { InputStats.Hit($"Pad.Release 0x{key:X}"); if ((VirtualPad.Release() & key) != 0) __result = true; }

    // menu queries
    [HarmonyPostfix, HarmonyPatch(typeof(P2.System.Pad.Pad), nameof(P2.System.Pad.Pad.menuDirect))]
    private static void MenuDirect(uint key, ref bool __result) { InputStats.Hit($"Pad.MenuDirect 0x{key:X}"); if ((VirtualPad.Direct(key) & key) != 0) __result = true; }

    [HarmonyPostfix, HarmonyPatch(typeof(P2.System.Pad.Pad), nameof(P2.System.Pad.Pad.menuStand))]
    private static void MenuStand(uint key, ref bool __result) { InputStats.Hit($"Pad.MenuStand 0x{key:X}"); if ((VirtualPad.Stand() & key) != 0) __result = true; }

    [HarmonyPostfix, HarmonyPatch(typeof(P2.System.Pad.Pad), nameof(P2.System.Pad.Pad.menuRepeat))]
    private static void MenuRepeat(uint key, ref bool __result) { InputStats.Hit($"Pad.MenuRepeat 0x{key:X}"); if ((VirtualPad.Direct(key) & key) != 0) __result = true; }

    [HarmonyPostfix, HarmonyPatch(typeof(P2.System.Pad.Pad), nameof(P2.System.Pad.Pad.menuRelease))]
    private static void MenuRelease(uint key, ref bool __result) { InputStats.Hit($"Pad.MenuRelease 0x{key:X}"); if ((VirtualPad.Release() & key) != 0) __result = true; }

    // whole-flag queries
    [HarmonyPostfix, HarmonyPatch(typeof(P2.System.Pad.Pad), nameof(P2.System.Pad.Pad.getFlag))]
    private static void GetFlag(P2.External.CheckType checkType, ref uint __result) { InputStats.Hit("Pad.getFlag"); __result |= VirtualPad.ForCheck((int)checkType); }

    [HarmonyPostfix, HarmonyPatch(typeof(P2.System.Pad.Pad), nameof(P2.System.Pad.Pad.getMenuFlag))]
    private static void GetMenuFlag(P2.External.CheckType checkType, ref uint __result) { InputStats.Hit("Pad.getMenuFlag"); __result |= VirtualPad.ForCheck((int)checkType); }

    [HarmonyPostfix, HarmonyPatch(typeof(P2.System.Pad.Pad), nameof(P2.System.Pad.Pad.getFlagDirect))]
    private static void AnyDirect(ref bool __result) { InputStats.Hit("Pad.AnyDirect"); if (VirtualPad.Direct() != 0) __result = true; }

    [HarmonyPostfix, HarmonyPatch(typeof(P2.System.Pad.Pad), nameof(P2.System.Pad.Pad.getMenuFlagDirect))]
    private static void AnyMenuDirect(ref bool __result) { InputStats.Hit("Pad.AnyMenuDirect"); if (VirtualPad.Direct() != 0) __result = true; }
}
