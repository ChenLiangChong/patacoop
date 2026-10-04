using System;
using System.Collections.Generic;
using PataCoop.Coop;
using UnityEngine;

namespace PataCoop;

/// <summary>
/// Player names over the battlefield. Units of the same class stand on the same spot when they
/// attack, so two players' armies can overlap exactly; a coloured label per player ("Bob x3")
/// over their squads shows who is where, with the command they drummed last. Overlapping labels
/// are stacked upwards. Every army walks on its own, so another army can be off screen: its label
/// then sits at that edge, high up, with an arrow and how many marches away it is.
/// </summary>
internal static class NameLabels
{
    private static readonly Color[] PlayerColors =
    {
        new(1f, 0.42f, 0.42f), new(0.30f, 0.67f, 0.97f), new(0.41f, 0.86f, 0.49f), new(1f, 0.83f, 0.23f),
    };

    private struct Label { public int Owner; public float X, Y; public int Units; public string Text; public int Side; }

    /// <summary>About how far one march takes an army (for "about 3 marches ahead").</summary>
    private const float MarchLength = 100f;

    private static readonly List<Label> Labels = new();
    private static int _builtFrame = -1;

    public static Color ColorOf(int slot) => PlayerColors[Math.Clamp(slot, 0, PlayerColors.Length - 1)];

    public static void Draw()
    {
        if (!Battle.Active || P2.Game.Game.pGame_g == null) return;
        if (_builtFrame != Time.frameCount)
        {
            _builtFrame = Time.frameCount;
            Build();
        }
        float scale = Screen.height / 540f;
        float w = 260 * scale, h = 20 * scale;
        var placed = new List<Rect>();
        var old = GUI.contentColor;
        foreach (var l in Labels)
        {
            var r = l.Side == 0
                ? new Rect(Mathf.Clamp(l.X - w / 2, 0, Screen.width - w), l.Y - h, w, h)
                : new Rect(l.Side < 0 ? 4 * scale : Screen.width - w - 4 * scale, 160 * scale, w, h); // below the squad gauges and the panel
            for (int guard = 0; guard < 4 && placed.Exists(p => p.Overlaps(r)); guard++) r.y -= h;
            placed.Add(r);
            Ui.Outlined(r, l.Text, ColorOf(l.Owner), 11 * scale);
        }
        GUI.contentColor = old;
    }

    private static void Build()
    {
        Labels.Clear();
        var troops = P2.Game.Game.pGame_g?.getUnitMng()?.unitTroopPtrArray_;
        if (troops == null || troops.Count == 0) return;
        var troop = troops[0];
        if (troop?.unitSquadPtrList_ == null) return;
        float head = 70 * (Screen.height / 540f);
        var sumX = new float[Session.MaxPlayers];
        var minY = new float[Session.MaxPlayers];
        var worldX = new float[Session.MaxPlayers];
        var count = new int[Session.MaxPlayers];
        foreach (var squad in troop.unitSquadPtrList_)
        {
            int tag = squad?.squadInfo_?.squadAddingParam?.rsv1 ?? 0;
            if ((tag & unchecked((int)0xFFFF0000)) != Armies.OwnerTag) continue;
            int owner = tag & 0xFF;
            if (owner >= Session.MaxPlayers || squad!.unitBasePtrList_ == null) continue;
            foreach (var unit in squad.unitBasePtrList_)
            {
                var model = unit?.pActorModel_?.TryCast<P2.Game.Unit.UnitModel>();
                if (model == null || !model.enableRender_) continue;
                float x = model.mtx_.m03, y = model.mtx_.m13;
                if (x < -1000) continue; // parked off-field (dead or not deployed)
                var s = P2.System.Gfx.Camera.CameraController.worldToScreenPos(new Vector3(x, y, 0));
                float gy = Screen.height - s.y - head;
                sumX[owner] += s.x;
                worldX[owner] += x;
                minY[owner] = count[owner] == 0 ? gy : Math.Min(minY[owner], gy);
                count[owner]++;
            }
        }
        int now = MarchRule.Now();
        var basePos = troop.troopBasePos_;
        float ourBase = basePos != null && basePos.Length > 0 ? basePos[0] : 0;
        for (int p = 0; p < Session.MaxPlayers; p++)
        {
            if (count[p] == 0) continue;
            string doing = MarchRule.Idle(p, now) ? Text.T("idle", "閒置") : MarchRule.Word(troop, p);
            string name = $"{Armies.ArmyName(p)} x{count[p]}" + (doing.Length > 0 ? $" · {doing}" : "");
            float screenX = sumX[p] / count[p];
            int side = screenX < 0 ? -1 : screenX > Screen.width ? 1 : 0;
            string text;
            if (side == 0) text = "▼ " + name;
            else
            {
                int marches = Math.Max(1, (int)Math.Round(Math.Abs(worldX[p] / count[p] - ourBase) / MarchLength));
                text = side < 0
                    ? "◀ " + name + Text.T($" (about {marches} marches behind)", $"（後方約 {marches} 次前進）")
                    : name + Text.T($" (about {marches} marches ahead)", $"（前方約 {marches} 次前進）") + " ▶";
            }
            Labels.Add(new Label { Owner = p, X = screenX, Y = minY[p], Units = count[p], Text = text, Side = side });
        }
        Labels.Sort((a, b) => a.Owner.CompareTo(b.Owner));
    }
}
