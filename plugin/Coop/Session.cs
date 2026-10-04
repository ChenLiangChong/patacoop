using System;
using System.Linq;
using PataCoop.Net;

namespace PataCoop.Coop;

/// <summary>Room membership as the game sees it: who sits in which slot and what they are called.</summary>
public static class Session
{
    public const int MaxPlayers = 4;
    private static readonly string[] Names = new string[MaxPlayers];
    private static readonly string?[] Versions = new string?[MaxPlayers];
    private static bool _subscribed;

    /// <summary>Mission the host announced for the next co-op battle; -1 = none.</summary>
    public static int AnnouncedMission { get; internal set; } = -1;
    public static string LastEvent { get; private set; } = "";

    public static bool InRoom => CoopNet.State == NetState.InRoom;
    public static int PlayerCount => CoopNet.Roster.Count(id => id != ulong.MaxValue);
    /// <summary>A co-op battle needs a room with at least one other player.</summary>
    public static bool CoopActive => InRoom && PlayerCount >= 2;

    public static bool Occupied(int slot) => slot >= 0 && slot < CoopNet.Roster.Length && CoopNet.Roster[slot] != ulong.MaxValue;
    public static ulong UserId(int slot) => Occupied(slot) ? CoopNet.Roster[slot] : 0;
    public static string Name(int slot) => Occupied(slot) ? Names[slot] ?? $"P{slot + 1}" : "";

    /// <summary>The PataCoop version a player runs (ours for our own slot; null until they said hello).</summary>
    public static string? VersionOf(int slot) => slot == CoopNet.MySlot ? CoopPlugin.Version : Occupied(slot) ? Versions[slot] : null;

    public static int SlotOf(ulong userId)
    {
        for (int i = 0; i < CoopNet.Roster.Length; i++) if (CoopNet.Roster[i] == userId) return i;
        return -1;
    }

    /// <summary>Tell the player something (in their language) and log it (in English).</summary>
    internal static void Note(string en, string zh)
    {
        LastEvent = Text.T(en, zh);
        CoopPlugin.L.LogInfo("[coop] " + en);
    }

    public static void Tick()
    {
        if (!_subscribed)
        {
            _subscribed = true;
            CoopNet.Message += OnMessage;
            CoopNet.RosterChanged += OnRosterChanged;
        }
        Battle.Tick();
        WorldSync.Tick();
        Clock.Tick();
        HitSync.Tick();
        Lobby.Tick();
    }

    private static void OnRosterChanged()
    {
        if (CoopNet.MySlot >= 0) Names[CoopNet.MySlot] = CoopPlugin.PlayerName.Value;
        for (int i = 0; i < MaxPlayers; i++)
        {
            if (Occupied(i)) continue;
            if (Names[i] != null && i != CoopNet.MySlot)
            {
                Note($"{Names[i]} left the room", $"{Names[i]} 離開了房間");
                Battle.OnPlayerLeft(i, Names[i]);
            }
            Names[i] = null!;
            Versions[i] = null;
            Armies.Forget(i);
            Lobby.Forget(i);
        }
        if (InRoom)
        {
            CoopNet.SendAll(new MsgWriter(Msg.Hello).Str(CoopPlugin.PlayerName.Value).Str(CoopPlugin.Version).ToArray(), true);
            Lobby.Resend();
            Armies.PublishOwn("roster changed");
        }
        if (!CoopActive) Battle.OnSessionEnded();
        if (!InRoom || !Occupied(0)) Battle.OnHostGone();
    }

    private static void OnMessage(int fromSlot, ulong fromId, byte[] payload)
    {
        MsgReader r;
        try { r = new MsgReader(payload); }
        catch { return; }
        try
        {
            switch (r.Kind)
            {
                case Msg.Hello:
                    if (fromSlot >= 0 && fromSlot < MaxPlayers)
                    {
                        string name = r.Str(), version = r.Str();
                        bool isNew = Names[fromSlot] != name;
                        Names[fromSlot] = name;
                        Versions[fromSlot] = version;
                        if (version != CoopPlugin.Version) Note($"{name} runs PataCoop {version}, you run {CoopPlugin.Version}", $"{name} 的 PataCoop 是 {version} 版，你是 {CoopPlugin.Version} 版，請用同一版");
                        else if (isNew) Note($"{name} is in slot {fromSlot + 1}", $"{name} 加入了（P{fromSlot + 1}）");
                    }
                    break;
                case Msg.Battle:
                    Battle.OnAnnounced(fromSlot, r);
                    break;
                case Msg.SyncScene:
                    Battle.OnSyncScene(fromSlot, fromId, r.U8());
                    break;
                case Msg.GamePacket:
                    Battle.OnGamePacket(fromSlot, fromId, r);
                    break;
                case Msg.BattleEnd:
                    Battle.OnEndAnnounced(fromSlot, r.I32());
                    break;
                case Msg.Formation:
                    Armies.OnFormation(fromSlot, r);
                    break;
                case Msg.Lobby:
                    Lobby.OnLobby(fromSlot, r);
                    break;
                case Msg.State:
                    Lobby.OnState(fromSlot, r);
                    break;
                case Msg.Go:
                    Lobby.OnGo(fromSlot, r);
                    break;
                case Msg.GameEnd:
                    Battle.OnGameEnd(fromSlot, r.I32(), r.U8() != 0);
                    break;
                case Msg.Weather:
                    WorldSync.OnWeather(fromSlot, r);
                    break;
                case Msg.Clock:
                    Clock.OnClock(fromSlot, r);
                    break;
                case Msg.Hits:
                    HitSync.OnHits(fromSlot, r);
                    break;
                case Msg.HitPoints:
                    HitSync.OnHitPoints(fromSlot, r);
                    break;
                case Msg.KeyItem:
                    KeyItems.OnKeyItem(fromSlot, r);
                    break;
            }
        }
        catch (Exception e)
        {
            CoopPlugin.L.LogWarning($"could not handle {r.Kind} from slot {fromSlot}: {e.Message}");
        }
    }
}
