using System;
using System.IO;
using HarmonyLib;

namespace PataCoop.Dev;

/// <summary>
/// Dev copies must never touch the player's real save. Whenever the game sets its
/// save root, redirect it to %USERPROFILE%\PataCoop\saves\i{instance}, seeding that
/// folder once from the real save so tests start from real progress.
/// </summary>
internal static class SaveSandbox
{
    private static readonly string SandboxRoot = Path.Combine(Environment.GetEnvironmentVariable("PATACOOP_WORK_WIN") ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "PataCoop"), "saves");
    public static string? RealRoot { get; private set; }
    public static string? Current { get; private set; }

    public static string Dir(int instance) => Path.Combine(SandboxRoot, "i" + instance);

    private static void RedirectRoot(ref string value)
    {
        if (string.IsNullOrEmpty(value) || value.StartsWith(SandboxRoot, StringComparison.OrdinalIgnoreCase)) return;
        RealRoot = value;
        // Port is assigned after patches are applied, so fall back to probing the
        // instance number here: the first free sandbox lock decides it.
        string dir = Dir(DevPlugin.Port > 0 ? DevPlugin.Instance : ClaimInstance());
        if (!Directory.Exists(dir) || Directory.GetFiles(dir, "*", SearchOption.AllDirectories).Length == 0)
            CopyTree(value, dir);
        DevPlugin.L.LogWarning($"[sandbox] save root {value} -> {dir}");
        Current = dir;
        value = dir;
    }

    /// <summary>
    /// Called every frame: the game writes the root field directly (no setter call),
    /// so redirect it as soon as the manager exists. Startup never writes saves, and
    /// this runs on the launcher screen, long before any load or save.
    /// </summary>
    internal static void Tick()
    {
        if (UnityEngine.Time.frameCount % 15 != 0) return;
        var m = UnityEngine.Object.FindObjectOfType<SaveDataManager>();
        if (m == null) return;
        string root = m.savePathRoot;
        if (string.IsNullOrEmpty(root) || root.StartsWith(SandboxRoot, StringComparison.OrdinalIgnoreCase)) return;
        string redirected = root;
        RedirectRoot(ref redirected);
        m.savePathRoot = redirected;
    }

    private static int ClaimInstance()
    {
        // Same order as the HTTP port probe: lowest index whose lock file we can hold.
        for (int i = 0; i < 8; i++)
        {
            Directory.CreateDirectory(Dir(i));
            try
            {
                _lock = new FileStream(Path.Combine(Dir(i), ".lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
                return i;
            }
            catch (IOException) { }
        }
        return 7;
    }

    private static FileStream? _lock;

    private static void CopyTree(string from, string to)
    {
        Directory.CreateDirectory(to);
        if (!Directory.Exists(from)) return;
        foreach (var file in Directory.GetFiles(from, "*", SearchOption.AllDirectories))
        {
            var target = Path.Combine(to, Path.GetRelativePath(from, file));
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            File.Copy(file, target, overwrite: false);
        }
    }
}
