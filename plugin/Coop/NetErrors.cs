using HarmonyLib;
using P2.GameSystem.Network;

namespace PataCoop.Coop;

/// <summary>
/// The game's own network watchdog was made for PSPs side by side: a couple of seconds without a
/// packet count as a lost connection and the mission is abandoned (and restarted single-player).
/// Over the internet one player's PC stuttering for two seconds would throw everybody out. During
/// a co-op battle our own connection decides who is gone (<see cref="Battle.OnHostGone"/>, players
/// leaving the room); the game's timeouts and connection errors are only logged.
/// </summary>
internal static class NetErrors
{
    private static int _logged;

    internal static bool Ignore(string what)
    {
        if (!Battle.Active) return false;
        if (_logged++ < 20) CoopPlugin.L.LogInfo($"[coop] ignored the game's network watchdog: {what}");
        return true;
    }
}

[HarmonyPatch(typeof(Controller), nameof(Controller.setError))]
internal static class NetSetErrorPatch
{
    private static bool Prefix(Controller.ErrorType errorType, uint errorCategory, uint errorCode) =>
        !NetErrors.Ignore($"error {errorType} ({errorCategory}/{errorCode})");
}

[HarmonyPatch(typeof(Controller), nameof(Controller.receiveTimeout))]
internal static class NetTimeoutPatch
{
    private static bool Prefix(int occurredTimeout) => !NetErrors.Ignore($"timeout {occurredTimeout}");
}

[HarmonyPatch(typeof(P2.Game.Game), nameof(P2.Game.Game.executeErrorProcess))]
internal static class GameErrorProcessPatch
{
    private static bool Prefix(P2.Game.Packet.ErrorDecider.ErrorProcess errorProcess) => !NetErrors.Ignore($"error process {errorProcess}");
}
