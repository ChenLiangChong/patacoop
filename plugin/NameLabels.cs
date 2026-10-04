using System;
using System.Collections.Generic;
using PataCoop.Coop;
using UnityEngine;

namespace PataCoop;

/// <summary>
/// Player names over the battlefield. Units of the same class stand on the same spot when they
/// attack, so two players' armies can overlap exactly; a coloured label per player ("Bob x3")
/// over their squads shows who is where, with the command they drummed last. Whoever holds the
/// march up (drumming something else while others march) blinks with "holding up". Overlapping
/// labels are stacked upwards.
/// </summary>
internal static class NameLabels
{
    private static readonly Color[] PlayerColors =
    {
        new(1f, 0.42f, 0.42f), new(0.30f, 0.67f, 0.97f), new(0.41f, 0.86f, 0.49f), new(1f, 0.83f, 0.23f),
    };

    private struct Label { public int Owner; public float X, Y; public int Units; public string Text; public bool Blocking; }

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
        bool blink = (int)(Time.unscaledTime * 3) % 2 == 0;
        var placed = new List<Rect>();
        var old = GUI.contentColor;
        foreach (var l in Labels)
        {
            var r = new Rect(Mathf.Clamp(l.X - w / 2, 0, Screen.width - w), l.Y - h, w, h);
            for (int guard = 0; guard < 4 && placed.Exists(p => p.Overlaps(r)); guard++) r.y -= h;
            placed.Add(r);
            Ui.Outlined(r, l.Text, l.Blocking && blink ? Color.white : ColorOf(l.Owner), 11 * scale);
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
                minY[owner] = count[owner] == 0 ? gy : Math.Min(minY[owner], gy);
                count[owner]++;
            }
        }
        int now = MarchRule.Now();
        var blockers = MarchRule.Blockers(troop, now);
        for (int p = 0; p < Session.MaxPlayers; p++)
        {
            if (count[p] == 0) continue;
            var stance = MarchRule.StanceOf(troop, p, now);
            string doing = stance == MarchRule.Stance.Idle ? Text.T("idle", "閒置") : MarchRule.Word(troop, p);
            bool blocking = blockers.Contains(p);
            string text = $"▼ {Armies.ArmyName(p)} x{count[p]}" + (doing.Length > 0 ? $" · {doing}" : "") + (blocking ? Text.T(" (holding up)", "（擋路）") : "");
            Labels.Add(new Label { Owner = p, X = sumX[p] / count[p], Y = minY[p], Units = count[p], Text = text, Blocking = blocking });
        }
        Labels.Sort((a, b) => a.Owner.CompareTo(b.Owner));
    }
}
