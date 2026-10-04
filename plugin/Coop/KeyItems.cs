using System;
using System.Collections.Generic;
using HarmonyLib;
using P2.GameSystem.Item;
using PataCoop.Net;

namespace PataCoop.Coop;

/// <summary>
/// Story items must not depend on luck or a dropped connection. Loot in general is each player's
/// own roll, but the "memories" that unlock a class (item category 9) move the story on: every
/// player tells the others which of them they got in a co-op battle, and when the battle is over
/// everybody makes sure they have at least as many of each as anyone else got.
/// </summary>
internal static class KeyItems
{
    private const short MemoryCategory = 9;

    /// <summary>Key items we got this battle, and the most any other player got.</summary>
    private static readonly Dictionary<int, int> Mine = new(), Others = new();
    /// <summary>True from the camp after a battle until the next battle starts (the battle's results are all in).</summary>
    private static bool _backInCamp = true;

    internal static bool IsKey(int itemId)
    {
        try { return new Operator().getItemParam(itemId)?.categoryId == MemoryCategory; }
        catch { return false; }
    }

    internal static void BattleStarted()
    {
        Mine.Clear();
        Others.Clear();
        _backInCamp = false;
    }

    /// <summary>The game gave us an item during a co-op battle (or its results).</summary>
    internal static void Got(int itemId, int num)
    {
        if (_backInCamp || num <= 0 || !IsKey(itemId)) return;
        Mine[itemId] = Mine.GetValueOrDefault(itemId) + num;
        CoopNet.SendAll(new MsgWriter(Msg.KeyItem).I32(itemId).I32(Mine[itemId]).ToArray(), true);
        CoopPlugin.L.LogInfo($"[coop] key item {itemId} x{num}");
    }

    internal static void OnKeyItem(int fromSlot, MsgReader r)
    {
        int itemId = r.I32(), total = r.I32();
        if (fromSlot == CoopNet.MySlot || total <= 0) return;
        Others[itemId] = Math.Max(Others.GetValueOrDefault(itemId), total);
        if (_backInCamp) Settle(); // we are already back from that battle: make up for it now
    }

    /// <summary>The battle is over: make up for any key item somebody else got and we did not.</summary>
    internal static void Settle()
    {
        foreach (var (itemId, total) in Others)
        {
            int missing = total - Mine.GetValueOrDefault(itemId);
            if (missing <= 0) continue;
            try
            {
                Mine[itemId] = total;
                _granting = true;
                new Operator().addItem(itemId, missing);
                Session.Note($"key item {itemId} x{missing} added (another player got it)", $"補發了重要道具（隊友拿到的主線道具），共 {missing} 個");
            }
            catch (Exception e)
            {
                CoopPlugin.L.LogWarning($"could not add key item {itemId}: {e.Message}");
            }
            finally
            {
                _granting = false;
            }
        }
    }

    /// <summary>Back in the camp after a battle: its results are all in, settle what others got.</summary>
    internal static void BackInCamp()
    {
        if (_backInCamp) return;
        _backInCamp = true;
        Settle();
    }

    private static bool _granting;
    internal static bool Granting => _granting;
}

[HarmonyPatch(typeof(Operator), nameof(Operator.addItem))]
internal static class KeyItemGotPatch
{
    private static void Postfix(int itemId, int num)
    {
        if (!KeyItems.Granting) KeyItems.Got(itemId, num);
    }
}
