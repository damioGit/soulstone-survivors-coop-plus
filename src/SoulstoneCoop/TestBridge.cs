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
