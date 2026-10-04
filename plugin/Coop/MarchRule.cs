using System;
using HarmonyLib;
using P2.Game.Unit;
using PataCoop.Net;

namespace PataCoop.Coop;

/// <summary>
/// The latest command each player drummed (for the name labels). The march itself needs no rule
/// between players any more: every player's army marches on its own drums (see
/// <see cref="ArmyPositions"/> and <see cref="OwnMarchPatch"/>).
/// </summary>
internal static class MarchRule
{
    /// <summary>A player who drummed nothing for this many half beats (10 beats, a little over one command cycle) is idle.</summary>
    internal const int IdleHalfBeats = 20;

    /// <summary>Half-beat count of each player's last drum command (int.MinValue: none this battle), and the command.</summary>
    private static readonly int[] LastDrum = new int[Session.MaxPlayers];
    private static readonly InstructionParam.Command[] LastCommand = new InstructionParam.Command[Session.MaxPlayers];

    internal static void Reset()
    {
        Array.Fill(LastDrum, int.MinValue);
        Array.Fill(LastCommand, InstructionParam.Command.Command_None);
    }

    /// <summary>Half beats into the battle (a miracle's restart of the beat timer does not count).</summary>
    internal static int Now() => (int)(BattleClock.Ticks / BattleClock.TicksPerHalfBeat);

    internal static void Drummed(int player, InstructionParam.Command command)
    {
        if (player < 0 || player >= LastDrum.Length) return;
        LastDrum[player] = Now();
        LastCommand[player] = command;
    }

    /// <summary>Nothing drummed for a while (a beat counter that went backwards counts as idle).</summary>
    internal static bool Idle(int player, int now)
    {
        if (player < 0 || player >= LastDrum.Length || LastDrum[player] == int.MinValue) return true;
        int since = now - LastDrum[player];
        return since < 0 || since > IdleHalfBeats;
    }

    /// <summary>The latest command a player drummed, in words ("" before their first).</summary>
    internal static string Word(UnitTroop troop, int player)
    {
        if (player < 0 || player >= LastCommand.Length || LastCommand[player] == InstructionParam.Command.Command_None) return "";
        var p = troop.aInstructionParam_?[0]?.getInstCmdParam(LastCommand[player]);
        if (p == null) return "";
        if (p.commandType == 3) return Text.T("miracle", "奇蹟");
        if (p.commandType != 1) return Text.T("other", "其他");
        // the troop's action table: 0 stay, 1 work, 2 attack, 3 miss, 4 back, 5 charge, 6 escape, 7 jump, 8 act up
        return p.commandId switch
        {
            0 => Text.T("defend", "防禦"),
            1 => Text.T("march", "前進"),
            2 => Text.T("attack", "攻擊"),
            3 => Text.T("missed", "失誤"),
            4 or 6 => Text.T("retreat", "撤退"),
            5 => Text.T("charge", "衝鋒"),
            7 => Text.T("jump", "跳躍"),
            8 => Text.T("party", "派對"),
            _ => Text.T("other", "其他"),
        };
    }
}

/// <summary>Note when each player drums a command (ours and the others', as the game hands them to the troop).</summary>
[HarmonyPatch(typeof(UnitTroop), nameof(UnitTroop.receiveCommand))]
internal static class DrumSeenPatch
{
    private static void Postfix(UnitTroop __instance, InstructionParam.CommandParam pCommandParam, int playerId)
    {
        if (!Battle.Active || P2.Game.Game.pGame_g?.gamePhase_ != P2.Game.Game.GamePhase.GamePhase_Play) return;
        if ((int)(__instance.troopInfo_?.troopType ?? (TroopType)(-1)) != 0) return;
        // our own drum can come without a player id: it is ours
        MarchRule.Drummed(playerId < 0 ? CoopNet.MySlot : playerId, pCommandParam?.command ?? InstructionParam.Command.Command_None);
    }
}

/// <summary>
/// Our troop's base is our own army (see <see cref="ArmyPositions"/>), so it marches by the
/// single-player rules on our own drums alone. The single-player march reads the first player's
/// instructions; on a guest ours take that place for the call. The multiplayer march would instead
/// walk at well under half the pace, only while everyone marches at once, and only while the
/// flag bearer is carried (story missions have no egg: see <see cref="StoryArmy"/>). Enemy troops
/// keep the story rules too (several enemy squads would otherwise never advance, and an enemy troop
/// without a flag bearer would throw).
/// </summary>
[HarmonyPatch(typeof(TroopCtrl), nameof(TroopCtrl.moveSquadLine))]
internal static class OwnMarchPatch
{
    private sealed class State
    {
        public bool WasMultiMode, Carried, WasCarried;
        public int Swapped = -1;
    }

    private static bool _warned;

    private static void Prefix(TroopCtrl __instance, out State? __state)
    {
        __state = null;
        if (!Battle.Active) return;
        try
        {
            Apply(__instance, ref __state);
        }
        catch (Exception e)
        {
            Restore(__instance, __state);
            __state = null;
            if (!_warned) CoopPlugin.L.LogWarning("own march failed, this step runs on the game's own rule: " + e);
            _warned = true;
        }
    }

    private static void Apply(TroopCtrl __instance, ref State? __state)
    {
        var troop = __instance.pUnitTroop_;
        var s = Battle.Settings;
        if (troop == null || s == null || troop.troopInfo_ == null) return;
        var st = __state = new State { WasMultiMode = s.isMultiMode };
        s.isMultiMode = false;
        if ((int)troop.troopInfo_.troopType != 0) return;
        var flag = __instance.flagUnit_;
        if (flag != null && !flag.isEgg_)
        {
            st.WasCarried = flag.isGripped_;
            st.Carried = true;
            flag.isGripped_ = true;
        }
        var inst = troop.aInstructionParam_;
        int me = CoopNet.MySlot;
        if (inst == null || me <= 0 || me >= inst.Length || inst[0] == null || inst[me] == null) return;
        var first = inst[0];
        inst[0] = inst[me];
        inst[me] = first;
        st.Swapped = me;
    }

    /// <summary>Runs after the march even when it (or the prefix) threw.</summary>
    private static Exception? Finalizer(TroopCtrl __instance, State? __state, Exception? __exception)
    {
        try { Restore(__instance, __state); }
        catch (Exception e) { CoopPlugin.L.LogWarning("could not restore after the march: " + e.Message); }
        return __exception;
    }

    private static void Restore(TroopCtrl __instance, State? state)
    {
        if (state == null) return;
        if (Battle.Settings is { } s) s.isMultiMode = state.WasMultiMode;
        if (state.Carried && __instance.flagUnit_ is { } flag) flag.isGripped_ = state.WasCarried;
        var inst = __instance.pUnitTroop_?.aInstructionParam_;
        if (state.Swapped > 0 && inst != null && state.Swapped < inst.Length)
        {
            var ours = inst[0];
            inst[0] = inst[state.Swapped];
            inst[state.Swapped] = ours;
        }
    }
}
