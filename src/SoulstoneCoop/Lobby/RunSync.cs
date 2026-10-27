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
    private static bool _holding;

    // =====================================================================================
    // Gate on Fight (both sides)
    // =====================================================================================

    /// <summary>Prefix of the per-mode OnFightPressed. False = don't start (the button just does nothing).</summary>
    internal static bool AllowFight(GameModes mode)
    {
        var role = NetworkManager.Role;
        if (role == NetRole.Client)
        {
            CoopSession.Notice(Ui.Loc.Msg(CoopSession.Connected ? "st.wait_host" : "nt.connecting_host"));
            return false;
        }
        if (role == NetRole.Host && CoopSession.Connected)
        {
            if (mode == GameModes.Crusade)
            {
                CoopSession.Notice(Ui.Loc.Msg("nt.crusade"));
                return false;
            }
            var away = CoopSession.NamesWhere(r => r.Place != LobbyPlace.Menu);
            if (away.Length > 0)
            {
                CoopSession.Notice(Ui.Loc.Msg("nt.not_back", ("names", away)));
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

        var p = BuildHostParams(scene!);
        _hostRun = p;
        _rejoinSent.Clear();
        if (!CoopSession.Connected) return;

        SendRunStart(p);
        PauseSync.ResetForRun();
        CoopSession.BeginLoading();
        Plugin.StaticLog.LogInfo($"[SoulstoneCoop][run] Host: RunStart sent: {Describe(p)}");
    }

    private static void SendRunStart(RunParams p)
    {
        var w = new NetDataWriter();
        w.Put((byte)PacketType.RunStart);
        WriteParams(w, p);
        NetworkManager.Send(w, DeliveryMethod.ReliableOrdered);
    }

    // =====================================================================================
    // Phase 8, host, [Coop] AllowRejoin (off by default): a partner who connects while we're in a run is sent
    // the same RunStart and loads into it, as a new character (level 1, no items); the enemies come with the
    // usual resync when its LobbyState says InRun. See docs/fase8-notas.md.
    // =====================================================================================

    private static RunParams? _hostRun;   // host: the run we're in (or loading)
    private static readonly HashSet<byte> _rejoinSent = new(); // phase 9: once per connection, per player

    internal static void OnPeerConnected(byte slot) => _rejoinSent.Remove(slot);

    /// <summary>Phase 9: that player is in our run already (it came with the RunStart): if it goes back to the menu
    /// (surrendered alone), AllowRejoin must not pull it straight back in.</summary>
    internal static void NoteInRun(byte slot) { if (NetworkManager.Role == NetRole.Host && CoopSession.LocalInRun) _rejoinSent.Add(slot); }

    /// <summary>Host took an Endless/BossRush portal (PortalSync): a partner joining later loads the current stage's
    /// map, with the same mode and the run's affixes.</summary>
    internal static void OnHostStageChanged(string scene)
    {
        if (_hostRun == null || NetworkManager.Role != NetRole.Host) return;
        var p = new RunParams
        {
            Scene = scene,
            Mode = _hostRun.Mode,
            MasteryTier = _hostRun.MasteryTier,
            MasteryLevel = _hostRun.MasteryLevel,
            HasAffixes = _hostRun.HasAffixes,
        };
        p.Affixes.AddRange(_hostRun.Affixes);
        _hostRun = p;
    }

    internal static void HostRejoinTick()
    {
        if (_hostRun == null || !Plugin.AllowRejoin.Value) return;
        if (NetworkManager.Role != NetRole.Host || !CoopSession.Connected || !CoopSession.LocalInRun) return;
        if (Life.CoopLife.RunEnding || Life.CoopLife.VictoryReached() || Life.CoopLife.LocalOut) return;
        foreach (var r in CoopSession.Remotes)
        {
            // Hello received (its LobbyState comes first on the same ordered channel) and that player is in the menu.
            if (_rejoinSent.Contains(r.Slot) || !r.Info.Known || r.Place != LobbyPlace.Menu) continue;
            _rejoinSent.Add(r.Slot);
            var w = new NetDataWriter();
            w.Put((byte)PacketType.RunStart);
            WriteParams(w, _hostRun);
            NetworkManager.SendTo(r.Slot, w, DeliveryMethod.ReliableOrdered); // only the newcomer: the others are in the run
            CoopSession.Notice(Ui.Loc.Msg("nt.arrived", ("name", r.Name)), 6f);
            Plugin.StaticLog.LogInfo($"[SoulstoneCoop][run] Host: slot {r.Slot} connected mid-run - RunStart resent (scene={_hostRun.Scene} mode={_hostRun.Mode}).");
        }
    }

    /// <summary>The run the host is starting: Global (written by the game's Fight/Restart) and the host's affixes.</summary>
    private static RunParams BuildHostParams(string scene)
    {
        var p = new RunParams
        {
            Scene = scene,
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
        return p;
    }

    private static void WriteParams(NetDataWriter w, RunParams p)
    {
        w.Put(p.Scene);
        w.Put((byte)p.Mode);
        w.Put((int)p.MasteryTier);
        w.Put(p.MasteryLevel);
        w.Put(p.HasAffixes);
        w.Put((ushort)p.Affixes.Count);
        foreach (var a in p.Affixes) w.Put(a);
    }

    private static RunParams ReadParams(NetDataReader r)
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
        return p;
    }

    private static string Describe(RunParams p) =>
        $"scene={p.Scene} mode={p.Mode} mastery={p.MasteryTier}/{p.MasteryLevel} affixes=[{string.Join(",", p.Affixes)}]";

    // =====================================================================================
    // Client: follow the host
    // =====================================================================================

    public static void OnRunStart(NetDataReader r)
    {
        var p = ReadParams(r);
        if (NetworkManager.Role != NetRole.Client) return;

        Plugin.StaticLog.LogInfo($"[SoulstoneCoop][run] Client: RunStart received: {Describe(p)}");
        string? refuse = null;
        try { refuse = TryStartClientRun(p); }
        catch (Exception ex)
        {
            Plugin.StaticLog.LogError($"[SoulstoneCoop][run] Client: starting the host's run: {ex}");
            refuse = Ui.Loc.Msg("why.start_error");
            _clientRun = null;
        }
        if (refuse != null) Refuse(Ui.Loc.Msg("nt.cant_join_run", ("reason", refuse)), refuse);
    }

    /// <summary>Client: tell the host why we didn't follow (shown on both screens). Both are Ui.Loc.Msg: the reason
    /// travels as a key + parameters and the host shows it in its own language.</summary>
    internal static void Refuse(string localNotice, string reasonForHost)
    {
        CoopSession.Notice(localNotice, 8f);
        var w = new NetDataWriter();
        w.Put((byte)PacketType.RunStartRefused);
        w.Put(reasonForHost);
        NetworkManager.Send(w, DeliveryMethod.ReliableOrdered);
    }

    /// <returns>Null when the scene change started; otherwise why not (a Ui.Loc.Msg, shown on both screens).</returns>
    private static string? TryStartClientRun(RunParams p)
    {
        if (CoopSession.LocalInRun) return Ui.Loc.Msg("why.in_run");
        if (CoopSession.LocalLoading) return Ui.Loc.Msg("why.loading");
        var ui = MainUIManager.Instance;
        if (ui == null) return Ui.Loc.Msg("why.no_menu");
        string active = UnityEngine.SceneManagement.SceneManager.GetActiveScene().name;
        if (active != MenuScene) return Ui.Loc.Msg("why.not_in_menu", ("scene", active));
        string? missing = MissingScene(p);
        if (missing != null) return missing;

        PrepareClientRun(p);
        Enemies.ClientPuppets.ResetForNewRun();
        PauseSync.ResetForRun();
        CoopSession.BeginLoading();
        ui.ChangeScene(p.Scene, 0f, LoadingScreenManager.LoadingScreenMode.Full);
        Plugin.StaticLog.LogInfo($"[SoulstoneCoop][run] Client: ChangeScene({p.Scene}) mode={p.Mode}.");
        return null;
    }

    private static string? MissingScene(RunParams p)
    {
        var maps = Global.MapCollection;
        if (maps != null && maps.GetMapById(p.Scene) == null && p.Mode == GameModes.Survivor) return Ui.Loc.Msg("why.no_map", ("map", p.Scene));
        if (!Application.CanStreamedLevelBeLoaded(p.Scene)) return Ui.Loc.Msg("why.no_scene", ("map", p.Scene));
        return null;
    }

    /// <summary>Client: same Global as the host's run, and the host's affixes for when AffixGameplayData is built.</summary>
    private static void PrepareClientRun(RunParams p)
    {
        Global.SelectedGameMode = p.Mode;
        Global.MasteryDifficultyTier = p.MasteryTier;
        Global.MasteryDynamicLevel = p.MasteryLevel;
        _clientRun = p;

        // Issue #3: the game builds AffixGameplayData from the profile's list (null when the map's affixes are off)
        // in SurvivorsGameManager.Start. The prefix on its constructor never ran (Il2CppInterop can't patch that
        // ctor: "Failed to init IL2CPP patch backend"), so the profile itself carries the host's list for the load:
        // contents and AffixesDisabled are swapped now and put back at StartMatch (or when the run is left).
        RestoreAffixFlag();
        if (p.HasAffixes)
        {
            var prog = TryGetProgression(p.Scene);
            if (prog == null)
            {
                if (p.Affixes.Count > 0)
                    Plugin.StaticLog.LogWarning($"[SoulstoneCoop][run] Client has no progression for {p.Scene}: the host's affixes can't be applied.");
                return;
            }
            var list = prog.SelectedAffixes;
            if (list == null)
            {
                list = new Il2CppSystem.Collections.Generic.List<string>();
                prog.SelectedAffixes = list;
            }
            _savedAffixes = new List<string>();
            foreach (var a in list) _savedAffixes.Add(a);
            _savedAffixesDisabled = prog.AffixesDisabled;
            _swappedProgression = prog;
            list.Clear();
            foreach (var a in p.Affixes) list.Add(a);
            prog.AffixesDisabled = p.Affixes.Count == 0;
            Plugin.StaticLog.LogInfo($"[SoulstoneCoop][run] Client: profile affixes of {p.Scene} swapped for the host's ({p.Affixes.Count}, own {_savedAffixes.Count}, disabled was {_savedAffixesDisabled}).");
        }
    }

    private static PlayerProfileMapProgressionData? _swappedProgression;
    private static List<string>? _savedAffixes;
    private static bool _savedAffixesDisabled;

    /// <summary>Both sides at StartMatch: what the run was built with, to compare the host's log with the client's
    /// (the boss counter follows AdditionalBossQuantity / AdditionalSimultaneousBossesAmount).</summary>
    private static void LogRunAffixes()
    {
        try
        {
            var data = GameManagerUtil.AffixGameplayData;
            if (data == null) { Plugin.StaticLog.LogInfo("[SoulstoneCoop][run] Run affixes: none."); return; }
            int n = data.SelectedAffixes?.Count ?? 0;
            string summary = $"affixes={n} extraBosses={data.AdditionalBossQuantity} simultaneous=+{data.AdditionalSimultaneousBossesAmount} ethereal={data.EnableEtherealBosses}";
            Plugin.StaticLog.LogInfo($"[SoulstoneCoop][run] Run affixes ({NetworkManager.Role}): {summary}");
            LastRunAffixes = summary;
            var p = _clientRun;
            if (p != null && p.HasAffixes && data.SelectedAffixes != null)
            {
                var have = new HashSet<string>();
                foreach (var a in data.SelectedAffixes) have.Add(a);
                var missing = p.Affixes.Where(a => !have.Contains(a)).ToList();
                if (missing.Count > 0)
                    Plugin.StaticLog.LogWarning($"[SoulstoneCoop][run] Client run is missing {missing.Count} of the host's affixes: {string.Join(",", missing)}");
            }
        }
        catch (Exception ex)
        {
            Plugin.StaticLog.LogWarning($"[SoulstoneCoop][run] Run affixes: {ex.Message}");
        }
    }

    /// <summary>Test bridge: the last run's affix summary.</summary>
    internal static string LastRunAffixes = "-";

    /// <summary>Test bridge, "cmd affixes MAP on|off": turn this instance's own affixes of a map on or off in memory,
    /// so two instances sharing one save can start with different selections.</summary>
    internal static string TestSetOwnAffixes(string[] args)
    {
        if (args.Length < 2) return "usage: affixes MAP on|off";
        var prog = TryGetProgression(args[0]);
        if (prog == null) return "no progression for " + args[0];
        prog.AffixesDisabled = args[1] == "off";
        return $"{args[0]} affixes disabled={prog.AffixesDisabled} ({prog.SelectedAffixes?.Count ?? 0} selected)";
    }

    // =====================================================================================
    // Restart on the end screen: the host decides, the client follows (docs/portais-investigacao.md, 4)
    // =====================================================================================

    /// <summary>Prefix of EndGamePanelManager.OnRestartPressed. False = the button does nothing.</summary>
    internal static bool AllowRestart(EndGamePanelManager panel)
    {
        if (!CoopSession.Connected) return true;
        if (NetworkManager.Role != NetRole.Host)
        {
            CoopSession.Notice(Ui.Loc.Msg("nt.restart_host_only"));
            return false;
        }
        if (panel.RequestedGoToMenu) return true; // the game ignores the click anyway
        if (Global.SelectedGameMode == GameModes.Crusade)
        {
            CoopSession.Notice(Ui.Loc.Msg("nt.crusade_menu"));
            return false;
        }
        if (CoopSession.AnyRemoteChangingScene)
        {
            CoopSession.Notice(Ui.Loc.Msg("nt.still_loading", ("names", CoopSession.NamesWhere(r => r.ChangingScene))));
            return false;
        }
        return true;
    }

    /// <summary>Postfix of OnRestartPressed, host: the game just scheduled LoadNextScene(MatchStats.MapId, 0, true).
    /// A partner already in the menu gets the usual RunStart; one still on its end screen restarts from there.</summary>
    internal static void AfterHostRestart(EndGamePanelManager panel, bool wasRequested)
    {
        if (NetworkManager.Role != NetRole.Host) return;
        if (!CoopSession.Connected && !Plugin.AllowRejoin.Value) return;
        if (wasRequested || !panel.RequestedGoToMenu) return; // the game didn't restart
        string? scene = null;
        try { scene = GameManagerUtil.MatchStats?.MapId; } catch { }
        if (string.IsNullOrEmpty(scene))
        {
            Plugin.StaticLog.LogWarning("[SoulstoneCoop][run] Host restarted but the map is unknown - client not told.");
            return;
        }

        // Alone (phase 8 AllowRejoin keeps the run for a late partner) or the partner is in the menu: usual RunStart.
        if (!CoopSession.Connected || CoopSession.NamesWhere(r => r.Place != LobbyPlace.Menu).Length == 0)
        {
            AfterHostFight(scene);
            if (CoopSession.Connected) CoopSession.BeginStageTransition("restart");
            return;
        }

        var p = BuildHostParams(scene!);
        _hostRun = p;
        _rejoinSent.Clear();
        var w = new NetDataWriter();
        w.Put((byte)PacketType.PortalTransition);
        w.Put((byte)PortalKind.Restart);
        w.Put(p.Scene);
        WriteParams(w, p);
        NetworkManager.Send(w, DeliveryMethod.ReliableOrdered);
        PauseSync.ResetForRun();
        CoopSession.BeginStageTransition("restart");
        Plugin.StaticLog.LogInfo($"[SoulstoneCoop][run] Host: restart sent to the partner's end screen: {Describe(p)}");
    }

    /// <summary>Client: PortalTransition(Restart); the kind byte and the map are already read.</summary>
    internal static void OnHostRestart(NetDataReader r)
    {
        var p = ReadParams(r);
        if (NetworkManager.Role != NetRole.Client) return;
        Plugin.StaticLog.LogInfo($"[SoulstoneCoop][run] Client: host restarted: {Describe(p)} (local {CoopSession.LocalPlace})");
        string? refuse;
        try { refuse = CoopSession.LocalInRun ? TryRestartClientRun(p) : TryStartClientRun(p); }
        catch (Exception ex)
        {
            Plugin.StaticLog.LogError($"[SoulstoneCoop][run] Client: following the host's restart: {ex}");
            refuse = Ui.Loc.Msg("why.restart_error");
            _clientRun = null;
        }
        if (refuse != null) Refuse(Ui.Loc.Msg("nt.cant_restart", ("reason", refuse)), refuse);
    }

    /// <summary>Client still in the old run (end screen): what its own Restart button does, with the host's run.</summary>
    private static string? TryRestartClientRun(RunParams p)
    {
        if (CoopSession.StageTransition) return Ui.Loc.Msg("why.changing_scene");
        string? missing = MissingScene(p);
        if (missing != null) return missing;

        PrepareClientRun(p);
        Enemies.ClientPuppets.ResetForNewRun();
        PauseSync.ResetForRun();
        CoopSession.BeginStageTransition("host restarted");
        // EndGamePanelManager.OnRestartPressed: LoadNextScene(MatchStats.MapId, 0, clearLoad: true).
        GameManagerUtil.LoadNextScene(p.Scene, 0f, true);
        Plugin.StaticLog.LogInfo($"[SoulstoneCoop][run] Client: LoadNextScene({p.Scene}) from the end screen.");
        return null;
    }

    public static void OnRunStartRefused(NetDataReader r)
    {
        string reason = r.GetString();
        if (NetworkManager.Role != NetRole.Host) return;
        Plugin.StaticLog.LogWarning($"[SoulstoneCoop][run] Host: slot {PacketRouter.From} refused the run: {Ui.Loc.Plain(reason)}");
        // The reason is a Ui.Loc.Msg from the client: shown in the host's language.
        CoopSession.Notice(Ui.Loc.Msg("nt.refused_run", ("name", CoopSession.NameOf(PacketRouter.From)), ("reason", reason)), 8f);
    }

    internal static void OnLocalMatchStarted()
    {
        if (CoopSession.Connected) LogRunAffixes();
        RestoreAffixFlag();
        _clientRun = null;
        _holding = false;
    }

    internal static void OnLocalRunLeft()
    {
        // Restart from the end screen: the next scene is already on its way and still needs _clientRun
        // (the host's affixes); OnLocalMatchStarted clears it.
        if (CoopSession.StageTransition) { _holding = false; return; }
        RestoreAffixFlag();
        _clientRun = null;
        _hostRun = null;
        _holding = false;
    }

    /// <summary>Puts the client's own affix selection back into its profile (see PrepareClientRun).</summary>
    private static void RestoreAffixFlag()
    {
        var prog = _swappedProgression;
        if (prog == null) return;
        _swappedProgression = null;
        try
        {
            var list = prog.SelectedAffixes;
            if (list != null && _savedAffixes != null)
            {
                list.Clear();
                foreach (var a in _savedAffixes) list.Add(a);
            }
            prog.AffixesDisabled = _savedAffixesDisabled;
            Plugin.StaticLog.LogInfo($"[SoulstoneCoop][run] Client: own affixes restored ({_savedAffixes?.Count ?? 0}).");
        }
        catch (Exception ex)
        {
            Plugin.StaticLog.LogWarning($"[SoulstoneCoop][run] Restoring the client's affixes: {ex.Message}");
        }
        _savedAffixes = null;
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
    // Loading barrier (read by PauseSync every frame)
    // =====================================================================================

    /// <summary>Our match runs but the partner's is still loading: freeze ours (bounded).</summary>
    internal static bool HoldForPartner
    {
        get
        {
            // Never in our own old scene during a portal/Restart: LoadNextStageAfterDelay waits on scaled time.
            bool hold = CoopSession.LocalInRun && !CoopSession.StageTransition && CoopSession.AnyRemoteChangingScene
                        && Time.unscaledTime - CoopSession.LocalRunStartTime < HoldTimeout;
            if (hold != _holding)
            {
                _holding = hold;
                Plugin.StaticLog.LogInfo(hold
                    ? $"[SoulstoneCoop][run] Others still loading ({CoopSession.DescribeOthers()}) - holding our match."
                    : $"[SoulstoneCoop][run] Hold released after {Time.unscaledTime - CoopSession.LocalRunStartTime:F1}s (others {CoopSession.DescribeOthers()}).");
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

/// <summary>"Restart" on the end screen reloads the map locally (LoadNextScene). In co-op only the host restarts, and
/// the client follows (from its end screen or from the menu). No arguments: safe to patch.</summary>
[HarmonyPatch(typeof(EndGamePanelManager), nameof(EndGamePanelManager.OnRestartPressed))]
public static class CoopRestartPatch
{
    [HarmonyPrefix]
    public static bool Prefix(EndGamePanelManager __instance, out bool __state)
    {
        __state = true;
        try
        {
            __state = __instance.RequestedGoToMenu;
            return RunSync.AllowRestart(__instance);
        }
        catch (Exception ex)
        {
            Plugin.StaticLog.LogError($"[SoulstoneCoop] CoopRestartPatch.Prefix: {ex}");
            return true;
        }
    }

    [HarmonyPostfix]
    public static void Postfix(EndGamePanelManager __instance, bool __state)
    {
        try { RunSync.AfterHostRestart(__instance, __state); }
        catch (Exception ex) { Plugin.StaticLog.LogError($"[SoulstoneCoop] CoopRestartPatch.Postfix: {ex}"); }
    }
}
