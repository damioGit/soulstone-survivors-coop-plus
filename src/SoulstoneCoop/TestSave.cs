using HarmonyLib;

namespace SoulstoneCoop;

/// <summary>
/// Test only (--coop-test NAME): the game's save files go to %USERPROFILE%\soulstone-uitest\saves\NAME instead of
/// the player's own LocalLow folder, so test runs (two instances at once) never touch a real save. An empty folder
/// starts as a copy of soulstone-uitest\save-template (tools/uitest/duo.sh fills it from the save backup).
/// --coop-test-save real keeps the real folder (the save merge tools need it).
/// </summary>
[HarmonyPatch(typeof(StandalonePlayerDataProvider), nameof(StandalonePlayerDataProvider.Init))]
public static class TestSave
{
    internal static bool UseRealSave;

    /// <summary>The folder this instance saves to (null: not redirected).</summary>
    internal static string? Folder;

    private static string? TestFolder()
    {
        if (Plugin.TestArg == null || UseRealSave) return null;
        string root = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "soulstone-uitest");
        string dir = Path.Combine(root, "saves", Plugin.TestArg);
        if (Folder == null)
        {
            Directory.CreateDirectory(dir);
            string template = Path.Combine(root, "save-template");
            if (Directory.GetFiles(dir, "playerProfile-*").Length == 0 && Directory.Exists(template))
            {
                foreach (var f in Directory.GetFiles(template)) File.Copy(f, Path.Combine(dir, Path.GetFileName(f)), true);
                Plugin.StaticLog.LogInfo($"[SoulstoneCoop][test] Test save {dir} seeded from {template}.");
            }
            Folder = dir;
        }
        return dir;
    }

    [HarmonyPrefix]
    public static void Prefix(StandalonePlayerDataProvider __instance) => Redirect(__instance);

    [HarmonyPostfix]
    public static void Postfix(StandalonePlayerDataProvider __instance) => Redirect(__instance);

    private static void Redirect(StandalonePlayerDataProvider p)
    {
        try
        {
            string? dir = TestFolder();
            if (dir == null || p.PersistentDataPath == dir) return;
            Plugin.StaticLog.LogWarning($"[SoulstoneCoop][test] Save folder {p.PersistentDataPath ?? "-"} -> {dir}");
            p.PersistentDataPath = dir;
        }
        catch (Exception ex)
        {
            Plugin.StaticLog.LogError($"[SoulstoneCoop][test] Save redirect failed: {ex}");
        }
    }

    /// <summary>"cmd savepath": where the live provider reads and writes.</summary>
    internal static string Info(string[] args)
    {
        var p = PlayerDataUtil.Default?.Provider;
        var sp = p != null ? p.TryCast<StandalonePlayerDataProvider>() : null;
        return $"provider={(sp != null ? sp.PersistentDataPath : p != null ? "other provider" : "none")} test={Folder ?? "-"} real={UnityEngine.Application.persistentDataPath}";
    }
}
