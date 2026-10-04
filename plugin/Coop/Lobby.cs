using System;
using System.Collections.Generic;
using System.Linq;
using HarmonyLib;
using P2.Bases.HeadQuarters;
using PataCoop.Net;

namespace PataCoop.Coop;

public enum PlayerState : byte { Unknown, Camp, Headquarters, Ready, InMission }

/// <summary>
/// Everything before the battle: where each player is, the host choosing the mission, and the
/// ready check. Only the host picks missions. When the host sorties from the headquarters, the
/// sortie is held until every other player is in the same mission's headquarters and has
/// confirmed their own sortie; then the host's "go" releases everyone at once.
/// </summary>
public static class Lobby
{
    private static readonly PlayerState[] States = new PlayerState[Session.MaxPlayers];
    private static readonly int[] Missions = { -1, -1, -1, -1 };
    private static readonly Dictionary<int, string> Names = new();
    private static string _lastWait = "";
    private static int _nextPoll;
    private static int _worldMapPick = -1;

    /// <summary>
    /// The mission picked on the world map. The headquarters itself has no mission id yet (the camp
    /// script writes it only after the headquarters closes), but the world map's selection is the id.
    /// </summary>
    public static int PickedMission => _worldMapPick;

    /// <summary>Mission the host is preparing in its headquarters (-1 = none).</summary>
    public static int HostMission { get; private set; } = -1;

    /// <summary>The mission the host released with "go": the next mission start is a co-op battle.</summary>
    public static int GoMission { get; internal set; } = -1;

    public static PlayerState StateOf(int slot) => slot >= 0 && slot < States.Length ? States[slot] : PlayerState.Unknown;
    public static int MissionOf(int slot) => slot >= 0 && slot < Missions.Length ? Missions[slot] : -1;

    /// <summary>Display name of a mission (learned when a mission starts, or from other players).</summary>
    public static string NameOf(int mission) => mission < 0 ? "-" : Names.TryGetValue(mission, out var n) && n.Length > 0 ? n : Text.T($"mission {mission}", $"關卡 {mission}");

    internal static void LearnName(int mission, string? name)
    {
        if (mission >= 0 && !string.IsNullOrEmpty(name)) Names[mission] = name!;
    }

    public static string Describe(PlayerState s, int mission) => s switch
    {
        PlayerState.Camp => Text.T("camp", "營地"),
        PlayerState.Headquarters => Text.T($"preparing {NameOf(mission)}", $"整備中：{NameOf(mission)}"),
        PlayerState.Ready => Text.T($"READY {NameOf(mission)}", $"準備完成：{NameOf(mission)}"),
        PlayerState.InMission => Text.T($"in {NameOf(mission)}", $"關卡中：{NameOf(mission)}"),
        _ => "...",
    };

    // ------------------------------------------------------------------ own state

    internal static void Tick()
    {
        WatchWorldMap();
        if (!Session.InRoom || UnityEngine.Time.frameCount < _nextPoll) return;
        _nextPoll = UnityEngine.Time.frameCount + 15;
        var (state, mission) = Observe();
        int me = CoopNet.MySlot;
        if (me < 0) return;
        // "ready" is set by the sortie gate and lasts until we leave the headquarters
        if (States[me] == PlayerState.Ready && state == PlayerState.Headquarters) return;
        SetMine(state, mission);
    }

    private static (PlayerState, int) Observe()
    {
        var scene = Labo2Application.getApplication()?.lgsm_g?.scene_?.scene_;
        var camp = scene?.TryCast<P2.Bases.Camp.Scene>();
        if (camp?.controller_ != null)
            return camp.controller_.getPhase() == P2.Bases.Camp.Controller.Phase.Phase_HeadQuarters
                ? (PlayerState.Headquarters, _worldMapPick)
                : (PlayerState.Camp, -1);
        // the last mission's game object stays around (phase None) after the battle
        var game = P2.Game.Game.pGame_g;
        if (game != null && game.gamePhase_ != P2.Game.Game.GamePhase.GamePhase_None)
            return (PlayerState.InMission, P2.LaboCommon.pLaboCommonInstance_g?.laboSettingDataPtr_?.gameSettingData?.missionId ?? -1);
        return (PlayerState.Unknown, -1);
    }

    private static int _campFrame = -1;
    private static bool _inCamp;

    /// <summary>
    /// The Patapon 2 camp (with its world map and headquarters) is on screen: the only place where
    /// the panel takes the mouse and the keyboard. Never during a mission, single-player or not, nor in Patapon 1.
    /// </summary>
    internal static bool InCamp
    {
        get
        {
            int frame = UnityEngine.Time.frameCount;
            if (frame == _campFrame) return _inCamp;
            _campFrame = frame;
            try { _inCamp = Labo2Application.getApplication()?.lgsm_g?.scene_?.scene_?.TryCast<P2.Bases.Camp.Scene>()?.controller_ != null; }
            catch (System.Exception) { _inCamp = false; }
            return _inCamp;
        }
    }

    /// <summary>Every frame while the world map is open: remember the highlighted mission (the one a sortie picks).</summary>
    private static void WatchWorldMap()
    {
        var camp = Labo2Application.getApplication()?.lgsm_g?.scene_?.scene_?.TryCast<P2.Bases.Camp.Scene>();
        var controller = camp?.controller_;
        if (controller == null || controller.getPhase() != P2.Bases.Camp.Controller.Phase.Phase_WorldMap) return;
        var map = controller.worldMap_;
        if (map != null && map.isSelect()) _worldMapPick = map.getSelect();
    }

    internal static void SetMine(PlayerState state, int mission)
    {
        int me = CoopNet.MySlot;
        if (me < 0 || (States[me] == state && Missions[me] == mission)) return;
        var before = States[me];
        States[me] = state;
        Missions[me] = mission;
        CoopNet.SendAll(new MsgWriter(Msg.State).U8((byte)state).I32(mission).Str(Names.GetValueOrDefault(mission, "")).ToArray(), true);
        if (state == PlayerState.Camp && before != PlayerState.Camp) KeyItems.BackInCamp();

        if (state == PlayerState.Headquarters && before != PlayerState.Ready)
        {
            GoMission = -1; // a fresh preparation: any earlier release is stale
            Armies.PublishOwn("headquarters");
            if (CoopNet.IsHost) AnnounceHostMission(mission);
        }
        // (leaving the headquarters after "go" passes through the camp on the way to the mission,
        // so the release must survive until the mission starts; Battle.Finish clears it)
        if (CoopNet.IsHost && state is PlayerState.Camp or PlayerState.Unknown && HostMission >= 0 && before != PlayerState.Ready) AnnounceHostMission(-1);
    }

    private static void AnnounceHostMission(int mission)
    {
        HostMission = mission;
        CoopNet.SendAll(new MsgWriter(Msg.Lobby).I32(mission).Str(Names.GetValueOrDefault(mission, "")).ToArray(), true);
    }

    /// <summary>A new member needs to know where everyone is.</summary>
    internal static void Resend()
    {
        int me = CoopNet.MySlot;
        if (me < 0) return;
        CoopNet.SendAll(new MsgWriter(Msg.State).U8((byte)States[me]).I32(Missions[me]).Str(Names.GetValueOrDefault(Missions[me], "")).ToArray(), true);
        if (CoopNet.IsHost) CoopNet.SendAll(new MsgWriter(Msg.Lobby).I32(HostMission).Str(Names.GetValueOrDefault(HostMission, "")).ToArray(), true);
    }

    internal static void Forget(int slot)
    {
        if (slot < 0 || slot >= States.Length) return;
        States[slot] = PlayerState.Unknown;
        Missions[slot] = -1;
    }

    // ------------------------------------------------------------------ messages

    internal static void OnState(int fromSlot, MsgReader r)
    {
        var state = (PlayerState)r.U8();
        int mission = r.I32();
        LearnName(mission, r.Str());
        if (fromSlot < 0 || fromSlot >= States.Length) return;
        if (States[fromSlot] == state && Missions[fromSlot] == mission) return;
        States[fromSlot] = state;
        Missions[fromSlot] = mission;
        string who = Session.Name(fromSlot);
        switch (state)
        {
            case PlayerState.Headquarters: Session.Note($"{who} is preparing mission {mission}", $"{who} 正在整備：{NameOf(mission)}"); break;
            case PlayerState.Ready: Session.Note($"{who} is ready", $"{who} 準備完成"); break;
            case PlayerState.InMission: Session.Note($"{who} entered mission {mission}", $"{who} 進入了關卡：{NameOf(mission)}"); break;
            case PlayerState.Camp: Session.Note($"{who} is in the camp", $"{who} 回到了營地"); break;
        }
    }

    internal static void OnLobby(int fromSlot, MsgReader r)
    {
        if (fromSlot != 0) return;
        int mission = r.I32();
        LearnName(mission, r.Str());
        if (mission == HostMission) return;
        HostMission = mission;
        if (mission >= 0 && !CoopNet.IsHost)
            Session.Note($"the host picked {NameOf(mission)}: pick it on the world map and sortie to get ready", $"房主選了「{NameOf(mission)}」：在世界地圖選同一關、進整備出擊就會準備完成");
    }

    internal static void OnGo(int fromSlot, MsgReader r)
    {
        if (fromSlot != 0) return;
        GoMission = r.I32();
        Difficulty.Use(r.F32(), r.F32());
        SharedRandom.Seed = r.I32();
        Session.Note($"the host sorties: {NameOf(GoMission)} (enemy HP x{Difficulty.EnemyHp:0.##}, damage x{Difficulty.EnemyDamage:0.##})",
            $"房主出擊：{NameOf(GoMission)}（敵人血量 x{Difficulty.EnemyHp:0.##}、傷害 x{Difficulty.EnemyDamage:0.##}）");
    }

    // ------------------------------------------------------------------ sortie gate

    /// <summary>
    /// Called while a headquarters sortie is confirmed. Returns false to hold the sortie this frame.
    /// </summary>
    internal static bool CanSortie(int mission)
    {
        if (!Session.CoopActive) return true;
        int me = CoopNet.MySlot;

        if (!CoopNet.IsHost)
        {
            if (GoMission >= 0 && GoMission == mission) return true;
            if (States[me] != PlayerState.Ready)
            {
                Armies.PublishOwn("ready");
                SetMine(PlayerState.Ready, mission);
                Session.Note(HostMission == mission
                    ? "ready: waiting for the host to sortie"
                    : $"ready for {NameOf(mission)}, but the host is preparing {(HostMission < 0 ? "nothing yet" : NameOf(HostMission))}",
                    HostMission == mission
                    ? "準備完成：等房主出擊"
                    : $"你準備的是「{NameOf(mission)}」，但房主{(HostMission < 0 ? "還沒選關卡" : $"選的是「{NameOf(HostMission)}」")}");
            }
            return false;
        }

        if (States[me] != PlayerState.Ready) SetMine(PlayerState.Ready, mission);
        // battle rules change between versions: different versions would build different battles
        for (int p = 0; p < Session.MaxPlayers; p++)
        {
            if (p == me || !Session.Occupied(p) || Session.VersionOf(p) == CoopPlugin.Version) continue;
            string other = Session.VersionOf(p) ?? "?";
            string text = $"{Session.Name(p)} runs PataCoop {other} (you run {CoopPlugin.Version}): everyone needs the same version";
            if (text != _lastWait) { _lastWait = text; Session.Note(text, $"{Session.Name(p)} 的 PataCoop 是 {other} 版（你是 {CoopPlugin.Version}），大家要用同一版才能出擊"); }
            return false;
        }
        var waiting = new List<string>();
        for (int p = 0; p < Session.MaxPlayers; p++)
        {
            if (p == me || !Session.Occupied(p)) continue;
            if (States[p] != PlayerState.Ready || Missions[p] != mission || Armies.Formations[p] == null)
                waiting.Add($"{Session.Name(p)} ({Describe(States[p], Missions[p])})");
        }
        if (waiting.Count > 0)
        {
            string text = "waiting for " + string.Join(", ", waiting);
            if (text != _lastWait) { _lastWait = text; Session.Note(text, "等待 " + string.Join("、", waiting)); }
            return false;
        }
        _lastWait = "";
        GoMission = mission;
        var (hp, damage) = Difficulty.ForPlayers(Session.PlayerCount);
        Difficulty.Use(hp, damage);
        SharedRandom.Seed = new System.Random().Next();
        CoopNet.SendAll(new MsgWriter(Msg.Go).I32(mission).F32(hp).F32(damage).I32(SharedRandom.Seed).ToArray(), true);
        Session.Note("everyone is ready: sortie!", "全員準備完成：出擊！");
        return true;
    }
}

/// <summary>
/// The headquarters state machine runs inline in updateMe: idle -> check -> finish update (writes
/// the formation into the layout) -> update end (fades out and leaves for the mission). We hold
/// in "update end" while the sortie is not released: the formation is final and written, so it is
/// what we share, and the screen keeps drawing because we still update the squad view.
/// </summary>
[HarmonyPatch(typeof(HeadQuartersMainObserver), nameof(HeadQuartersMainObserver.updateMe))]
internal static class SortieGatePatch
{
    private const int StateUpdateEnd = 10;

    private static bool Prefix(HeadQuartersMainObserver __instance, uint dt)
    {
        try
        {
            if ((int)__instance.state_ != StateUpdateEnd || !Session.CoopActive) return true;
            var model = __instance.squadOrgObserver_?.paragetoSquadModel_;
            if (model == null || model.result_ != SquadModel.Result.Result_Decide) return true;
            if (Lobby.CanSortie(Lobby.PickedMission)) return true;
            if (__instance.isReadyResource_) __instance.squadOrgObserver_!.update(dt);
            return false;
        }
        catch (Exception e)
        {
            CoopPlugin.L.LogError("sortie gate failed, letting the sortie through: " + e);
            return true;
        }
    }
}

/// <summary>Whenever the game writes the player's formation into the layout, share the new army.</summary>
[HarmonyPatch(typeof(P2.Bases.Organization.Managed.OrganizationManager), nameof(P2.Bases.Organization.Managed.OrganizationManager.setupAddingParam))]
internal static class FormationChangedPatch
{
    private static void Postfix()
    {
        try { if (Session.InRoom) Armies.PublishOwn("formation written"); }
        catch (Exception e) { CoopPlugin.L.LogWarning("could not share the formation: " + e.Message); }
    }
}
