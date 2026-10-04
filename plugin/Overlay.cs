using System;
using System.Linq;
using System.Text;
using PataCoop.Coop;
using PataCoop.Net;
using UnityEngine;

namespace PataCoop;

/// <summary>
/// The panel in the top-right corner. Out of a room it is the lobby: your name, "open a room",
/// the rooms found on the Radmin / LAN network with a join button each, and joining by IP. In a
/// room it shows who is there and how the battle goes. F10 folds it to a small tab.
/// </summary>
internal static class Overlay
{
    public static bool Visible = true;
    private static string _error = "";
    private static int _builtFrame = -1;
    private static string _text = "";
    private static string? _name, _ip;
    private const string NameField = "name", IpField = "ip";

    /// <summary>The room search runs while the lobby part of the panel is on screen (in the Patapon 2 camp).</summary>
    public static bool WantsRooms => Visible && CoopNet.State == NetState.Offline && Lobby.InCamp;

    public static void Fail(Exception e)
    {
        if (_error.Length == 0) CoopPlugin.L.LogWarning("overlay failed: " + e);
        _error = e.Message;
    }

    public static void Draw()
    {
        NameLabels.Draw();
        // only the camp's panel takes clicks; everywhere else (missions, Patapon 1, menus) it is text at most
        Ui.Panel = default;
        bool camp = Lobby.InCamp;
        if (!camp && CoopNet.State == NetState.Offline) return;
        float u = Ui.U;
        if (!Visible)
        {
            if (!camp) return;
            var tab = new Rect(Screen.width - 150 * u - 12 * u, 8 * u, 150 * u, 26 * u);
            Ui.Panel = tab;
            if (Ui.Button(tab, "PataCoop  (F10)")) Visible = true;
            Ui.EndPanel();
            return;
        }
        float w = 480 * u;
        var at = new Vector2(Screen.width - w - 12 * u, 8 * u);
        switch (CoopNet.State)
        {
            case NetState.InRoom: DrawRoom(at, w, camp); break;
            case NetState.Offline: DrawLobby(at, w); break;
            default: DrawConnecting(at, w, camp); break;
        }
        Ui.EndPanel();
    }

    // ---- out of a room ----------------------------------------------------------------------------

    private static void DrawLobby(Vector2 at, float w)
    {
        float u = Ui.U, pad = 10 * u, row = 30 * u, gap = 6 * u;
        _name ??= CoopPlugin.PlayerName.Value;
        _ip ??= CoopPlugin.HostAddress.Value;
        var rooms = RoomFinder.Rooms;
        int roomRows = Math.Max(1, Math.Min(4, rooms.Count));
        bool status = CoopNet.Status != Text.T("offline", "未連線");
        float h = pad * 2 + row * (5 + roomRows + (status ? 1 : 0)) + gap * 6;
        Ui.Panel = new Rect(at.x, at.y, w, h);
        Ui.Fill(Ui.Panel, Ui.Back);
        float x = at.x + pad, y = at.y + pad, inner = w - pad * 2;

        Title(x, ref y, inner, row, gap);

        Ui.Label(new Rect(x, y, 100 * u, row), Text.T("Your name", "你的名字"));
        _name = Ui.Field(new Rect(x + 100 * u, y, 220 * u, row), NameField, _name, 16, c => true, out _, ime: true);
        if (Ui.Focus != NameField) SaveName();
        y += row + gap;

        if (Ui.Button(new Rect(x, y, 150 * u, row), Text.T("Open a room (F7)", "開房間 (F7)"))) Host();
        string mine = Networks.Radmin.Length > 0 ? Text.T($"your Radmin IP: {Networks.Radmin}", $"你的 Radmin IP：{Networks.Radmin}")
            : Networks.Lan.Length > 0 ? Text.T($"your LAN IP: {Networks.Lan} (no Radmin VPN)", $"你的區網 IP：{Networks.Lan}（沒有 Radmin VPN）")
            : "";
        Ui.Label(new Rect(x + 160 * u, y, inner - 160 * u, row), mine, Ui.Dim);
        y += row + gap;

        Ui.Fill(new Rect(x, y, inner, 1), Ui.Line);
        y += gap;
        Ui.Label(new Rect(x, y, inner, row), Text.T("Rooms on your network:", "同一個網路上的房間："));
        y += row;
        if (rooms.Count == 0)
        {
            Ui.Label(new Rect(x + 12 * u, y, inner - 12 * u, row), Text.T("searching… a room shows up here once its host opens it", "搜尋中…房主按「開房間」後就會出現在這裡"), Ui.Dim);
            y += row;
        }
        foreach (var room in rooms.Take(4))
        {
            string who = Text.T($"{room.Host}'s room  {room.Players}/{room.Capacity}  {room.Address}", $"{room.Host} 的房間  {room.Players}/{room.Capacity} 人  {room.Address}");
            Ui.Label(new Rect(x + 12 * u, y, inner - 110 * u, row), who);
            string action = !room.SameProtocol ? Text.T("other version", "版本不同") : !room.Open ? Text.T("full", "已滿") : Text.T("Join", "加入");
            if (Ui.Button(new Rect(x + inner - 90 * u, y + 2 * u, 90 * u, row - 4 * u), action, room.SameProtocol && room.Open))
                Join(room.Address, room.Code);
            y += row;
        }
        y += gap;

        Ui.Label(new Rect(x, y, 100 * u, row), Text.T("Join by IP", "用 IP 加入"));
        _ip = Ui.Field(new Rect(x + 100 * u, y, 220 * u, row), IpField, _ip, 64, c => char.IsLetterOrDigit(c) || c == '.' || c == '-', out bool enter);
        bool hasIp = _ip.Trim().Length > 0;
        if ((Ui.Button(new Rect(x + inner - 90 * u, y, 90 * u, row), Text.T("Join", "加入"), hasIp) || enter) && hasIp) Join(_ip.Trim(), "");
        y += row + gap;

        if (status) Ui.Label(new Rect(x, y, inner, row), CoopNet.Status, Ui.Warn);
    }

    private static void DrawConnecting(Vector2 at, float w, bool camp)
    {
        float u = Ui.U, pad = 10 * u, row = 30 * u, gap = 6 * u;
        var rect = new Rect(at.x, at.y, w, pad * 2 + row * (camp ? 3 : 1) + gap * (camp ? 2 : 0));
        if (camp) Ui.Panel = rect;
        Ui.Fill(rect, Ui.Back);
        float x = at.x + pad, y = at.y + pad, inner = w - pad * 2;
        if (camp) Title(x, ref y, inner, row, gap);
        Ui.Label(new Rect(x, y, inner, row), CoopNet.Status);
        y += row + gap;
        if (camp && Ui.Button(new Rect(x, y, 120 * u, row), Text.T("Cancel", "取消"))) Actions.Leave();
    }

    // ---- in a room -----------------------------------------------------------------------------------

    private static void DrawRoom(Vector2 at, float w, bool camp)
    {
        float u = Ui.U, pad = 10 * u, row = 30 * u, gap = 6 * u;
        if (_builtFrame != Time.frameCount)
        {
            _builtFrame = Time.frameCount;
            _text = Build();
        }
        int lines = _text.Count(c => c == '\n') + 1;
        float textH = lines * Ui.LineHeight + 4 * u;
        // outside the camp (in battle) only the text: no title and no buttons, and the panel takes no
        // clicks, so the game keeps its keys and its cursor (F9 still leaves)
        bool full = camp;
        var rect = new Rect(at.x, at.y, w, pad * 2 + textH + (full ? row * 2 + gap * 2 : 0));
        if (full) Ui.Panel = rect;
        Ui.Fill(rect, Ui.Back);
        float x = at.x + pad, y = at.y + pad, inner = w - pad * 2;
        if (full) Title(x, ref y, inner, row, gap);
        Ui.Block(new Rect(x, y, inner, textH), _text);
        if (!full) return;
        y += textH + gap;
        if (Ui.Button(new Rect(x, y, 150 * u, row), Text.T("Leave room (F9)", "離開房間 (F9)"))) Actions.Leave();
        if (CoopNet.IsHost && Networks.Radmin.Length > 0)
            Ui.Label(new Rect(x + 160 * u, y, inner - 160 * u, row), Text.T($"friends see your room ({Networks.Radmin})", $"朋友會自動看到你的房間（{Networks.Radmin}）"), Ui.Dim);
    }

    private static void Title(float x, ref float y, float inner, float row, float gap)
    {
        float u = Ui.U;
        Ui.Label(new Rect(x, y, inner - 110 * u, row), "PataCoop " + CoopPlugin.Version + (_error.Length > 0 ? "  (!)" : ""));
        if (Ui.Button(new Rect(x + inner - 100 * u, y + 2 * u, 100 * u, row - 4 * u), Text.T("Hide (F10)", "收起 (F10)"))) Visible = false;
        y += row + gap;
    }

    private static string Build()
    {
        var sb = new StringBuilder();
        sb.Append(Text.T($"room {CoopNet.RoomCode} · {(CoopNet.IsHost ? "host" : "guest")} · {Session.PlayerCount}/4 · ping {CoopNet.Ping} ms",
            $"房間 {CoopNet.RoomCode} · {(CoopNet.IsHost ? "房主" : "客人")} · {Session.PlayerCount}/4 人 · 延遲 {CoopNet.Ping} ms"));
        for (int i = 0; i < Session.MaxPlayers; i++)
        {
            if (!Session.Occupied(i)) continue;
            sb.Append($"\nP{i + 1} {Session.Name(i)}{(i == CoopNet.MySlot ? Text.T(" (you)", "（你）") : "")}: {Lobby.Describe(Lobby.StateOf(i), Lobby.MissionOf(i))}");
        }
        if (Battle.Active)
        {
            sb.Append(Text.T($"\nbattle: {Battle.State} · enemy HP x{Difficulty.EnemyHp:0.##} dmg x{Difficulty.EnemyDamage:0.##}",
                $"\n戰鬥：{Battle.State} · 敵人血量 x{Difficulty.EnemyHp:0.##} 傷害 x{Difficulty.EnemyDamage:0.##}"));
            sb.Append('\n').Append(Clock.Describe()).Append(" · ").Append(HitSync.Describe());
            var troops = P2.Game.Game.pGame_g?.getUnitMng()?.unitTroopPtrArray_;
            if (troops != null && troops.Count > 0 && troops[0] != null)
            {
                var blockers = MarchRule.Blockers(troops[0], MarchRule.Now());
                if (blockers.Count > 0)
                    sb.Append(Text.T("\nmarch held up by: ", "\n前進被擋：")).Append(string.Join("、", blockers.Select(p => $"{Session.Name(p)}（{MarchRule.Word(troops[0], p)}）")));
            }
        }
        else if (!CoopNet.IsHost && Lobby.HostMission >= 0) sb.Append(Text.T($"\nhost is preparing {Lobby.NameOf(Lobby.HostMission)}", $"\n房主正在整備：{Lobby.NameOf(Lobby.HostMission)}"));
        if (Session.LastEvent.Length > 0) sb.Append('\n').Append(Session.LastEvent.TrimEnd('\n'));
        return sb.ToString();
    }

    // ---- actions ---------------------------------------------------------------------------------------

    private static void SaveName()
    {
        if (_name == null) return;
        string name = _name.Trim();
        if (name.Length == 0) _name = name = CoopPlugin.PlayerName.Value;
        if (name != CoopPlugin.PlayerName.Value) CoopPlugin.PlayerName.Value = name;
    }

    private static void Host()
    {
        SaveName();
        Actions.Host();
    }

    private static void Join(string address, string code)
    {
        SaveName();
        if (CoopPlugin.HostAddress.Value != address) CoopPlugin.HostAddress.Value = address;
        _ip = address;
        Actions.Join(address, code);
    }
}
