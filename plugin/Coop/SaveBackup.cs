using System;
using System.IO;
using System.Linq;

namespace PataCoop.Coop;

/// <summary>
/// Before every co-op battle, copy the save folder aside (BepInEx/PataCoop/save-backups/...),
/// keeping the newest <see cref="Keep"/> copies. Co-op writes the result of each battle into every
/// player's own save, so a bad battle should never cost anyone their progress.
/// </summary>
internal static class SaveBackup
{
    private const int Keep = 20;
    private static string Folder => Path.Combine(BepInEx.Paths.BepInExRootPath, "PataCoop", "save-backups");

    internal static void BeforeBattle(string missionName)
    {
        try
        {
            var manager = UnityEngine.Object.FindObjectOfType<SaveDataManager>();
            string root = manager?.savePathRoot ?? "";
            if (root.Length == 0 || !Directory.Exists(root)) return;

            string target = Path.Combine(Folder, DateTime.Now.ToString("yyyyMMdd-HHmmss"));
            foreach (var file in Directory.GetFiles(root, "*", SearchOption.AllDirectories))
            {
                string to = Path.Combine(target, Path.GetRelativePath(root, file));
                Directory.CreateDirectory(Path.GetDirectoryName(to)!);
                File.Copy(file, to, overwrite: true);
            }
            File.WriteAllText(Path.Combine(target, "PATACOOP-BACKUP.txt"),
                $"Save copied before the co-op battle \"{missionName}\".\r\nFrom: {root}\r\nTo restore: quit the game and copy these files back over that folder.\r\n");

            foreach (var old in new DirectoryInfo(Folder).GetDirectories().OrderByDescending(d => d.Name).Skip(Keep))
                old.Delete(recursive: true);
            CoopPlugin.L.LogInfo($"[coop] save backed up to {target}");
        }
        catch (Exception e)
        {
            CoopPlugin.L.LogWarning("[coop] save backup failed (the battle goes on): " + e.Message);
        }
    }
}
