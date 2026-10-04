namespace PataCoop;

/// <summary>Player-facing text in the chosen language (Traditional Chinese by default). Logs stay English.</summary>
internal static class Text
{
    internal static bool Chinese => !string.Equals(CoopPlugin.Language?.Value, "en", System.StringComparison.OrdinalIgnoreCase);

    internal static string T(string en, string zh) => Chinese ? zh : en;

    /// <summary>A mission outcome in words.</summary>
    internal static string Outcome(int type) => (P2.Game.Game.GameEndType)type switch
    {
        P2.Game.Game.GameEndType.GameEndType_Clear => T("cleared", "過關"),
        P2.Game.Game.GameEndType.GameEndType_Failed => T("failed", "失敗"),
        var other => other.ToString().Replace("GameEndType_", ""),
    };
}
