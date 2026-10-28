using System.Text;
using SoulstoneCoop.Lobby;
using SoulstoneCoop.Net;
using UnityEngine;
using UnityEngine.EventSystems;

namespace SoulstoneCoop;

/// <summary>
/// Test only (--coop-test NAME): lets an agent drive several instances of the game on one PC. Each instance reads
/// one command at a time from %USERPROFILE%\soulstone-uitest\NAME\request.txt and answers in reply.txt.
/// Commands: shot FILE | state | play | fight | quit | cmd ... (feature hooks in <see cref="Commands"/>).
/// Everything runs on the main thread from CoopBehaviour.Update, after the mod's own ticks.
/// </summary>
public static class TestBridge
{
    private static string? _dir;
    private static float _poll;
    private static int _fightStep;
    private static GameModes _fightMode = GameModes.Survivor;
    private static float _fightTimer;

    /// <summary>Extra commands registered by feature modules: name -> handler(args) returning the reply.</summary>
    internal static readonly Dictionary<string, Func<string[], string>> Commands = new();

    public static bool Enabled => Plugin.TestArg != null;

    private static bool _god;
    private static float _godTimer;

    /// <summary>Test "god": invulnerable and topped up, every scene (some hits ignore InvulnerabilityTime).</summary>
    private static void TickGod(float dt)
    {
        if (!_god) return;
        _godTimer -= dt;
        if (_godTimer > 0f) return;
        _godTimer = 0.3f;
        var local = GameState.LocalPlayerEntity;
        var h = local?._health;
        if (local == null || h == null || h.HasBeenKilled) return;
        h.InvulnerabilityTime = 100000f;
        float max = local._stats != null ? local._stats.GetMaxHealth() : h.CurrentHealth;
        if (h.CurrentHealth < max * 0.6f) UnitUtil.ChangeEntityHealth(local, local, max - h.CurrentHealth, false, out _, out _, out _);
    }

    public static void Tick(float dt)
    {
        if (Plugin.TestArg == null) return;
        try { TickGod(dt); } catch { _godTimer = 5f; }
        try
        {
            if (_dir == null)
            {
                _dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "soulstone-uitest", Plugin.TestArg);
                Directory.CreateDirectory(_dir);
                foreach (var f in Directory.GetFiles(_dir)) File.Delete(f);
                File.WriteAllText(Path.Combine(_dir, "ready.txt"), "starting");
                Commands["affixes"] = RunSync.TestSetOwnAffixes;
                Commands["healorb"] = Life.HealOrbs.TestFire;
                Commands["hurt"] = Hurt;
                Commands["chest"] = Loot.ChestSync.TestSpawn;
                Commands["chests"] = Loot.ChestSync.TestInfo;
                Commands["tp"] = Teleport;
                Commands["dashtest"] = Enemies.ClientIncomingDamage.TestDashDodge;
                Commands["minimap"] = GhostOverhead.TestInfo;
                Commands["hint"] = PauseSync.TestHint;
                Commands["down"] = Life.CoopLife.TestDown;
                Commands["animparams"] = AnimParams;
                Commands["ghostdown"] = Life.GhostDowned.TestInfo;
                Commands["walk"] = Walk;
                Commands["aims"] = Skills.CastSync.TestAims;
                Commands["godcast"] = GodCast;
                Commands["skills"] = ListSkills;
                Commands["auras"] = Skills.CastSync.TestAuras;
                Commands["boss"] = _ => { Enemies.SpecialsDebug.ForceBoss(); return "boss objective completed"; };
                Commands["ethereal"] = _ => { var d = GameManagerUtil.AffixGameplayData; if (d == null) return "no affix data"; d.EnableEtherealBosses = true; return "ethereal bosses on"; };
                Commands["bosses"] = Bosses;
                Commands["autopick"] = a => { _autoPick = a.Length == 0 || a[0] != "off"; return "autopick " + _autoPick; };
                Commands["killbosses"] = args =>
                {
                    var list = GameManagerUtil.BossEntities;
                    if (list == null) return "no boss list";
                    var all = new List<Entity>();
                    foreach (var b in list) if (b != null && b._health != null && !b._health.HasBeenKilled) all.Add(b);
                    foreach (var b in all)
                    {
                        b._health.InvulnerabilityTime = 0f;
                        UnitUtil.ChangeEntityHealth(GameState.LocalPlayerEntity!, b, -(b._health.CurrentHealth + 1e9f), true, out _, out _, out _);
                    }
                    return "killed " + all.Count;
                };
                Commands["bossvis"] = BossVis;
                Commands["statuesync"] = Enemies.StatueSync.TestInfo;
                Commands["tasks"] = Tasks;
                Commands["inv"] = Inventory;
                Commands["hpbonus"] = Enemies.EnemyHealthBonus.TestInfo;
                Commands["mastery"] = Mastery;
                Commands["masterysync"] = Lobby.MasterySync.TestInfo;
                Commands["hit"] = a =>
                {
                    var local = GameState.LocalPlayerEntity;
                    if (local == null) return "no player";
                    float dmg = a.Length > 0 && float.TryParse(a[0], out var v) ? v : 1000f;
                    Entity? best = null; float bd = float.MaxValue;
                    foreach (var e in UnityEngine.Object.FindObjectsOfType<Entity>())
                    {
                        if (e == null || e._health == null || e._team == null || (e._team.Team & Team.Monsters) == 0 || e._health.HasBeenKilled) continue;
                        if (e._health.CurrentHealth < dmg * 2f || e._health.CurrentHealth > 1e6f) continue;
                        float d = (e.transform.position - local.transform.position).sqrMagnitude;
                        if (d < bd) { bd = d; best = e; }
                    }
                    if (best == null) return "no enemy with enough health";
                    float before = best._health.CurrentHealth;
                    UnitUtil.ChangeEntityHealth(local, best, -dmg, false, out float eff, out _, out _);
                    return $"{best.gameObject.name}: hit {dmg:0}, health {before:0} -> {best._health.CurrentHealth:0} (lost {before - best._health.CurrentHealth:0})";
                };
                Commands["itemdiag"] = Loot.ItemDiag.Info;
                Commands["ore"] = a =>
                {
                    var ci = System.Globalization.CultureInfo.InvariantCulture;
                    var pos = new Vector3(float.Parse(a[0], ci), 0f, float.Parse(a[1], ci));
                    string path = a.Length > 2 ? a[2] : "Minerals/CommonCopper-Ore";
                    Enemies.SpecialsDebug.SpawnManual(path, pos, false);
                    return $"spawned {path} at {pos}";
                };
                Commands["statues"] = _ =>
                {
                    var live = UnityEngine.Object.FindObjectsOfType<StatueComponent>().Where(x => x != null)
                        .Select(x => $"{x.gameObject.name}@{x.transform.position:0.0} root={x.transform.root.name} scale={x.transform.lossyScale.x:0.00}");
                    var all = Resources.FindObjectsOfTypeAll<StatueComponent>().Where(x => x != null).Select(x => x.gameObject.name + (x.gameObject.scene.IsValid() ? "" : "[asset]")).Distinct();
                    return $"live: {string.Join("; ", live)} || all: {string.Join(", ", all)}";
                };
                Commands["near"] = a =>
                {
                    // big renderers/colliders around a point (also inactive ones): what makes up an arena
                    var ci = System.Globalization.CultureInfo.InvariantCulture;
                    var at = new Vector3(float.Parse(a[0], ci), 0f, float.Parse(a[1], ci));
                    float r = a.Length > 2 ? float.Parse(a[2], ci) : 40f;
                    var rows = new List<string>();
                    foreach (var rd in Resources.FindObjectsOfTypeAll<Renderer>())
                    {
                        if (rd == null || !rd.gameObject.scene.IsValid()) continue;
                        var b = rd.bounds; var c = b.center; c.y = 0f;
                        if ((c - at).magnitude > r || b.size.x < 25f || rd.transform.root.name.Contains("(Clone)") || rd.transform.root.name == "Player") continue;
                        rows.Add($"R {rd.transform.root.name}/{rd.gameObject.name} act={rd.gameObject.activeInHierarchy} en={rd.enabled} vis={rd.isVisible} size={b.size:0} y={b.center.y:0.0}");
                    }
                    foreach (var co in Resources.FindObjectsOfTypeAll<Collider>())
                    {
                        if (co == null || co.isTrigger || !co.gameObject.scene.IsValid()) continue;
                        var b = co.bounds; var c = b.center; c.y = 0f;
                        if ((c - at).magnitude > r || b.size.x < 8f) continue;
                        rows.Insert(0, $"C {co.transform.root.name}/{co.gameObject.name} act={co.gameObject.activeInHierarchy} en={co.enabled} layer={co.gameObject.layer} size={b.size:0}");
                    }
                    foreach (var cam in Resources.FindObjectsOfTypeAll<Camera>())
                        if (cam != null && cam.gameObject.scene.IsValid())
                            rows.Insert(0, $"CAM {cam.transform.root.name}/{cam.name} act={cam.gameObject.activeInHierarchy} en={cam.enabled} mask={cam.cullingMask:X} far={cam.farClipPlane:0} pos={cam.transform.position:0}");
                    return rows.Count + " | " + string.Join(" ; ", rows.Distinct().Take(60));
                };
                Commands["loadscene"] = Lobby.MasterySync.TestLoadScene;
                Commands["crusade"] = Lobby.CrusadeSync.Test;
                Commands["options"] = Ui.CoopUi.TestOptions;
                Commands["char"] = a =>
                {
                    var prof = Global.PlayerProfile;
                    if (prof?.SelectedCharacter == null) return "no profile";
                    if (a.Length == 0) return prof.SelectedCharacter.CharacterIdAsEnum.ToString();
                    var ct = (CharactersType)Enum.Parse(typeof(CharactersType), a[0], true);
                    prof.SelectedCharacter.CharacterIdAsEnum = ct;
                    prof.SelectedCharacter.CharacterId = CharactersTypeNames.GetStringId(ct);
                    return "character " + ct; // CoopSession's poll sees the change and sends a new Hello
                };
                Commands["guestcc"] = Enemies.GuestEffects.TestApply;
                Commands["enemycc"] = Enemies.GuestEffects.TestInspect;
                Commands["levelup"] = _ => { var xp = GameState.LocalPlayerEntity?._experience; if (xp == null) return "no xp"; xp.LevelUpMultipleTimes(1); return "level " + xp.Level; };
                Commands["healpath"] = _ => GameManagerHealingCrystalSpawnComponent.PrefabPath ?? "-";
                // Host: "forcecast Slayer Bladestorm 5" casts that enemy's skill at us, channeled for 5 s.
                Commands["forcecast"] = a =>
                {
                    if (a.Length < 2) return "usage: forcecast ENTITY_PART SKILL_PART [SECONDS]";
                    float secs = a.Length > 2 && float.TryParse(a[2], System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var f) ? f : 5f;
                    foreach (var e in UnityEngine.Object.FindObjectsOfType<Entity>())
                    {
                        if (e == null || e.gameObject.name.IndexOf(a[0], StringComparison.OrdinalIgnoreCase) < 0 || e._skillCaster == null) continue;
                        foreach (var s in e.GetComponentsInChildren<Skill>(true))
                        {
                            if (s == null || s.gameObject.name.IndexOf(a[1], StringComparison.OrdinalIgnoreCase) < 0) continue;
                            float until = Time.time + secs;
                            var p = new SkillActivationParameters { TargetEntity = GameState.LocalPlayerEntity };
                            p.IsBeingChanneled = (Il2CppSystem.Func<bool>)(() => Time.time < until);
                            var me = GameState.LocalPlayerEntity;
                            Vector3 aim = me != null ? me.transform.position : e.transform.position;
                            p.GetTargetFrontPosition = (Il2CppSystem.Func<Skill, Vector3>)(_ => aim);
                            p.GetTargetFrontPositionGround = (Il2CppSystem.Func<Skill, Vector3>)(_ => aim);
                            Skills.ActivationParamsRaw.SetVector(p, Skills.ActivationParamsRaw.TargetFrontPosition, aim);
                            bool ok = e._skillCaster.CastSkill(s, p, SkillActivationOptions.IgnoreAll);
                            return $"cast {s.gameObject.name} by {e.gameObject.name}: {ok}";
                        }
                    }
                    return "not found";
                };
                Commands["find"] = a =>
                {
                    if (a.Length == 0) return "usage: find NAME_PART";
                    var hits = new List<string>();
                    foreach (var e in UnityEngine.Object.FindObjectsOfType<Entity>())
                        if (e != null && e.gameObject.name.IndexOf(a[0], StringComparison.OrdinalIgnoreCase) >= 0)
                            hits.Add($"{e.gameObject.name}@{e.transform.position:0} team={(e._team != null ? e._team.Team.ToString() : "-")} setup={e.HasBeenSetUp} tracked={Enemies.HostEnemySync.TryGetNetId(e, out var id)}#{id} icon={(e._health != null ? e._health.MinimapIconId : -9)} ignore={(e._health != null && e._health.IgnoreMinimapTracking)}");
                    return hits.Count + ": " + string.Join(" | ", hits.Take(8));
                };
                Commands["mmicons"] = _ =>
                {
                    var items = MinimapManager._items;
                    if (items == null) return "no items";
                    var arr = items._elements;
                    int n = Mathf.Min(items.Count, arr != null ? arr.Length : 0);
                    var counts = new System.Collections.Generic.Dictionary<string, int>();
                    for (int i = 0; i < n; i++)
                    {
                        var it = arr![i];
                        if (it == null || it.State != MinimapItemState.Active) continue;
                        string k = it.Type + ":" + (it.TransformToFollow != null ? it.TransformToFollow.name : "-");
                        counts[k] = counts.TryGetValue(k, out var c) ? c + 1 : 1;
                    }
                    return string.Join(" | ", counts.OrderBy(kv => kv.Key).Select(kv => $"{kv.Key} x{kv.Value}"));
                };
                // Host: "spawn Bosses/Slayer" puts that prefab 6 m from the first guest (or from us).
                Commands["spawn"] = a =>
                {
                    if (a.Length == 0) return "usage: spawn RESOURCE_PATH";
                    var at = GameState.LocalPlayerEntity != null ? GameState.LocalPlayerEntity.transform.position : Vector3.zero;
                    foreach (var r in CoopSession.Remotes) if (r.Ghost != null) { at = r.Ghost.transform.position; break; }
                    var e = Enemies.SpecialsDebug.SpawnManual(a[0], at + new Vector3(6f, 0f, 0f), false);
                    return "spawned " + e.gameObject.name;
                };
                Commands["xpmult"] = _ => Loot.LootSync.MultiplierInfo();
                // A pickup the game's way (through the shared pool); "xpmod V" adds V to our ExperienceModifier stat.
                Commands["pickup"] = a =>
                {
                    var xp = GameState.LocalPlayerEntity?._experience;
                    if (xp == null || a.Length == 0 || !float.TryParse(a[0], System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var n)) return "usage: pickup N";
                    xp.AddExperience(n);
                    return $"lv={xp._level} exp={xp._experience:F1} toAdd={xp._experienceToAdd:F1}";
                };
                Commands["xpmod"] = a =>
                {
                    var st = GameState.LocalPlayerEntity?._stats?.Stats;
                    if (st == null) return "no stats";
                    if (a.Length > 0 && float.TryParse(a[0], System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var v)) st.ExperienceModifier += v;
                    return $"ExperienceModifier={st.ExperienceModifier:F3}";
                };
                Commands["xp"] = a =>
                {
                    var xp = GameState.LocalPlayerEntity?._experience;
                    if (xp == null) return "no xp";
                    string S() => $"lv={xp._level} exp={xp._experience:F1} toAdd={xp._experienceToAdd:F1} req={xp._requiredExperience:F1} hidden={xp.HiddenExperienceMultiplier:F2} disc={xp._endlessExperienceDiscount:F2}";
                    string before = S();
                    if (a.Length > 0 && float.TryParse(a[0], System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var n))
                    {
                        Loot.LootSync.ApplyingSharedXp = true;
                        try { xp.AddExperience(n); } finally { Loot.LootSync.ApplyingSharedXp = false; }
                    }
                    return before + " -> " + S();
                };
                Commands["pick"] = a =>
                {
                    var panel = GameManagerUtil.GameplayUIManager?.LevelUpPanel;
                    if (panel == null) return "no level-up panel";
                    Ui.LevelUpOverlay.Pick(panel, a.Length > 0 && int.TryParse(a[0], out var n) ? n : 0);
                    return "picked";
                };
                Commands["god"] = _ => { _god = true; var h = GameState.LocalPlayerEntity?._health; if (h == null) return "no health (kept on)"; h.InvulnerabilityTime = 100000f; return "invulnerable"; };
                Commands["panel"] = a => { Ui.CoopUi.TestOpen(a.Length == 0 || a[0] != "off"); return "panel toggled"; };
                Commands["speeds"] = _ => PlayerSync.TestSpeeds();
                StateProviders["affix"] = () => RunSync.LastRunAffixes.Replace(' ', ',');
                Plugin.StaticLog.LogInfo($"[SoulstoneCoop][test] bridge '{Plugin.TestArg}' in {_dir}");
            }
            TickFight(dt);
            TickAutoPick(dt);
            TickWalk();
            _poll -= dt;
            if (_poll > 0f) return;
            _poll = 0.2f;
            var req = Path.Combine(_dir, "request.txt");
            if (!File.Exists(req)) return;
            string line;
            try { line = File.ReadAllText(req).Trim(); }
            catch (IOException) { return; } // still being written: next poll
            File.Delete(req);
            string reply;
            try { reply = Run(line); }
            catch (Exception ex) { reply = "error " + ex.Message; Plugin.StaticLog.LogError($"[SoulstoneCoop][test] '{line}': {ex}"); }
            File.WriteAllText(Path.Combine(_dir, "reply.txt"), reply);
        }
        catch (Exception ex)
        {
            Plugin.StaticLog.LogError($"[SoulstoneCoop][test] {ex}");
        }
    }

    private static string Run(string line)
    {
        var parts = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length == 0) return "empty";
        switch (parts[0])
        {
            case "shot":
                string path = Path.Combine(_dir!, (parts.Length > 1 ? parts[1] : "shot") + ".png");
                ScreenCapture.CaptureScreenshot(path);
                return "shot " + path;
            case "state": return State();
            case "play": return PressPlay();
            case "fight":
                _fightMode = parts.Length > 1 && parts[1] == "mastery" ? GameModes.Mastery : parts.Length > 1 && parts[1] == "crusade" ? GameModes.Crusade : GameModes.Survivor;
                _fightStep = 1;
                _fightTimer = 0f;
                return "fight started";
            case "quit":
                Application.Quit();
                return "quitting";
            case "cmd":
                if (parts.Length > 1 && Commands.TryGetValue(parts[1], out var h)) return h(parts.Skip(2).ToArray());
                return "unknown cmd; have: " + string.Join(",", Commands.Keys);
            default: return "unknown " + parts[0];
        }
    }

    private static string PressPlay()
    {
        var splash = UnityEngine.Object.FindObjectOfType<SplashScreenPanel>();
        if (splash == null || splash.PlayButton == null || !splash.PlayButton.gameObject.activeInHierarchy) return "no play button";
        ExecuteEvents.Execute(splash.PlayButton.gameObject, new PointerEventData(EventSystem.current), ExecuteEvents.pointerClickHandler);
        return "play pressed";
    }

    /// <summary>Hub "Fight" then the map screen's Fight, with the map/mode the profile has selected (the same path as
    /// the buttons, so RunSync's patches send RunStart).</summary>
    private static void TickFight(float dt)
    {
        if (_fightStep == 0) return;
        _fightTimer -= dt;
        if (_fightTimer > 0f) return;
        if (_fightStep == 1)
        {
            var fight = UnityEngine.Object.FindObjectOfType<FightScreenPanel>();
            if (fight == null) { Plugin.StaticLog.LogWarning("[SoulstoneCoop][test] fight: no FightScreenPanel"); _fightStep = 0; return; }
            fight.OnFightPressed();
            _fightStep = 2;
            _fightTimer = 2.5f;
            return;
        }
        if (_fightStep == 2)
        {
            var map = UnityEngine.Object.FindObjectOfType<MapSelectionScreenPanel>();
            if (map == null) { Plugin.StaticLog.LogWarning("[SoulstoneCoop][test] fight: no MapSelectionScreenPanel"); _fightStep = 0; return; }
            if (map.CurrentSelectedGameMode != _fightMode) { map.SelectGameMode(_fightMode); _fightTimer = 2f; Plugin.StaticLog.LogInfo($"[SoulstoneCoop][test] fight: mode -> {_fightMode}"); return; }
            map.OnFightPressed();
            Plugin.StaticLog.LogInfo($"[SoulstoneCoop][test] fight: map screen Fight pressed (mode {map.CurrentSelectedGameMode}).");
            _fightStep = 0;
        }
    }

    private static string State()
    {
        var sb = new StringBuilder();
        sb.Append("scene=").Append(UnityEngine.SceneManagement.SceneManager.GetActiveScene().name);
        sb.Append(" role=").Append(NetworkManager.Role).Append(" status=").Append(NetworkManager.Status);
        sb.Append(" players=").Append(Players.Count).Append(" place=").Append(CoopSession.LocalPlace);
        sb.Append(" timeScale=").Append(Time.timeScale.ToString("0.00"));
        var local = GameState.LocalPlayerEntity;
        if (local != null)
        {
            sb.Append(" pos=").Append(local.transform.position.ToString("0.0"));
            try
            {
                var hc = local.GetComponent<HealthComponent>();
                if (hc != null) sb.Append($" hp={hc.CurrentHealth:0}");
            }
            catch { }
        }
        foreach (var r in CoopSession.Remotes)
            sb.Append($" remote{r.Slot}={r.Name}/{r.Place}/ghost={(r.Ghost != null ? r.Ghost.transform.position.ToString("0.0") : "-")}");
        foreach (var kv in StateProviders)
        {
            try { sb.Append(' ').Append(kv.Key).Append('=').Append(kv.Value()); }
            catch (Exception ex) { sb.Append(' ').Append(kv.Key).Append("=err:").Append(ex.Message); }
        }
        return sb.ToString();
    }

    /// <summary>"cmd hurt N": take N off the local player's health directly (no hit, no death below 1).</summary>
    private static string Hurt(string[] args)
    {
        var local = GameState.LocalPlayerEntity;
        var hc = local != null ? local.GetComponent<HealthComponent>() : null;
        if (hc == null) return "no local health";
        float n = args.Length > 0 && float.TryParse(args[0], out var v) ? v : 30f;
        hc.CurrentHealth = Mathf.Max(1f, hc.CurrentHealth - n);
        return $"hp={hc.CurrentHealth:0}";
    }

    /// <summary>"cmd animparams": the parameters of each partner ghost's model Animator.</summary>
    private static string AnimParams(string[] args)
    {
        var parts = new List<string>();
        foreach (var r in CoopSession.Remotes)
        {
            var a = r.ModelAnimator;
            if (a == null) { parts.Add($"slot{r.Slot}:no animator"); continue; }
            var names = new List<string>();
            for (int i = 0; i < a.parameterCount; i++) { var p = a.GetParameter(i); names.Add($"{p.name}:{p.type}"); }
            parts.Add($"slot{r.Slot}:{a.name}:[{string.Join(",", names)}]");
        }
        return string.Join(" ", parts);
    }

    /// <summary>"cmd godcast [N]": the God-Entity casts its N-th skill (not the meteor) at the local player's feet.</summary>
    private static string GodCast(string[] args)
    {
        var god = GameManagerUtil.HasGameManager ? GameManagerUtil.GameData?.GodEntity : null;
        var local = GameState.LocalPlayerEntity;
        if (god == null || local == null || god._skillCaster == null) return "no god entity";
        var skills = god.GetComponentsInChildren<Skill>(true).Where(x => x != null).ToList();
        if (args.Length > 0 && args[0] == "list") return string.Join(",", skills.Select(x => x.gameObject.name + ":" + x.SkillType));
        int n = args.Length > 0 && int.TryParse(args[0], out var v) ? v : 0;
        if (n >= skills.Count) return $"only {skills.Count} skills";
        var skill = skills[n];
        var p = new SkillActivationParameters();
        Skills.ActivationParamsRaw.SetVector(p, Skills.ActivationParamsRaw.TargetGroundPosition, local.transform.position + new Vector3(3f, 0f, 0f));
        Skills.ActivationParamsRaw.SetVector(p, Skills.ActivationParamsRaw.TargetFrontPosition, local.transform.position + new Vector3(3f, 0f, 0f));
        bool ok = god._skillCaster.CastSkill(skill, p, SkillActivationOptions.IgnoreAll);
        return $"god cast '{skill.gameObject.name}' ok={ok}";
    }

    /// <summary>"cmd skills": the local player's skills: name, C# type, SkillType, active.</summary>
    private static string ListSkills(string[] args)
    {
        var local = GameState.LocalPlayerEntity;
        if (local == null) return "no local player";
        return string.Join(" | ", local.GetComponentsInChildren<Skill>(true).Where(x => x != null)
            .Select(x => $"{x.gameObject.name}:{x.GetIl2CppType().Name}:{x.SkillType}:{(x.isActiveAndEnabled ? "on" : "off")}"));
    }

    /// <summary>"cmd bosses [kill]": the bosses this side has (puppets on a client), optionally killing the first (host).</summary>
    private static string Bosses(string[] args)
    {
        var list = GameManagerUtil.BossEntities;
        if (list == null) return "no boss list";
        var names = new List<string>();
        Entity? first = null;
        foreach (var b in list) { if (b == null) continue; first ??= b; names.Add($"{b.gameObject.name}@{b.transform.position:0}"); }
        if (args.Length > 0 && args[0] == "kill" && first != null && first._health != null)
        {
            first._health.InvulnerabilityTime = 0f;
            UnitUtil.ChangeEntityHealth(GameState.LocalPlayerEntity!, first, -(first._health.CurrentHealth + 1e9f), true, out _, out _, out _);
            return "killed " + first.gameObject.name;
        }
        return $"{names.Count}: {string.Join(", ", names)}";
    }

    /// <summary>"cmd bossvis": for each boss, its renderers (enabled/total), active model children and material names.</summary>
    private static string BossVis(string[] args)
    {
        var list = GameManagerUtil.BossEntities;
        if (list == null) return "no boss list";
        var parts = new List<string>();
        foreach (var b in list)
        {
            if (b == null) continue;
            var rs = b.GetComponentsInChildren<Renderer>(true);
            int on = rs.Count(r => r != null && r.enabled && r.gameObject.activeInHierarchy);
            var mats = rs.Where(r => r != null && r.enabled && r.gameObject.activeInHierarchy).SelectMany(r => r.sharedMaterials)
                .Where(m => m != null).Select(m => m.name).Distinct().Take(4);
            var kids = new List<string>();
            for (int i = 0; i < b.transform.childCount; i++) { var c = b.transform.GetChild(i); kids.Add((c.gameObject.activeSelf ? "" : "-") + c.name); }
            parts.Add($"{b.gameObject.name}: renderers {on}/{rs.Length} mats=[{string.Join(",", mats)}] children=[{string.Join(",", kids.Take(8))}] hp={b._health?.CurrentHealth:0} invuln={b._health?.InvulnerabilityTime:0.0}");
        }
        return string.Join(" || ", parts);
    }

    /// <summary>"cmd tasks [new]": live map tasks (name, stage, trigger, icon text); "new" asks the spawner for one now.</summary>
    private static string Tasks(string[] args)
    {
        var sp = UnityEngine.Object.FindObjectOfType<GameManagerMapTaskSpawnerComponent>();
        if (args.Length > 0 && args[0] == "list")
            return string.Join(",", Resources.LoadAll<MapTask>("MapTasks/Tasks").Where(x => x != null).Select(x => x.gameObject.name + ":" + x.GetIl2CppType().Name));
        if (args.Length > 1 && args[0] == "spawn")
        {
            var prefab = Resources.Load<MapTask>("MapTasks/Tasks/" + args[1]);
            var local = GameState.LocalPlayerEntity;
            if (prefab == null || local == null) return "no prefab / player";
            var t = UnityEngine.Object.Instantiate(prefab.gameObject).GetComponent<MapTask>();
            t.Setup();
            t.StartTaskAtPosition(local.transform.position + new Vector3(-20f, 0f, 0f), Quaternion.identity);
            return $"spawned {t.gameObject.name} stage={t.Stage}";
        }
        if (args.Length > 0 && args[0] == "new")
        {
            if (sp == null) return "no task spawner";
            sp.GetNewTask();
            return "asked for a task";
        }
        var parts = new List<string> { $"spawner={(sp != null ? $"active={sp._isActive} pending={sp._pendingTasks} simultaneous={sp.SimultaneousTasks}" : "none")}" };
        foreach (var t in UnityEngine.Object.FindObjectsOfType<MapTask>())
        {
            if (t == null) continue;
            var trig = t._taskTriggerObjectInstance;
            string text = "";
            try { var w = t.WorldSpaceMapTaskIcon; var tmp = w != null ? w.GetComponentInChildren<TMPro.TextMeshProUGUI>(true) : null; text = tmp != null ? tmp.text : ""; } catch { }
            parts.Add($"{t.gameObject.name}({t.GetIl2CppType().Name}) stage={t.Stage} at={t.transform.position:0} trig={(trig != null ? trig.gameObject.name + "@" + trig.transform.position.ToString("0") + ":" + trig.OneTimeTrigger?.State : "-")} " +
                $"addr={t.TaskMetadata.TaskAddress} diff={t.TaskMetadata.Difficulty} text='{text}'");
        }
        return string.Join(" | ", parts);
    }

    /// <summary>"cmd inv": the local player's run inventory: soulstones, prestige and items (materials) by id.</summary>
    private static string Inventory(string[] args)
    {
        var inv = GameState.LocalPlayerEntity?._inventory;
        var d = inv != null ? inv.InventoryData : null;
        if (d == null) return "no inventory";
        var items = new List<string>();
        if (d.Items != null) foreach (var it in d.Items) if (it != null) items.Add($"{it.ItemId}={it.Quantity:0}");
        return $"minor={d.MinorSoulstones:0} corrupted={d.CorruptedSoulstones} vile={d.VileSoulstones} wicked={d.WickedSoulstones} hateful={d.HatefulSoulstones} rogue={d.RogueSoulstones} prestige={d.Prestige:0} items=[{string.Join(",", items)}]";
    }

    /// <summary>"cmd mastery": the Mastery run's state on this side.</summary>
    private static string Mastery(string[] args)
    {
        var gm = UnityEngine.Object.FindObjectOfType<MasteryGameManager>();
        if (gm == null) return "no MasteryGameManager";
        var parts = new List<string>
        {
            $"special={gm.BossSpawnDetails?.SpecialBossInfo?.Entity?.gameObject.name ?? "-"} tracker={(gm._mainBossTrackerComponentReference != null)} cycle={gm.CurrentMasteryCycle}/{gm.TotalCyclesAmount} setUp={gm.IsSetUp} spawnTriggered={gm._bossSpawnTriggered} spawnFinished={gm._bossSpawnFinished} lords={gm._lordsKilled} mainBoss={(gm._mainBossEntity != null ? gm._mainBossEntity.gameObject.name : "-")} mainId={gm._mainBossId}",
        };
        var dp = gm.DisruptThePillarsObjective;
        if (dp != null) parts.Add($"pillarsObj={dp.GetCurrentProgressPercent():0.00} done={dp.IsComplete()}");
        var mo = gm.MainObjective;
        if (mo != null) parts.Add($"main={mo.Current}/{mo.Total}");
        var pillars = UnityEngine.Object.FindObjectsOfType<MasteryPillar>();
        parts.Add("pillars=" + string.Join(",", pillars.Where(x => x != null).Select(x => $"{x.gameObject.name}:{(x.Destroyed ? "X" : "ok")}@{x.transform.position:0}")));
        foreach (var sp in UnityEngine.Object.FindObjectsOfType<MasteryBossSpawner>())
            if (sp != null) parts.Add($"spawner@{sp.transform.position:0} active={sp.gameObject.activeInHierarchy} triggered={sp._triggered} timer={sp._currentTimer:0.0}/{sp.TimeToEnter:0.0}");
        foreach (var b in UnityEngine.Object.FindObjectsOfType<MasteryBossComponent>())
            if (b != null) parts.Add($"bossAI:{b.gameObject.name}@{b.transform.position:0} state={b.CurrentState} registered={b.BossRegistered}");
        return string.Join(" | ", parts);
    }

    private static bool _autoPick;
    private static float _autoPickTimer;

    /// <summary>"cmd autopick": tests pick the first upgrade card as soon as a choice is open.</summary>
    private static void TickAutoPick(float dt)
    {
        if (!_autoPick) return;
        _autoPickTimer -= dt;
        if (_autoPickTimer > 0f) return;
        _autoPickTimer = 0.25f;
        var panel = GameManagerUtil.HasGameManager ? GameManagerUtil.GameplayUIManager?.LevelUpPanel : null;
        if (panel != null && panel.gameObject.activeInHierarchy) Ui.LevelUpOverlay.Pick(panel, 0);
    }

    private static Vector3 _walkVel;
    private static float _walkUntil;

    /// <summary>"cmd walk VX VZ SECONDS": slide the local player at that velocity (u/s) for a while.</summary>
    private static string Walk(string[] args)
    {
        var ci = System.Globalization.CultureInfo.InvariantCulture;
        if (args.Length < 3) return "usage: walk VX VZ SECONDS";
        _walkVel = new Vector3(float.Parse(args[0], ci), 0f, float.Parse(args[1], ci));
        _walkUntil = Time.unscaledTime + float.Parse(args[2], ci);
        var local = GameState.LocalPlayerEntity;
        var ch = local != null ? local._character : null;
        return $"walking at {_walkVel.magnitude:0.0} u/s (own max speed {(ch != null ? ch.GetMaxSpeed() : -1):0.0})";
    }

    private static void TickWalk()
    {
        if (Time.unscaledTime >= _walkUntil) return;
        var local = GameState.LocalPlayerEntity;
        if (local == null) return;
        var pos = local.transform.position + _walkVel * Time.deltaTime;
        local.transform.position = pos;
        var rb = local.GetComponent<Rigidbody>();
        if (rb != null) rb.position = pos;
    }

    /// <summary>"cmd tp X Z": move the local player there (transform and rigidbody).</summary>
    private static string Teleport(string[] args)
    {
        var local = GameState.LocalPlayerEntity;
        if (local == null || args.Length < 2) return "usage: tp X Z (in a run)";
        var ci = System.Globalization.CultureInfo.InvariantCulture;
        var pos = new Vector3(float.Parse(args[0], ci), local.transform.position.y, float.Parse(args[1], ci));
        local.transform.position = pos;
        var rb = local.GetComponent<Rigidbody>();
        if (rb != null) rb.position = pos;
        return "at " + local.transform.position.ToString("0.0");
    }

    /// <summary>Extra state fields registered by feature modules.</summary>
    internal static readonly Dictionary<string, Func<string>> StateProviders = new();
}
