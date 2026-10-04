using System;
using HarmonyLib;
using PataCoop.Net;
using UnityEngine;

namespace PataCoop.Coop;

/// <summary>
/// How far into the battle we are, in beat-timer ticks (80 a battle step): the same at the same
/// battle step on every machine. The beat timer alone cannot tell. A miracle's rhythm game starts
/// it again from 0 for the miracle's music, and the return to the battle music starts it from 0
/// once more (and the step after that does not even count). That happens only on the machine of
/// the player who drums the miracle. So this clock takes the beat timer's reading at the battle's
/// first step and from then on counts battle steps (catch-up steps included).
/// </summary>
internal static class BattleClock
{
    internal const int TicksPerStep = 80;
    /// <summary>Ticks per half beat (the battle's tempo, 120 beats a minute).</summary>
    internal const int TicksPerHalfBeat = 1200;

    private static bool _started;
    private static uint _start;
    private static long _steps;

    internal static void Reset()
    {
        _started = false;
        _steps = 0;
    }

    /// <summary>Before every battle step.</summary>
    internal static void BeforeStep(P2.Game.Game game)
    {
        if (!Battle.Active || game.gamePhase_ != P2.Game.Game.GamePhase.GamePhase_Play) return;
        if (_started)
        {
            _steps++;
            return;
        }
        _started = true;
        _start = game.soundDirector_?.getBeatTimer()?.tick_ ?? 0;
    }

    /// <summary>True from the battle's first step (in Play) on.</summary>
    internal static bool Started => _started;

    /// <summary>Ticks into the battle (before its first step: the beat timer's own reading).</summary>
    internal static long Ticks => _started
        ? _start + _steps * TicksPerStep
        : P2.Game.Game.pGame_g?.soundDirector_?.getBeatTimer()?.tick_ ?? 0;
}

[HarmonyPatch(typeof(P2.Game.Game), nameof(P2.Game.Game.update), new[] { typeof(uint) })]
internal static class BattleClockStepPatch
{
    private static void Prefix(P2.Game.Game __instance) => BattleClock.BeforeStep(__instance);
}

/// <summary>
/// One battle clock for everybody.
///
/// The game moves its whole battle (music sequencer, beat, units) one fixed step per frame, so a
/// machine that stutters for a moment falls behind for good: its drums, marches and attacks land
/// later than everybody else's. Every machine sends its beat clock a few times a second, and one
/// that has fallen behind the furthest-ahead player runs a second battle step in some frames until
/// it has caught up (music, beat and units together, a short fast-forward). Nobody ever waits:
/// skipping a battle step makes the game abandon the mission. Differences under three steps
/// (50 ms) are left alone.
///
/// The clocks compared are <see cref="BattleClock"/>s, which go on counting through a miracle.
///
/// Measuring: a clock reading arrives some time after it was taken (network, the relay, the
/// frame it waits for). Each message also carries what the sender measured of everybody else,
/// so the two one-way measurements of a pair cancel that delay, as NTP does:
/// offset = (what I see of them - what they see of me) / 2.
/// </summary>
internal static class Clock
{
    /// <summary>Beat-timer ticks per battle step (the game's fixed time step, 1/60 s).</summary>
    internal const int TicksPerStep = 80;
    private const double TicksPerMs = TicksPerStep * 60 / 1000.0;
    private const int StartBeyond = 3 * TicksPerStep, StopWithin = TicksPerStep;
    private const int FreshFrames = 60;

    /// <summary>Raw view of each player's clock minus ours (ticks, delay not removed), and when measured.</summary>
    private static readonly double[] Raw = new double[Session.MaxPlayers];
    private static readonly int[] RawFrame = new int[Session.MaxPlayers];
    /// <summary>How far each player's clock is ahead of ours (ticks), delay removed.</summary>
    private static readonly double[] Ahead = new double[Session.MaxPlayers];
    private static readonly int[] MeasuredFrame = new int[Session.MaxPlayers];
    private static bool _correcting, _extraStep;
    private static int _nextSend;

    /// <summary>Battle steps run twice this battle (shown in the panel).</summary>
    internal static int Caught;

    internal static string Describe()
    {
        double lead = Lead();
        bool behind = lead > 2 * TicksPerStep;
        return Text.T($"clock {(behind ? $"-{lead / TicksPerMs:F0} ms" : "in step")} · caught up {Caught} steps",
            $"節拍 {(behind ? $"落後 {lead / TicksPerMs:F0} ms" : "同步")} · 已追上 {Caught} 步");
    }

    internal static void Reset()
    {
        Array.Clear(Raw);
        Array.Clear(Ahead);
        Array.Fill(RawFrame, -100000);
        Array.Fill(MeasuredFrame, -100000);
        _correcting = _extraStep = false;
        Caught = 0;
    }

    private static P2.System.Sound.BeatTimer? Timer(P2.Game.Game? game) => game?.soundDirector_?.getBeatTimer();

    private static bool Playing(P2.Game.Game? game) =>
        Battle.Active && game != null && game.gamePhase_ == P2.Game.Game.GamePhase.GamePhase_Play && !game.isGameEndOrder_;

    private static bool Fresh(int frame) => Time.frameCount - frame < FreshFrames;

    /// <summary>The furthest any other player is ahead of us (ticks, 0 if nobody is).</summary>
    private static double Lead()
    {
        double lead = 0;
        for (int p = 0; p < Ahead.Length; p++)
            if (p != CoopNet.MySlot && Session.Occupied(p) && Fresh(MeasuredFrame[p])) lead = Math.Max(lead, Ahead[p]);
        return lead;
    }

    internal static void Tick()
    {
        if (Time.frameCount < _nextSend) return;
        var game = P2.Game.Game.pGame_g;
        var timer = Timer(game);
        if (!Playing(game) || timer == null) return;
        _nextSend = Time.frameCount + 10;
        var w = new MsgWriter(Msg.Clock).U32((uint)BattleClock.Ticks);
        for (int p = 0; p < Session.MaxPlayers; p++) w.F32(Fresh(RawFrame[p]) ? (float)Raw[p] : float.NaN);
        CoopNet.SendAll(w.ToArray(), false);
    }

    internal static void OnClock(int fromSlot, MsgReader r)
    {
        uint theirTick = r.U32();
        var seen = new float[Session.MaxPlayers];
        for (int p = 0; p < seen.Length; p++) seen[p] = r.F32();
        var game = P2.Game.Game.pGame_g;
        var timer = Timer(game);
        if (fromSlot < 0 || fromSlot >= Ahead.Length || !Playing(game) || timer == null) return;
        double raw = (double)theirTick - (uint)BattleClock.Ticks;   // their clock minus ours, late by the delay
        Raw[fromSlot] = raw;
        RawFrame[fromSlot] = Time.frameCount;
        int me = CoopNet.MySlot;
        float theirViewOfMe = me >= 0 && me < seen.Length ? seen[me] : float.NaN; // our clock minus theirs, as they saw it
        // the delay counts against both readings, so it cancels in the difference
        Ahead[fromSlot] = float.IsNaN(theirViewOfMe) ? raw : (raw - theirViewOfMe) / 2;
        MeasuredFrame[fromSlot] = Time.frameCount;
    }

    /// <summary>After a battle step: if we are behind, run one more.</summary>
    internal static void AfterStep(P2.Game.Game game)
    {
        if (_extraStep || !Playing(game)) return;
        double lead = Lead();
        if (!_correcting && lead > StartBeyond) _correcting = true;
        else if (_correcting && lead < StopWithin) _correcting = false;
        if (!_correcting) return;
        _extraStep = true;
        try
        {
            game.update(TicksPerStep);
            for (int p = 0; p < Ahead.Length; p++) { Ahead[p] -= TicksPerStep; Raw[p] -= TicksPerStep; }
            Caught++;
        }
        finally
        {
            _extraStep = false;
        }
    }
}

[HarmonyPatch(typeof(P2.Game.Game), nameof(P2.Game.Game.update), new[] { typeof(uint) })]
internal static class BattleClockPatch
{
    private static void Postfix(P2.Game.Game __instance) => Clock.AfterStep(__instance);
}
