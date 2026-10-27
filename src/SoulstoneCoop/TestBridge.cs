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
    private static float _fightTimer;

    /// <summary>Extra commands registered by feature modules: name -> handler(args) returning the reply.</summary>
    internal static readonly Dictionary<string, Func<string[], string>> Commands = new();

    public static bool Enabled => Plugin.TestArg != null;

    public static void Tick(float dt)
    {
        if (Plugin.TestArg == null) return;
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
                Commands["bossvis"] = BossVis;
                Commands["statuesync"] = Enemies.StatueSync.TestInfo;
                Commands["tasks"] = Tasks;
                Commands["inv"] = Inventory;
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
                Commands["guestcc"] = Enemies.GuestEffects.TestApply;
                Commands["enemycc"] = Enemies.GuestEffects.TestInspect;
                Commands["levelup"] = _ => { var xp = GameState.LocalPlayerEntity?._experience; if (xp == null) return "no xp"; xp.LevelUpMultipleTimes(1); return "level " + xp.Level; };
                Commands["pick"] = a =>
                {
                    var panel = GameManagerUtil.GameplayUIManager?.LevelUpPanel;
                    if (panel == null) return "no level-up panel";
                    Ui.LevelUpOverlay.Pick(panel, a.Length > 0 && int.TryParse(a[0], out var n) ? n : 0);
                    return "picked";
                };
                Commands["god"] = _ => { var h = GameState.LocalPlayerEntity?._health; if (h == null) return "no health"; h.InvulnerabilityTime = 100000f; return "invulnerable"; };
                Commands["panel"] = a => { Ui.CoopUi.TestOpen(a.Length == 0 || a[0] != "off"); return "panel toggled"; };
                Commands["speeds"] = _ => PlayerSync.TestSpeeds();
                StateProviders["affix"] = () => RunSync.LastRunAffixes.Replace(' ', ',');
                Plugin.StaticLog.LogInfo($"[SoulstoneCoop][test] bridge '{Plugin.TestArg}' in {_dir}");
            }
            TickFight(dt);
            TickWalk();
            _poll -= dt;
            if (_poll > 0f) return;
            _poll = 0.2f;
            var req = Path.Combine(_dir, "request.txt");
            if (!File.Exists(req)) return;
            string line = File.ReadAllText(req).Trim();
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
