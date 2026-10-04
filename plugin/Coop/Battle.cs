using System;
using HarmonyLib;
using Il2CppInterop.Runtime.InteropTypes.Arrays;
using P2.Game;
using PataCoop.Net;
using Px.Network;

namespace PataCoop.Coop;

/// <summary>
/// Runs a story mission as a co-op battle on the game's own multiplayer engine.
///
/// The game simulates the battle on every machine and exchanges small per-frame packets
/// (drum commands, unit hit points, positions, kills, script events) between the host
/// (buffer id 0) and the clients. On PC its transport is stubbed out, so we carry those
/// packets ourselves: whatever <c>Game.updateSend</c> serialises into <c>sendPacketData</c>
/// goes through our relay, and what arrives is handed to <c>Game.OnGamePacketReceived</c>.
/// The mission-start handshake ("sync scene") is bridged the same way.
/// </summary>
public static class Battle
{
    /// <summary>The running mission is a co-op battle.</summary>
    public static bool Active { get; private set; }
    /// <summary>Between our Game.initialize prefix and postfix.</summary>
    internal static bool Preparing { get; private set; }
    public static int MissionId { get; private set; } = -1;
    public static int Sent, Received, SyncSent, SyncReceived;
    public static string State => !Active ? Text.T("idle", "閒置") : Controller?.IsNetworkSynced == true ? Text.T("synced", "已同步") : Text.T("waiting for players", "等待其他玩家");

    private static int _syncResendFrame;
    private static bool _announcedSync;
    private static bool _initializedController;

    internal static P2.GameSystem.Network.Controller? Controller => P2.LaboCommon.pLaboCommonInstance_g?.networkControllerPtr_;
    internal static GameSettingData? Settings => P2.LaboCommon.pLaboCommonInstance_g?.laboSettingDataPtr_?.gameSettingData;

    // ------------------------------------------------------------------ mission start

    /// <summary>
    /// Called just before a mission initialises. If we are in a co-op room the mission becomes a
    /// multiplayer battle: every machine gets the same player table, with itself as "self".
    /// </summary>
    internal static void Prepare(Game game)
    {
        Active = false;
        var s = Settings;
        if (s == null || !Session.CoopActive) return;

        int mission = s.missionId;
        Lobby.LearnName(mission, P2.LaboCommon.pLaboCommonInstance_g?.laboSettingDataPtr_?.missionName);
        string name = Lobby.NameOf(mission);
        if (CoopNet.IsHost)
        {
            CoopNet.SendAll(new MsgWriter(Msg.Battle).I32(mission).ToArray(), true);
            Session.Note($"co-op battle: {name} with {Session.PlayerCount} players", $"合作戰鬥：{name}（{Session.PlayerCount} 人）");
        }
        else if (Lobby.GoMission != mission)
        {
            Session.Note($"{name} was not started by the host; playing it alone", $"房主沒有出擊「{name}」，這場改為單人遊玩");
            return;
        }

        int self = CoopNet.MySlot;
        // isMultiMode is switched on after Game.initialize (see Commit): initialize would otherwise take
        // the Paragate path for the story mission (no miracle setup, multiplayer script flags).
        s.enableNet = true;
        s.hostPlayer = CoopNet.IsHost;
        s.playerId = self;
        s.playerNum = Session.MaxPlayers;
        for (int i = 0; i < Session.MaxPlayers; i++)
            s.aPlayerType[i] = i == self ? PlayerType.PlayerType_SelfCtrl
                : Session.Occupied(i) ? PlayerType.PlayerType_OtherCtrl
                : PlayerType.PlayerType_None;

        var nc = Controller;
        if (nc != null)
        {
            // Matching normally does this: per-player packet buffers, a host transceiver, cleared tables.
            if (!nc.isInitialized_)
            {
                nc.initializeAdhoc();
                _initializedController = true;
            }
            nc.isErrorCheck = false; // its keep-alive timeouts watch the PSP transport, which we bypass
            var positions = nc.MemberInfo?.MemberPosition;
            for (int i = 0; i < Session.MaxPlayers; i++)
            {
                if (positions != null && i < positions.Length) positions[i] = Session.UserId(i);
                if (nc.playerType != null && i < nc.playerType.Length) nc.playerType[i] = s.aPlayerType[i];
                if (nc.SyncNumber != null && i < nc.SyncNumber.Length) nc.SyncNumber[i] = 0;
            }
            nc.selfBufferId_ = (uint)self; // 0 = host: the game derives host/client from this
            nc.IsNetworkSynced = false;
        }

        Armies.Prepare(s);

        SaveBackup.BeforeBattle(Lobby.NameOf(mission));
        Active = true;
        Preparing = true;
        // before the mission sets itself up: its first rolls (which squads come) are keyed by this clock, and
        // the last battle's count, which differs between machines, must not reach them
        BattleClock.Reset();
        EnemyRoster.Reset(); // before the mission lists its enemy candidates
        SharedRandom.Start();
        EndAllowed = false;
        _goalSent = false;
        MissionId = mission;
        Sent = Received = SyncSent = SyncReceived = 0;
        _announcedSync = false;
        _syncResendFrame = 0;
        Session.Note($"mission {mission}: co-op as P{self + 1}{(CoopNet.IsHost ? " (host)" : "")}", $"合作關卡開始：你是 P{self + 1}{(CoopNet.IsHost ? "（房主）" : "")}");
    }

    /// <summary>After Game.initialize: from here on the engine runs the battle as a network battle.</summary>
    internal static void Commit(Game game)
    {
        Preparing = false;
        if (!Active) return;
        var s = Settings;
        if (s != null) s.isMultiMode = true;
        StoryArmy.Reset();
        MarchRule.Reset();
        ArmyPositions.Reset();
        WorldSync.BattleStarted();
        Clock.Reset();
        HitSync.Reset();
        UnitIds.Reset();
        StoryCompanions.Reset();
        KeyItems.BattleStarted();

        // What Game.initialize does on its multiplayer path (we let it take the story path instead,
        // which sets up the mission's miracles): the outgoing packet buffer and only the host may retreat.
        // Left out: the Paragate carnival, and the fixed rhythm. With a fixed rhythm the army answers
        // only on every other bar line, so a command that ends anywhere else waits one to three beats
        // before the army moves, and the player's next command then cuts that answer short; each
        // player also gets a miss every cycle they do not drum, which would hold up the march for good.
        // The beat itself stays shared without it (see Clock): only when the army answers follows the
        // player's own drumming, as in a single-player battle.
        var send = game.sendPacketData;
        if (send != null && send.Packet == null)
        {
            send.PlayerID = -1;
            send.ProtocolID = 0;
            send.Counter = 0;
            send.Packet = new Il2CppStructArray<byte>(0x100);
        }
        game.enableReturnMessageBox(CoopNet.IsHost);
        var beat = game.soundDirector_?.beatCommander_;
        if (beat != null) beat.isFixedRhythm_ = false;
        // normally grown when the game sets the stock up; this covers a stock set up before Prepare
        try { ActorPools.Grow(game.systemAccessor_?.pActorMng_?.pActorPool_?.TryCast<P2.Game.Actor.GameActorPool>()); }
        catch (Exception e) { CoopPlugin.L.LogWarning("could not grow the battle stock: " + e.Message); }
    }

    internal static void OnAnnounced(int fromSlot, MsgReader r)
    {
        if (fromSlot != 0) return;
        Session.AnnouncedMission = r.I32();
        Session.Note($"host started mission {Session.AnnouncedMission}: deploy to the same mission to join", "房主出擊了：出擊同一關即可加入");
    }

    internal static void OnEndAnnounced(int fromSlot, int outcome)
    {
        if (fromSlot != 0) return;
        Session.AnnouncedMission = -1;
        Session.Note($"host ended the battle ({outcome})", "房主的戰鬥結束了");
    }

    internal static void OnSessionEnded()
    {
        Session.AnnouncedMission = -1;
    }

    // ------------------------------------------------------------------ mission end (host decides)

    /// <summary>A client may end its mission only when the host said so (or the host is gone).</summary>
    internal static bool EndAllowed { get; private set; }

    internal static void HostEnded(int type, bool exit)
    {
        if (!Active || !CoopNet.IsHost) return;
        CoopNet.SendAll(new MsgWriter(Msg.GameEnd).I32(type).U8(exit ? (byte)1 : (byte)0).ToArray(), true);
        Session.Note(exit ? "you left the mission: everyone returns" : $"mission over ({(Game.GameEndType)type})",
            exit ? "你離開了關卡：所有人一起返回" : $"關卡結束（{Text.Outcome(type)}）");
    }

    internal static void OnGameEnd(int fromSlot, int type, bool exit)
    {
        if (fromSlot != 0 || !Active) return;
        EndMission(type, exit, $"the host ended the mission ({(Game.GameEndType)type}{(exit ? ", left" : "")})",
            exit ? "房主離開了關卡：一起返回" : $"房主結束了關卡（{Text.Outcome(type)}）");
    }

    private static bool _goalSent;

    /// <summary>
    /// A guest's own game cleared the mission: its army reached the goal first (every army walks on
    /// its own). The host clears the mission for everyone, once.
    /// </summary>
    internal static void GuestReachedGoal()
    {
        if (!Active || CoopNet.IsHost || _goalSent) return;
        _goalSent = true;
        CoopNet.SendTo(0, new MsgWriter(Msg.Goal).ToArray(), true);
        Session.Note("your army reached the goal", "你的部隊抵達終點了");
    }

    internal static void OnGoal(int fromSlot)
    {
        var game = Game.pGame_g;
        if (!Active || !CoopNet.IsHost || game == null || game.isGameEndOrder_) return;
        Session.Note($"{Session.Name(fromSlot)}'s army reached the goal", $"{Session.Name(fromSlot)} 的部隊抵達終點了");
        game.setGameEnd(Game.GameEndType.GameEndType_Clear);
    }

    /// <summary>A player left during the battle: their army fights on, following the host's drum.</summary>
    internal static void OnPlayerLeft(int slot, string name)
    {
        if (!Active || slot == 0) return; // without the host the mission ends (OnHostGone)
        Armies.Departed(slot, name);
        if (Armies.HandOver(slot) > 0) Session.Note($"{name}'s army now follows {Session.Name(0)}'s drum", $"{name} 的部隊改聽 {Session.Name(0)} 的鼓聲");
    }

    /// <summary>The host left the room or the connection dropped: end our side of the battle.</summary>
    internal static void OnHostGone()
    {
        if (!Active || CoopNet.IsHost) return;
        EndMission((int)Game.GameEndType.GameEndType_Failed, true, "lost the host: returning to the village", "和房主斷線了：返回村莊");
    }

    private static void EndMission(int type, bool exit, string why, string whyZh)
    {
        var game = Game.pGame_g;
        if (game == null) return;
        EndAllowed = true;
        Session.Note(why, whyZh);
        if (!game.isGameEndOrder_) game.setGameEnd((Game.GameEndType)type);
        if (exit) game.setExit();
    }

    internal static void Finish()
    {
        if (!Active) return;
        Active = false;
        SharedRandom.Stop();
        BattleClock.Reset();
        Difficulty.Reset();
        WorldSync.Reset();
        RestoreSinglePlayer();
        if (CoopNet.IsHost) CoopNet.SendAll(new MsgWriter(Msg.BattleEnd).I32(0).ToArray(), true);
        Session.AnnouncedMission = -1;
        Lobby.GoMission = -1;
        Session.Note($"battle over: sent {Sent}, received {Received} packets", "戰鬥結束");
    }

    /// <summary>Put the settings back so the next mission (if not co-op) is a normal single-player one.</summary>
    private static void RestoreSinglePlayer()
    {
        var s = Settings;
        if (s != null)
        {
            try { Armies.RestoreOwnLayout(s, Game.pGame_g); }
            catch (Exception e) { CoopPlugin.L.LogError("could not restore your own army into the layout: " + e); }
            s.isMultiMode = false;
            s.enableNet = false;
            s.hostPlayer = true;
            s.playerId = 0;
            s.playerNum = 1;
            for (int i = 0; i < Session.MaxPlayers; i++)
                s.aPlayerType[i] = i == 0 ? PlayerType.PlayerType_SelfCtrl : PlayerType.PlayerType_None;
        }
        var nc = Controller;
        if (nc != null && _initializedController)
        {
            try { nc.terminate(); }
            catch (Exception e) { CoopPlugin.L.LogWarning("network controller terminate failed: " + e.Message); }
            _initializedController = false;
        }
    }

    // ------------------------------------------------------------------ start handshake

    /// <summary>
    /// The game's own rule: the host waits until every client reported the sync scene, then
    /// goes ahead; a client waits until the host's sync scene arrives. Clients repeat theirs
    /// until they are let in, so a message sent before the host was listening is not lost.
    /// </summary>
    internal static void AfterSyncPlayStart(Game game)
    {
        if (!Active) return;
        var nc = Controller;
        if (nc == null) return;
        if (!CoopNet.IsHost)
        {
            if (!nc.IsNetworkSynced && UnityEngine.Time.frameCount >= _syncResendFrame)
            {
                _syncResendFrame = UnityEngine.Time.frameCount + 20;
                CoopNet.SendTo(0, new MsgWriter(Msg.SyncScene).U8(1).ToArray(), true);
                SyncSent++;
            }
        }
        else if (nc.IsNetworkSynced && !_announcedSync)
        {
            _announcedSync = true;
            CoopNet.SendAll(new MsgWriter(Msg.SyncScene).U8(1).ToArray(), true);
            SyncSent++;
            Session.Note("all players ready: battle starts", "所有玩家就緒：開戰！");
        }
    }

    internal static void OnSyncScene(int fromSlot, ulong fromId, byte scene)
    {
        var game = Game.pGame_g;
        if (!Active || game == null) return;
        SyncReceived++;
        game.OnSyncSceneReceived(fromId, new SessionManager.SyncSceneReceive(scene));
        // A client that missed our go-ahead keeps asking; answer it directly.
        if (CoopNet.IsHost && _announcedSync && Controller?.IsNetworkSynced == true)
            CoopNet.SendTo(fromSlot, new MsgWriter(Msg.SyncScene).U8(scene).ToArray(), true);
    }

    // ------------------------------------------------------------------ battle frames

    internal static void AfterUpdateSend(Game game)
    {
        if (!Active) return;
        var data = game.sendPacketData;
        var packet = data?.Packet;
        if (data == null || packet == null || packet.Length == 0) return;
        var bytes = new byte[packet.Length];
        for (int i = 0; i < bytes.Length; i++) bytes[i] = packet[i];
        var msg = new MsgWriter(Msg.GamePacket).I32(data.PlayerID).U16(data.ProtocolID).U16(data.Counter).Bytes(bytes).ToArray();
        // Clients talk to the host; the host talks to everyone. The battle protocol was made for
        // lossy wireless and repeats its latest state every frame, so it travels unreliably (newest
        // wins): a lost packet must not hold back the ones after it.
        CoopNet.SendTo(CoopNet.IsHost ? -1 : 0, msg, false);
        Sent++;
    }

    internal static void OnGamePacket(int fromSlot, ulong fromId, MsgReader r)
    {
        var game = Game.pGame_g;
        if (!Active || game == null) return;
        int playerId = r.I32();
        ushort protocol = r.U16(), counter = r.U16();
        var bytes = r.Bytes();
        if (bytes == null) return;
        var data = new SessionManager.GamePacketReceive
        {
            PlayerID = playerId,
            ProtocolID = protocol,
            Counter = counter,
            Packet = new Il2CppStructArray<byte>(bytes),
        };
        game.OnGamePacketReceived(fromId, data);
        Received++;
    }

    internal static void Tick() { }
}

[HarmonyPatch(typeof(Game), nameof(Game.initialize))]
internal static class GameInitializePatch
{
    private static void Prefix(Game __instance)
    {
        try { Battle.Prepare(__instance); }
        catch (Exception e) { CoopPlugin.L.LogError("co-op setup failed, mission runs single-player: " + e); }
    }
}

[HarmonyPatch(typeof(Game), nameof(Game.initialize))]
internal static class GameInitializeCommitPatch
{
    private static void Postfix(Game __instance)
    {
        try { Battle.Commit(__instance); }
        catch (Exception e) { CoopPlugin.L.LogError("co-op commit failed: " + e); }
    }
}

/// <summary>
/// Name tags: the HP gauge copies the four hero names from script string variables 2..5 when it
/// initialises. Those variables belong to the save, so lend them the player names only for the
/// duration of that call.
/// </summary>
[HarmonyPatch(typeof(P2.Game.Unit.HpGauge), nameof(P2.Game.Unit.HpGauge.initialize))]
internal static class PlayerNameTagsPatch
{
    private static void Prefix(out string?[]? __state)
    {
        __state = null;
        if (!Battle.Active) return;
        var vars = P2.LaboCommon.pLaboCommonInstance_g?.laboGlobalDataPtr_?.strVarAry;
        var ids = P2.Game.Unit.HpGauge.heroNameId_g;
        if (vars == null || ids == null) return;
        __state = new string?[ids.Length];
        for (int i = 0; i < ids.Length; i++)
        {
            int v = (int)ids[i];
            if (v < 0 || v >= vars.Length) continue;
            __state[i] = vars[v];
            if (Session.Occupied(i)) vars[v] = Session.Name(i);
        }
    }

    private static void Postfix(string?[]? __state)
    {
        if (__state == null) return;
        var vars = P2.LaboCommon.pLaboCommonInstance_g?.laboGlobalDataPtr_?.strVarAry;
        var ids = P2.Game.Unit.HpGauge.heroNameId_g;
        if (vars == null || ids == null) return;
        for (int i = 0; i < ids.Length && i < __state.Length; i++)
        {
            int v = (int)ids[i];
            if (v >= 0 && v < vars.Length && __state[i] != null) vars[v] = __state[i];
        }
    }
}

/// <summary>
/// Story rules for the flag bearer: in multiplayer the engine turns him into the Paragate egg and
/// makes him invulnerable. Run his reset with the multiplayer flag off.
/// </summary>
[HarmonyPatch(typeof(P2.Game.Unit.FlagUnit), nameof(P2.Game.Unit.FlagUnit.reset))]
internal static class StoryFlagBearerPatch
{
    private static void Prefix(out bool __state)
    {
        __state = false;
        if (!Battle.Active || Battle.Settings is not { } s || !s.isMultiMode) return;
        s.isMultiMode = false;
        __state = true;
    }

    private static void Postfix(bool __state)
    {
        if (__state && Battle.Settings is { } s) s.isMultiMode = true;
    }
}

/// <summary>
/// Game.initialize resets the player table to single-player values unless isMultiMode is already
/// set; we only switch that on afterwards (see Battle.Commit), so keep our table instead.
/// </summary>
[HarmonyPatch(typeof(GameSettingData), nameof(GameSettingData.setSingleModeParam))]
internal static class KeepPlayerTablePatch
{
    private static bool Prefix() => !Battle.Preparing;
}

/// <summary>
/// In multiplayer the march watcher builds the Paragate goal posts (egg run, "MULTI_MISSION_GOAL").
/// Story missions have none of that: keep their single-player march rules.
/// </summary>
[HarmonyPatch(typeof(P2.Game.Mission.WatchGameMarch), nameof(P2.Game.Mission.WatchGameMarch.setup))]
internal static class SkipParagateGoalsPatch
{
    private static bool Prefix() => !Battle.Active;
}

/// <summary>
/// The host's mission end (clear, failure, retreat) is the battle's end for everyone; a client's
/// own simulation may not end the battle by itself (it could differ slightly from the host's).
/// A guest's clear is its army reaching the goal: the host is told, and clears it for everyone.
/// </summary>
[HarmonyPatch(typeof(Game), nameof(Game.setGameEnd))]
internal static class GameEndPatch
{
    private static bool Prefix(Game __instance, Game.GameEndType gameEndType)
    {
        if (!Battle.Active || CoopNet.IsHost || Battle.EndAllowed || __instance.isGameEndOrder_) return true;
        if (gameEndType == Game.GameEndType.GameEndType_Clear) Battle.GuestReachedGoal();
        return false;
    }

    private static void Postfix(Game.GameEndType gameEndType)
    {
        try { if (Battle.Active && CoopNet.IsHost) Battle.HostEnded((int)gameEndType, false); }
        catch (Exception e) { CoopPlugin.L.LogError("could not announce the mission end: " + e); }
    }
}

[HarmonyPatch(typeof(Game), nameof(Game.setExit))]
internal static class GameExitPatch
{
    private static void Postfix(Game __instance)
    {
        try
        {
            if (!Battle.Active || !CoopNet.IsHost) return;
            var s = Battle.Settings;
            Battle.HostEnded(s == null ? (int)Game.GameEndType.GameEndType_Failed : (int)s.gameEndType, true);
        }
        catch (Exception e) { CoopPlugin.L.LogError("could not announce leaving the mission: " + e); }
    }
}

[HarmonyPatch(typeof(Game), nameof(Game.terminate))]
internal static class GameTerminatePatch
{
    private static void Prefix()
    {
        try { Battle.Finish(); }
        catch (Exception e) { CoopPlugin.L.LogError("co-op teardown failed: " + e); }
    }
}

/// <summary>Logs every mission phase change of a co-op battle (with the frame), for diagnosis.</summary>
[HarmonyPatch(typeof(Game), nameof(Game.update))]
internal static class PhaseLogPatch
{
    private static int _last = -1;

    private static void Postfix(Game __instance)
    {
        if (!Battle.Active) { _last = -1; return; }
        int phase = (int)__instance.gamePhase_;
        if (phase == _last) return;
        _last = phase;
        var nc = Battle.Controller;
        CoopPlugin.L.LogInfo($"[coop] f{UnityEngine.Time.frameCount} phase {__instance.gamePhase_} synced={nc?.IsNetworkSynced} endOrder={__instance.isGameEndOrder_}");
    }
}

[HarmonyPatch(typeof(Game), nameof(Game.gpfSyncPlayStart))]
internal static class SyncPlayStartPatch
{
    private static void Postfix(Game __instance)
    {
        try { Battle.AfterSyncPlayStart(__instance); }
        catch (Exception e) { CoopPlugin.L.LogError("sync handshake failed: " + e); }
    }
}

[HarmonyPatch(typeof(Game), nameof(Game.updateSend))]
internal static class UpdateSendPatch
{
    private static void Postfix(Game __instance)
    {
        try { Battle.AfterUpdateSend(__instance); }
        catch (Exception e) { CoopPlugin.L.LogError("could not send a battle frame: " + e); }
    }
}

/// <summary>
/// Every machine's troop position is its own player's army (see <see cref="ArmyPositions"/>). The
/// multiplayer protocol sends each machine's troop position every frame (PD_TroopBasePos) and
/// overwrites the receiver's with it: the game's own multiplayer kept one army for everybody, moving
/// in lockstep. Applying them would pull every army onto the others, so nobody takes them.
/// </summary>
[HarmonyPatch(typeof(P2.Game.Packet.PacketMng), nameof(P2.Game.Packet.PacketMng.procGamePacket))]
internal static class OwnTroopPositionPatch
{
    private const int TroopBasePos = 3; // P2.Game.Packet.PacketId.PID_TroopBasePos

    private static bool Prefix(P2.Game.Packet.GamePacket pGamePacket) =>
        !Battle.Active || (pGamePacket?.header?.id ?? 0) != TroopBasePos;
}

/// <summary>
/// The multiplayer protocol names squads and units by their ids (squad control and state, kill
/// squad, unit hit points, kill unit, wake unit); every such packet starts with the troop it is
/// about. In the game's own multiplayer every player had a single squad and the ids never clashed;
/// in co-op the host's first squads and a mission's own squads can share an id, and a client's
/// "squad 1 is gone" (a story companion fading) killed the host's squad 1 instead. So packets about
/// our own troop are left out in both directions: hit points and deaths there are the host's word
/// through <see cref="HitSync"/>. Packets about the enemy troop still go from the host to the
/// guests (enemy squads do not clash); without them a guest's enemies decided on their own (hunted
/// animals fleeing, squads leaving the field) and the screens drifted apart. The host takes none.
/// "Gimmick broken" (PID_KillGimmick) is left out too: HitSync already replays the break on guests,
/// and the game's own handler ran each break's event script (drops included) a second time.
/// </summary>
[HarmonyPatch(typeof(P2.Game.Packet.PacketMng), nameof(P2.Game.Packet.PacketMng.procGamePacket))]
internal static class SquadPacketsOffPatch
{
    // P2.Game.Packet.PacketId: PID_SquadCtrl = 4 ... PID_WakeupUnit = 9 (troop first), PID_KillGimmick = 10
    private const int FirstById = 4, LastById = 9, KillGimmick = 10;
    private const int EnemyTroop = 1; // the player troop is 0

    [HarmonyPriority(Priority.First)]
    private static bool Prefix(P2.Game.Packet.GamePacket pGamePacket)
    {
        if (!Battle.Active) return true;
        int id = pGamePacket?.header?.id ?? 0;
        if (id == KillGimmick) return false;
        if (id < FirstById || id > LastById) return true;
        if (CoopNet.IsHost) return false;
        var data = pGamePacket!.data?.data;
        if (data == null || data.Length < 4) return false;
        int troop = data[0] | data[1] << 8 | data[2] << 16 | data[3] << 24;
        return troop == EnemyTroop;
    }
}
