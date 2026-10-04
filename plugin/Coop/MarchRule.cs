using System;
using System.Collections.Generic;
using HarmonyLib;
using P2.Game.Unit;
using PataCoop.Net;

namespace PataCoop.Coop;

/// <summary>
/// The army advances together. The multiplayer march moves the troop only in the frames when every
/// player's army is acting on the march, so two players whose measures run a beat or two apart
/// (and they always do a little) would stop and go at half speed. Here the first active player
/// sets the pace, as in a single-player battle, and the others march along as long as they keep
/// drumming and the latest command they drummed was the march too; any other command, or a miss,
/// stops the army. Players who are not drumming (nothing for a while), whose army has
/// fallen, or who left do not hold the others back.
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

    internal static int Now() => (int)(P2.Game.Game.pGame_g?.soundDirector_?.getBeatTimer()?.halfBeatCount_ ?? 0);

    internal static void Drummed(int player, InstructionParam.Command command)
    {
        if (player < 0 || player >= LastDrum.Length) return;
        LastDrum[player] = Now();
        LastCommand[player] = command;
    }

    /// <summary>
    /// The latest command this player drummed was the march, and they are still drumming. (Their
    /// next command reaches the other machines a little late: a window of exactly one cycle would
    /// end mid-march every time, and stop the army halfway through each march.)
    /// </summary>
    internal static bool Marching(UnitTroop troop, int player, int now) => Recent(player, now) && IsMarch(troop, LastCommand[player]);

    /// <summary>Drummed within the idle window (a beat counter that went backwards counts as idle).</summary>
    private static bool Recent(int player, int now)
    {
        if (LastDrum[player] == int.MinValue) return false;
        int since = now - LastDrum[player];
        return since >= 0 && since <= IdleHalfBeats;
    }

    /// <summary>The multiplayer march's own test: a command of type 1, id 1 (march, in any fever or class variant).</summary>
    internal static bool IsMarch(UnitTroop troop, InstructionParam.Command command)
    {
        if (command == InstructionParam.Command.Command_None) return false;
        var param = troop.aInstructionParam_?[0]?.getInstCmdParam(command);
        return param != null && param.commandType == 1 && param.commandId == 1;
    }

    /// <summary>The command this player drummed last.</summary>
    internal static InstructionParam.Command LastOf(int player) => LastCommand[player];

    /// <summary>Where a player stands for the march: not counted (idle), marching, or holding the army up.</summary>
    internal enum Stance { Idle, March, Other }

    internal static Stance StanceOf(UnitTroop troop, int player, int now) =>
        !Active(troop, player, now) ? Stance.Idle : Marching(troop, player, now) ? Stance.March : Stance.Other;

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

    /// <summary>
    /// Players holding the army up right now: drumming something other than the march while
    /// someone else marches (with nobody marching, nobody is in the way).
    /// </summary>
    internal static List<int> Blockers(UnitTroop troop, int now)
    {
        var blockers = new List<int>();
        bool someoneMarches = false;
        var stances = new Stance[Session.MaxPlayers];
        for (int p = 0; p < Session.MaxPlayers; p++)
        {
            stances[p] = StanceOf(troop, p, now);
            someoneMarches |= stances[p] == Stance.March;
        }
        if (someoneMarches)
            for (int p = 0; p < Session.MaxPlayers; p++)
                if (stances[p] == Stance.Other) blockers.Add(p);
        return blockers;
    }

    /// <summary>Does this player's drumming count for the march right now?</summary>
    internal static bool Active(UnitTroop troop, int player, int now) =>
        Session.Occupied(player) && Recent(player, now) && HasArmy(troop, player);

    private static int _armyFrame = -1;
    private static readonly int[] ArmyKnown = new int[Session.MaxPlayers]; // 0 unknown, 1 yes, 2 no (this frame)

    /// <summary>Does this player still have a unit standing? (Looked up once per frame: the march, the labels and the panel all ask.)</summary>
    private static bool HasArmy(UnitTroop troop, int player)
    {
        int frame = UnityEngine.Time.frameCount;
        if (frame != _armyFrame)
        {
            _armyFrame = frame;
            Array.Clear(ArmyKnown);
        }
        if (ArmyKnown[player] == 0) ArmyKnown[player] = LooksUpArmy(troop, player) ? 1 : 2;
        return ArmyKnown[player] == 1;
    }

    private static bool LooksUpArmy(UnitTroop troop, int player)
    {
        var squads = troop.unitSquadPtrList_;
        if (squads == null) return false;
        foreach (var squad in squads)
        {
            if (squad?.squadInfo_?.squadAddingParam?.rsv1 != (Armies.OwnerTag | player) || squad.unitBasePtrList_ == null) continue;
            foreach (var u in squad.unitBasePtrList_)
                if (u != null && !u.isEnd()) return true;
        }
        return false;
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
/// Applies <see cref="MarchRule"/> around the troop's march, and keeps the story rules for enemy
/// troops (several enemy squads would otherwise never advance, and an enemy troop without a flag
/// bearer would throw). The multiplayer march also moves only while someone carries the egg;
/// story missions have no egg (see <see cref="StoryArmy"/>), so for the march the flag bearer
/// counts as carried.
/// </summary>
[HarmonyPatch(typeof(TroopCtrl), nameof(TroopCtrl.moveSquadLine))]
internal static class MarchRulePatch
{
    private sealed class State
    {
        public bool StoryRules, WasMultiMode, Carried, WasCarried;
        public InstructionParam.Command[]? Saved;
        public int SavedCount;
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
            if (!_warned) CoopPlugin.L.LogWarning("march rule failed, this step runs on the game's own rule: " + e);
            _warned = true;
        }
    }

    private static void Apply(TroopCtrl __instance, ref State? __state)
    {
        var troop = __instance.pUnitTroop_;
        var s = Battle.Settings;
        if (troop == null || s == null || troop.troopInfo_ == null) return;
        if ((int)troop.troopInfo_.troopType != 0)
        {
            __state = new State { StoryRules = true, WasMultiMode = s.isMultiMode };
            s.isMultiMode = false;
            return;
        }
        var st = __state = new State();
        var flag = __instance.flagUnit_;
        if (flag != null && !flag.isEgg_)
        {
            st.WasCarried = flag.isGripped_;
            st.Carried = true;
            flag.isGripped_ = true;
        }

        // The single-player march moves the troop by the first player's command at the full pace; the
        // multiplayer march walks at well under half that pace and stops early in each measure. So our
        // troop marches by the single-player rules, and "everyone marches together" is decided here:
        // the first player's command becomes the march when every drumming player marches, and the
        // command of whoever holds the army up otherwise.
        var inst = troop.aInstructionParam_;
        if (inst == null || inst.Length == 0 || inst[0] == null) return;
        st.StoryRules = true;
        st.WasMultiMode = s.isMultiMode;
        s.isMultiMode = false;
        int now = MarchRule.Now(), blocker = -1;
        InstructionParam.Command? march = null;
        for (int p = 0; p < inst.Length && p < Session.MaxPlayers; p++)
        {
            if (inst[p] == null || !MarchRule.Active(troop, p, now)) continue;
            if (MarchRule.Marching(troop, p, now)) march ??= MarchRule.LastOf(p);
            else if (blocker < 0) blocker = p;
        }
        st.Saved = new InstructionParam.Command[] { inst[0].lastActionCmd_ };
        st.SavedCount = 1;
        if (blocker >= 0)
        {
            var held = inst[blocker].lastActionCmd_;
            inst[0].lastActionCmd_ = MarchRule.IsMarch(troop, held) ? InstructionParam.Command.Command_Stay : held;
        }
        else if (march is { } m) inst[0].lastActionCmd_ = m;
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
        if (state.StoryRules && Battle.Settings is { } s) s.isMultiMode = state.WasMultiMode;
        if (state.Carried && __instance.flagUnit_ is { } flag) flag.isGripped_ = state.WasCarried;
        var inst = __instance.pUnitTroop_?.aInstructionParam_;
        if (state.Saved != null && inst != null)
            for (int p = 0; p < inst.Length && p < state.SavedCount; p++)
                if (inst[p] != null) inst[p].lastActionCmd_ = state.Saved[p];
    }
}
