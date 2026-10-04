using System;
using PataCoop.Net;
using UnityEngine.InputSystem;

namespace PataCoop;

/// <summary>F7 host, F8 join, F9 leave, F10 toggle the panel.</summary>
internal static class Hotkeys
{
    public static void Tick()
    {
        var kb = Keyboard.current;
        if (kb == null || Ui.Focus != null) return;
        if (kb.f7Key.wasPressedThisFrame) Actions.Host();
        if (kb.f8Key.wasPressedThisFrame) Actions.Join();
        if (kb.f9Key.wasPressedThisFrame) Actions.Leave();
        if (kb.f10Key.wasPressedThisFrame) Overlay.Visible = !Overlay.Visible;
    }
}

/// <summary>What the hotkeys (and the dev harness) can do.</summary>
public static class Actions
{
    public static bool Host() => CoopNet.HostGame(CoopPlugin.Port.Value, CoopPlugin.PlayerName.Value);

    public static bool Join(string? address = null, string code = "")
    {
        address ??= CoopPlugin.HostAddress.Value;
        if (string.IsNullOrWhiteSpace(address)) address = "127.0.0.1";
        return CoopNet.JoinGame(address.Trim(), CoopPlugin.Port.Value, CoopPlugin.PlayerName.Value, code);
    }

    public static void Leave() => CoopNet.Disconnect();

    /// <summary>Capture the own formation, serialise, parse and serialise again: the bytes must match.</summary>
    public static string FormationSelfTest()
    {
        var layout = P2.LaboCommon.pLaboCommonInstance_g?.laboSettingDataPtr_?.gameSettingData?.unitSettingData?.playerUnitLayoutParam;
        if (layout == null) return "no layout";
        var f = Coop.Formation.Capture(layout);
        var a = f.ToBytes();
        var b = Coop.Formation.FromBytes(a).ToBytes();
        return $"{f} | {a.Length} bytes | round trip {(a.AsSpan().SequenceEqual(b) ? "OK" : "MISMATCH")}";
    }
}
