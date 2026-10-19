using HarmonyLib;
using LiteNetLib;
using LiteNetLib.Utils;
using SoulstoneCoop.Net;
using UnityEngine;

namespace SoulstoneCoop.Lobby;

/// <summary>
/// Phase 6: both runs start together. The host starts a run through the normal menu; the postfix of the map
/// screen's Fight (per game mode) sends RunStart with what the game just wrote to Global, and the client does
/// what that Fight does: write the same Global fields and MainUIManager.ChangeScene(map). The Survivors
/// affixes come from each profile, so the client swaps in the host's list while its AffixGameplayData is built.
/// Whoever reaches StartMatch first is frozen until the partner's match starts too (PauseSync).
/// See docs/fase6-investigacao.md, sections 1 and 4.
/// </summary>
public static class RunSync
{
    private const float HoldTimeout = 45f;
    private const string MenuScene = "Menu-Survivors";

    private sealed class RunParams
    {
        public string Scene = "";
        public GameModes Mode;
        public DifficultyTierType MasteryTier;
        public int MasteryLevel;
        public bool HasAffixes;              // Survivors: override the client's affixes with List
        public List<string> Affixes = new();
    }

    private static RunParams? _clientRun;      // client: the host's run we're loading into (affix override)
    private static PlayerProfileMapProgressionData? _reenabledAffixes; // client: AffixesDisabled flipped for the load
    private static bool _holding;
    private static int _affixOverrides;

    // =====================================================================================
    // Gate on Fight (both sides)
    // =====================================================================================

    /// <summary>Prefix of the per-mode OnFightPressed. False = don't start (the button just does nothing).</summary>
    internal static bool AllowFight(GameModes mode)
    {
        var role = NetworkManager.Role;
        if (role == NetRole.Client)
        {
            CoopSession.Notice(CoopSession.Connected
                ? "Aguardando o host começar a partida"
                : "Conectando ao host... (desconecte pra jogar sozinho)");
            return false;
        }
        if (role == NetRole.Host && CoopSession.Connected)
        {
            if (mode == GameModes.Crusade)
            {
                CoopSession.Notice("Cruzada ainda não funciona em co-op");
                return false;
            }
            if (CoopSession.RemotePlace != LobbyPlace.Menu)
            {
                CoopSession.Notice($"{CoopSession.PartnerName} ainda não voltou ao menu");
                return false;
            }
        }
        return true;
    }

    /// <summary>Postfix of the per-mode OnFightPressed, host side: the game has just called ChangeScene and
    /// written the mode/difficulty to Global.</summary>
    internal static void AfterHostFight(string? scene)
    {
        if (NetworkManager.Role != NetRole.Host) return;
        // Phase 8: with AllowRejoin the run is remembered even without a partner yet, for one who joins mid-run.
        if (!CoopSession.Connected && !Plugin.AllowRejoin.Value) return;
        _hostRun = null;
        if (string.IsNullOrEmpty(scene))
        {
            Plugin.StaticLog.LogWarning("[SoulstoneCoop][run] Host started a run but the scene name is unknown - client not told.");
            return;
        }

        var p = new RunParams
        {
            Scene = scene!,
            Mode = Global.SelectedGameMode,
            MasteryTier = Global.MasteryDifficultyTier,
            MasteryLevel = Global.MasteryDynamicLevel,
        };
        if (p.Mode == GameModes.Survivor)
        {
            p.HasAffixes = true;
            var prog = TryGetProgression(p.Scene);
            if (prog != null && !prog.AffixesDisabled && prog.SelectedAffixes != null)
                foreach (var a in prog.SelectedAffixes) if (!string.IsNullOrEmpty(a)) p.Affixes.Add(a);
        }
        _hostRun = p;
        _rejoinSent = false;
        if (!CoopSession.Connected) return;

        SendRunStart(p);
        PauseSync.ResetForRun();
        CoopSession.BeginLoading();
        Plugin.StaticLog.LogInfo($"[SoulstoneCoop][run] Host: RunStart sent: scene={p.Scene} mode={p.Mode} mastery={p.MasteryTier}/{p.MasteryLevel} affixes=[{string.Join(",", p.Affixes)}]");
    }

    private static void SendRunStart(RunParams p)
    {
        var w = new NetDataWriter();
        w.Put((byte)PacketType.RunStart);
        w.Put(p.Scene);
        w.Put((byte)p.Mode);
        w.Put((int)p.MasteryTier);
        w.Put(p.MasteryLevel);
        w.Put(p.HasAffixes);
        w.Put((ushort)p.Affixes.Count);
        foreach (var a in p.Affixes) w.Put(a);
        NetworkManager.Send(w, DeliveryMethod.ReliableOrdered);
    }

    // =====================================================================================
    // Phase 8, host, [Coop] AllowRejoin (off by default): a partner who connects while we're in a run is sent
    // the same RunStart and loads into it, as a new character (level 1, no items); the enemies come with the
    // usual resync when its LobbyState says InRun. See docs/fase8-notas.md.
    // =====================================================================================

    private static RunParams? _hostRun;   // host: the run we're in (or loading)
    private static bool _rejoinSent;      // once per connection

    internal static void OnPeerConnected() => _rejoinSent = false;

    internal static void HostRejoinTick()
    {
        if (_rejoinSent || _hostRun == null || !Plugin.AllowRejoin.Value) return;
        if (NetworkManager.Role != NetRole.Host || !CoopSession.Connected || !CoopSession.LocalInRun) return;
        // Hello received (its LobbyState comes first on the same ordered channel) and the partner is in the menu.
        if (!CoopSession.Remote.Known || CoopSession.RemotePlace != LobbyPlace.Menu) return;
        if (Life.CoopLife.RunEnding || Life.CoopLife.VictoryReached() || Life.CoopLife.LocalOut) return;
        _rejoinSent = true;
        SendRunStart(_hostRun);
        CoopSession.Notice($"{CoopSession.PartnerName} chegou: entrando na sua partida", 6f);
        Plugin.StaticLog.LogInfo($"[SoulstoneCoop][run] Host: partner connected mid-run - RunStart resent (scene={_hostRun.Scene} mode={_hostRun.Mode}).");
    }

    // =====================================================================================
    // Client: follow the host
    // =====================================================================================

    public static void OnRunStart(NetDataReader r)
    {
        var p = new RunParams
        {
            Scene = r.GetString(),
            Mode = (GameModes)r.GetByte(),
            MasteryTier = (DifficultyTierType)r.GetInt(),
            MasteryLevel = r.GetInt(),
            HasAffixes = r.GetBool(),
        };
        int n = r.GetUShort();
        for (int i = 0; i < n; i++) p.Affixes.Add(r.GetString());
        if (NetworkManager.Role != NetRole.Client) return;

        Plugin.StaticLog.LogInfo($"[SoulstoneCoop][run] Client: RunStart received: scene={p.Scene} mode={p.Mode} mastery={p.MasteryTier}/{p.MasteryLevel} affixes=[{string.Join(",", p.Affixes)}]");
        string? refuse = null;
        try { refuse = TryStartClientRun(p); }
        catch (Exception ex)
        {
            Plugin.StaticLog.LogError($"[SoulstoneCoop][run] Client: starting the host's run: {ex}");
            refuse = "erro ao iniciar a partida";
            _clientRun = null;
        }
        if (refuse == null) return;

        CoopSession.Notice($"Não deu pra entrar na partida do host: {refuse}", 8f);
        var w = new NetDataWriter();
        w.Put((byte)PacketType.RunStartRefused);
        w.Put(refuse);
        NetworkManager.Send(w, DeliveryMethod.ReliableOrdered);
    }

    /// <returns>Null when the scene change started; otherwise why not (Portuguese, shown on both screens).</returns>
    private static string? TryStartClientRun(RunParams p)
    {
        if (CoopSession.LocalInRun) return "ainda estou numa partida";
        if (CoopSession.LocalLoading) return "já estou carregando uma partida";
        var ui = MainUIManager.Instance;
        if (ui == null) return "menu não encontrado";
        string active = UnityEngine.SceneManagement.SceneManager.GetActiveScene().name;
        if (active != MenuScene) return $"não estou no menu ({active})";
        var maps = Global.MapCollection;
        if (maps != null && maps.GetMapById(p.Scene) == null && p.Mode == GameModes.Survivor) return $"não tenho o mapa {p.Scene}";
        if (!Application.CanStreamedLevelBeLoaded(p.Scene)) return $"não tenho a cena {p.Scene} (DLC?)";

        Global.SelectedGameMode = p.Mode;
        Global.MasteryDifficultyTier = p.MasteryTier;
        Global.MasteryDynamicLevel = p.MasteryLevel;
        _clientRun = p;

        // The game builds AffixGameplayData from the profile's list, or from null when the map's affixes are
        // off: turn them on for the load so there is a list to swap the host's into (restored at StartMatch).
        _reenabledAffixes = null;
        if (p.HasAffixes)
        {
            var prog = TryGetProgression(p.Scene);
            if (prog != null && prog.AffixesDisabled && p.Affixes.Count > 0)
            {
                prog.AffixesDisabled = false;
                _reenabledAffixes = prog;
            }
            if (prog == null && p.Affixes.Count > 0)
                Plugin.StaticLog.LogWarning($"[SoulstoneCoop][run] Client has no progression for {p.Scene}: the host's affixes can't be applied.");
        }

        Enemies.ClientPuppets.ResetForNewRun();
        PauseSync.ResetForRun();
        CoopSession.BeginLoading();
        ui.ChangeScene(p.Scene, 0f, LoadingScreenManager.LoadingScreenMode.Full);
        Plugin.StaticLog.LogInfo($"[SoulstoneCoop][run] Client: ChangeScene({p.Scene}) mode={p.Mode}.");
        return null;
    }

    public static void OnRunStartRefused(NetDataReader r)
    {
        string reason = r.GetString();
        if (NetworkManager.Role != NetRole.Host) return;
        Plugin.StaticLog.LogWarning($"[SoulstoneCoop][run] Host: the client refused the run: {reason}");
        CoopSession.Notice($"{CoopSession.PartnerName} não entrou: {reason}", 8f);
    }

    internal static void OnLocalMatchStarted()
    {
        RestoreAffixFlag();
        _clientRun = null;
        _holding = false;
    }

    internal static void OnLocalRunLeft()
    {
        RestoreAffixFlag();
        _clientRun = null;
        _hostRun = null;
        _holding = false;
    }

    private static void RestoreAffixFlag()
    {
        if (_reenabledAffixes == null) return;
        try { _reenabledAffixes.AffixesDisabled = true; } catch { }
        _reenabledAffixes = null;
    }

    private static PlayerProfileMapProgressionData? TryGetProgression(string mapId)
    {
        try
        {
            var mp = Global.PlayerProfile?.MapProgression;
            if (mp == null) return null;
            return mp.TryGetMapProgressionById(mapId, out var data) ? data : null;
        }
        catch (Exception ex)
        {
            Plugin.StaticLog.LogWarning($"[SoulstoneCoop][run] Map progression of {mapId}: {ex.Message}");
            return null;
        }
    }

    // =====================================================================================
    // Client: the host's affixes (AffixGameplayData..ctor(List<string> selectedAffixes, int), 0x3FC7B0)
    // The ctor copies the list (new List + AddRange), so swapping the contents in for the call and putting
    // the profile's own back right after never touches what gets saved.
    // =====================================================================================

    private static List<string>? _affixBackup;
    private static Il2CppSystem.Collections.Generic.List<string>? _affixSwapped;

    public static void AffixCtorPrefix(Il2CppSystem.Collections.Generic.List<string> selectedAffixes)
    {
        try
        {
            _affixBackup = null;
            _affixSwapped = null;
            var p = _clientRun;
            if (p == null || !p.HasAffixes || !CoopSession.LocalLoading) return;
            if (selectedAffixes == null)
            {
                if (p.Affixes.Count > 0)
                    Plugin.StaticLog.LogWarning("[SoulstoneCoop][run] AffixGameplayData built from a null list: host affixes not applied.");
                return;
            }
            _affixBackup = new List<string>();
            foreach (var a in selectedAffixes) _affixBackup.Add(a);
            selectedAffixes.Clear();
            foreach (var a in p.Affixes) selectedAffixes.Add(a);
            _affixSwapped = selectedAffixes;
            _affixOverrides++;
        }
        catch (Exception ex)
        {
            Plugin.StaticLog.LogError($"[SoulstoneCoop][run] AffixCtorPrefix: {ex.Message}");
        }
    }

    public static void AffixCtorPostfix(AffixGameplayData __instance)
    {
        try
        {
            var list = _affixSwapped;
            if (list == null || _affixBackup == null) return;
            list.Clear();
            foreach (var a in _affixBackup) list.Add(a);
            Plugin.StaticLog.LogInfo(
                $"[SoulstoneCoop][run] Client: host affixes applied ({_affixOverrides}): run has {__instance.SelectedAffixes?.Count ?? -1} affixes, " +
                $"curse={__instance.CurseLevel}; profile list restored ({_affixBackup.Count}).");
        }
        catch (Exception ex)
        {
            Plugin.StaticLog.LogError($"[SoulstoneCoop][run] AffixCtorPostfix: {ex.Message}");
        }
        finally
        {
            _affixSwapped = null;
            _affixBackup = null;
        }
    }

    // =====================================================================================
    // Loading barrier (read by PauseSync every frame)
    // =====================================================================================

    /// <summary>Our match runs but the partner's is still loading: freeze ours (bounded).</summary>
    internal static bool HoldForPartner
    {
        get
        {
            bool hold = CoopSession.Connected && CoopSession.LocalInRun && CoopSession.RemotePlace == LobbyPlace.Loading
                        && Time.unscaledTime - CoopSession.LocalRunStartTime < HoldTimeout;
            if (hold != _holding)
            {
                _holding = hold;
                Plugin.StaticLog.LogInfo(hold
                    ? "[SoulstoneCoop][run] Partner still loading - holding our match."
                    : $"[SoulstoneCoop][run] Hold released after {Time.unscaledTime - CoopSession.LocalRunStartTime:F1}s (partner {CoopSession.RemotePlace}).");
            }
            return hold;
        }
    }
}

// ---------------------------------------------------------------------------------------------
// Fight buttons of the map screen, one per game mode (only caller: MapSelectionScreenPanel.OnUpdate).
// No arguments, bool result: safe to patch.
// ---------------------------------------------------------------------------------------------

[HarmonyPatch(typeof(MapSelectionSurvivorsScreenPanel), nameof(MapSelectionSurvivorsScreenPanel.OnFightPressed))]
public static class SurvivorsFightPatch
{
    [HarmonyPrefix]
    public static bool Prefix(ref bool __result)
    {
        try { if (!RunSync.AllowFight(GameModes.Survivor)) { __result = false; return false; } }
        catch (Exception ex) { Plugin.StaticLog.LogError($"[SoulstoneCoop] SurvivorsFightPatch.Prefix: {ex}"); }
        return true;
    }

    [HarmonyPostfix]
    public static void Postfix(MapSelectionSurvivorsScreenPanel __instance, bool __result)
    {
        if (!__result) return;
        try { RunSync.AfterHostFight(__instance.LevelSelectionModule?.SelectedMapItem?.MapId); }
        catch (Exception ex) { Plugin.StaticLog.LogError($"[SoulstoneCoop] SurvivorsFightPatch.Postfix: {ex}"); }
    }
}

[HarmonyPatch(typeof(MapSelectionMasteryScreenPanel), nameof(MapSelectionMasteryScreenPanel.OnFightPressed))]
public static class MasteryFightPatch
{
    [HarmonyPrefix]
    public static bool Prefix(ref bool __result)
    {
        try { if (!RunSync.AllowFight(GameModes.Mastery)) { __result = false; return false; } }
        catch (Exception ex) { Plugin.StaticLog.LogError($"[SoulstoneCoop] MasteryFightPatch.Prefix: {ex}"); }
        return true;
    }

    [HarmonyPostfix]
    public static void Postfix(MapSelectionMasteryScreenPanel __instance, bool __result)
    {
        if (!__result) return;
        try { RunSync.AfterHostFight(__instance._masteryConfig?.MasteryMapId); }
        catch (Exception ex) { Plugin.StaticLog.LogError($"[SoulstoneCoop] MasteryFightPatch.Postfix: {ex}"); }
    }
}

[HarmonyPatch(typeof(MapSelectionCrusadeScreenPanel), nameof(MapSelectionCrusadeScreenPanel.OnFightPressed))]
public static class CrusadeFightPatch
{
    [HarmonyPrefix]
    public static bool Prefix(ref bool __result)
    {
        try { if (!RunSync.AllowFight(GameModes.Crusade)) { __result = false; return false; } }
        catch (Exception ex) { Plugin.StaticLog.LogError($"[SoulstoneCoop] CrusadeFightPatch.Prefix: {ex}"); }
        return true;
    }
}

/// <summary>"Restart" on the end screen reloads the map locally (LoadNextScene): in co-op both go through the menu.</summary>
[HarmonyPatch(typeof(EndGamePanelManager), nameof(EndGamePanelManager.OnRestartPressed))]
public static class CoopRestartBlockPatch
{
    [HarmonyPrefix]
    public static bool Prefix()
    {
        try
        {
            if (!CoopSession.Connected) return true;
            CoopSession.Notice("Em co-op, volte ao menu pra jogar de novo");
            return false;
        }
        catch (Exception ex)
        {
            Plugin.StaticLog.LogError($"[SoulstoneCoop] CoopRestartBlockPatch: {ex}");
            return true;
        }
    }
}
