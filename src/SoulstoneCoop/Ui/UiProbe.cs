using System.Text;
using TMPro;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.UI;

namespace SoulstoneCoop.Ui;

/// <summary>
/// Test harness for the co-op UI, off unless %USERPROFILE%\.soulstone-uitest exists. In the main menu it presses
/// Play on the title screen, waits for the Co-op tab, dumps the game's UI tree (sprites, 9-slice borders, fonts) to
/// %USERPROFILE%\soulstone-uitest\ui-dump.txt, opens the co-op panel in each state given in the flag file and
/// screenshots it, then quits if the flag file says "quit".
/// Flag file lines: "quit", "dump", "states=idle,hosting,connected", "ref" (also shoot the game's confirmation popup).
/// </summary>
public static class UiProbe
{
    private static readonly string Flag = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".soulstone-uitest");
    private static readonly string OutDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "soulstone-uitest");

    private static bool? _enabled;
    private static string[] _opts = Array.Empty<string>();
    private static readonly Queue<(string Name, Func<bool> Run, float Wait)> _steps = new();
    private static float _timer = 6f, _stepTime;

    public static void Tick(bool inMenu, bool uiBuilt)
    {
        if (_enabled == null) Init();
        if (_enabled != true || !inMenu || !uiBuilt) return;
        _timer -= Time.unscaledDeltaTime;
        if (_timer > 0f) return;
        if (_steps.Count == 0) return;

        var (name, run, wait) = _steps.Peek();
        try
        {
            _stepTime += 0.25f;
            if (!run())
            {
                if (_stepTime > 40f) throw new Exception($"step '{name}' timed out");
                _timer = 0.25f;
                return;
            }
            Plugin.StaticLog.LogInfo($"[SoulstoneCoop][uitest] step '{name}' ok");
            _steps.Dequeue();
            _stepTime = 0f;
            _timer = wait;
        }
        catch (Exception ex)
        {
            Plugin.StaticLog.LogError($"[SoulstoneCoop][uitest] {ex}");
            _enabled = false;
            File.WriteAllText(Path.Combine(OutDir, "done.txt"), "error: " + ex.Message);
            if (_opts.Contains("quit")) Application.Quit();
        }
    }

    private static void Init()
    {
        _enabled = File.Exists(Flag);
        if (_enabled != true) return;
        _opts = File.ReadAllLines(Flag).Select(l => l.Trim()).Where(l => l.Length > 0).ToArray();
        var s = _opts.FirstOrDefault(o => o.StartsWith("states="));
        var states = (s != null ? s.Substring(7) : "idle").Split(',').Select(x => x.Trim()).Where(x => x.Length > 0).ToList();
        Directory.CreateDirectory(OutDir);
        foreach (var f in Directory.GetFiles(OutDir, "*.png")) File.Delete(f);
        File.Delete(Path.Combine(OutDir, "done.txt"));
        Plugin.StaticLog.LogInfo($"[SoulstoneCoop][uitest] on: {string.Join(" | ", _opts)}");

        _steps.Enqueue(("press play", PressPlay, 1f));
        _steps.Enqueue(("tab visible", () => CoopUi.TestTabVisible, 2f));
        _steps.Enqueue(("hub shot", () => Shoot("hub"), 0.5f));
        if (_opts.Contains("dump")) _steps.Enqueue(("dump", () => { Dump(); return true; }, 0.2f));
        var sp = _opts.FirstOrDefault(o => o.StartsWith("sprites="));
        if (sp != null) _steps.Enqueue(("sprites", () => { ExportSprites(sp.Substring(8).Split(',')); return true; }, 0.2f));
        foreach (var st in states)
        {
            _steps.Enqueue(("open " + st, () => { CoopUi.TestState = st; CoopUi.TestOpen(true); return true; }, 1.2f));
            _steps.Enqueue(("shot " + st, () => Shoot("panel-" + st), 0.5f));
        }
        if (_opts.Contains("hover"))
        {
            _steps.Enqueue(("open hover", () => { CoopUi.TestState = "idle"; CoopUi.TestOpen(true); return true; }, 0.8f));
            _steps.Enqueue(("hover", () => { CoopUi.TestHoverAndNotice(); return true; }, 0.8f));
            _steps.Enqueue(("shot hover", () => Shoot("panel-hover"), 0.5f));
        }
        _steps.Enqueue(("close", () => { CoopUi.TestState = null; CoopUi.TestOpen(false); return true; }, 0.5f));
        if (_opts.Contains("ref")) _steps.Enqueue(("ref popup", OpenReferencePopup, 1.5f));
        if (_opts.Contains("ref")) _steps.Enqueue(("ref shot", () => Shoot("ref-popup"), 0.5f));
        if (_opts.Contains("watch")) _steps.Enqueue(("watch", Watch, 0f)); // never true: serves requests until "quit"
        _steps.Enqueue(("finish", Finish, 0f));
    }

    /// <summary>"watch": the agent drives the real mouse/keyboard and asks for screenshots by writing a line to
    /// soulstone-uitest\request.txt ("shot NAME" or "quit"); the reply is soulstone-uitest\reply.txt.</summary>
    private static bool Watch()
    {
        _stepTime = 0f; // no timeout
        var req = Path.Combine(OutDir, "request.txt");
        if (!File.Exists(req)) return false;
        string line = File.ReadAllText(req).Trim();
        File.Delete(req);
        string reply;
        if (line.StartsWith("shot ")) { Shoot(line.Substring(5).Trim()); reply = "shot " + line.Substring(5).Trim(); }
        else if (line == "quit") return true;
        else if (line == "field") reply = "field '" + CoopUi.TestFieldText + "' focused=" + CoopUi.IsTyping;
        else reply = "unknown " + line;
        File.WriteAllText(Path.Combine(OutDir, "reply.txt"), reply);
        return false;
    }

    private static bool PressPlay()
    {
        var splash = UnityEngine.Object.FindObjectOfType<SplashScreenPanel>();
        if (CoopUi.TestTabVisible) return true;
        if (splash == null || splash.PlayButton == null || !splash.PlayButton.gameObject.activeInHierarchy) return false;
        var go = splash.PlayButton.gameObject;
        var ev = new PointerEventData(EventSystem.current);
        ExecuteEvents.Execute(go, ev, ExecuteEvents.pointerClickHandler);
        return true;
    }

    private static bool OpenReferencePopup()
    {
        var conf = UnityEngine.Object.FindObjectOfType<ConfirmationPanel>(true);
        if (conf == null) return true;
        conf.gameObject.SetActive(true);
        conf.Open("Options_Reset_Title", "Options_Reset_Description", "Options_Reset_Confirm", "Options_Reset_Cancel");
        return true;
    }

    private static bool Finish()
    {
        File.WriteAllText(Path.Combine(OutDir, "done.txt"), "ok");
        Plugin.StaticLog.LogInfo("[SoulstoneCoop][uitest] done.");
        _enabled = false;
        if (_opts.Contains("quit")) Application.Quit();
        return true;
    }

    private static bool Shoot(string name)
    {
        string path = Path.Combine(OutDir, name + ".png");
        ScreenCapture.CaptureScreenshot(path);
        Plugin.StaticLog.LogInfo($"[SoulstoneCoop][uitest] shot {path} ({Screen.width}x{Screen.height})");
        return true;
    }

    private static void ExportSprites(string[] names)
    {
        var dir = Path.Combine(OutDir, "sprites");
        Directory.CreateDirectory(dir);
        var want = new HashSet<string>(names.Select(n => n.Trim()));
        foreach (var s in Resources.FindObjectsOfTypeAll<Sprite>())
        {
            if (s == null || !want.Remove(s.name) || s.texture == null) continue;
            var tex = s.texture;
            var rt = RenderTexture.GetTemporary(tex.width, tex.height, 0, RenderTextureFormat.ARGB32, RenderTextureReadWrite.sRGB);
            Graphics.Blit(tex, rt);
            var prev = RenderTexture.active;
            RenderTexture.active = rt;
            var r = s.textureRect;
            var outTex = new Texture2D((int)r.width, (int)r.height, TextureFormat.RGBA32, false);
            outTex.ReadPixels(new Rect(r.x, tex.height - r.y - r.height, r.width, r.height), 0, 0);
            outTex.Apply();
            RenderTexture.active = prev;
            RenderTexture.ReleaseTemporary(rt);
            File.WriteAllBytes(Path.Combine(dir, s.name + ".png"), ImageConversion.EncodeToPNG(outTex));
            UnityEngine.Object.Destroy(outTex);
        }
        Plugin.StaticLog.LogInfo($"[SoulstoneCoop][uitest] sprites exported; not found: {string.Join(",", want)}");
    }

    private static void Dump()
    {
        var sb = new StringBuilder();
        sb.AppendLine($"screen {Screen.width}x{Screen.height}");
        var seenSprites = new Dictionary<string, string>();
        foreach (var canvas in UnityEngine.Object.FindObjectsOfType<Canvas>(true))
        {
            if (canvas == null || !canvas.isRootCanvas) continue;
            var scaler = canvas.GetComponent<CanvasScaler>();
            sb.AppendLine($"\n=== CANVAS {canvas.name} mode={canvas.renderMode} order={canvas.sortingOrder} ref={scaler?.referenceResolution} match={scaler?.matchWidthOrHeight}");
            Walk(canvas.transform, 0, sb, seenSprites);
        }
        sb.AppendLine("\n=== SPRITES (name: texture size, rect, border, ppu)");
        foreach (var kv in seenSprites.OrderBy(k => k.Key)) sb.AppendLine($"{kv.Key}: {kv.Value}");
        File.WriteAllText(Path.Combine(OutDir, "ui-dump.txt"), sb.ToString());
        Plugin.StaticLog.LogInfo($"[SoulstoneCoop][uitest] dump written ({sb.Length} chars)");
    }

    private static void Walk(Transform t, int depth, StringBuilder sb, Dictionary<string, string> sprites)
    {
        if (depth > 14) return;
        var line = new StringBuilder();
        line.Append(new string(' ', depth * 2)).Append(t.gameObject.activeSelf ? "" : "(off) ").Append(t.name);
        var rt = t.GetComponent<RectTransform>();
        if (rt != null) line.Append($" [{rt.rect.width:0}x{rt.rect.height:0} @{rt.anchoredPosition.x:0},{rt.anchoredPosition.y:0}]");
        var img = t.GetComponent<Image>();
        if (img != null)
        {
            var sp = img.sprite;
            line.Append($" IMG({(sp != null ? sp.name : "-")} {img.type} c={Hex(img.color)}{(img.raycastTarget ? "" : " noray")})");
            if (sp != null && !sprites.ContainsKey(sp.name))
                sprites[sp.name] = $"{sp.texture?.name} {sp.rect.width:0}x{sp.rect.height:0} border={sp.border} ppu={sp.pixelsPerUnit:0}";
        }
        if (t.GetComponent<Button>() != null) line.Append(" BTN");
        var tmp = t.GetComponent<TextMeshProUGUI>();
        if (tmp != null)
        {
            string txt = tmp.text ?? "";
            if (txt.Length > 40) txt = txt.Substring(0, 40);
            line.Append($" TMP('{txt.Replace("\n", "\\n")}' {tmp.font?.name} {tmp.fontSize:0} {tmp.fontStyle} c={Hex(tmp.color)} {tmp.alignment})");
        }
        foreach (var c in t.GetComponents<Component>())
        {
            var n = c.GetIl2CppType().Name;
            if (n is "RectTransform" or "Image" or "Button" or "TextMeshProUGUI" or "CanvasRenderer") continue;
            line.Append(' ').Append(n);
        }
        sb.AppendLine(line.ToString());
        for (int i = 0; i < t.childCount; i++) Walk(t.GetChild(i), depth + 1, sb, sprites);
    }

    private static string Hex(Color c) => $"#{(int)(c.r * 255):X2}{(int)(c.g * 255):X2}{(int)(c.b * 255):X2}{(int)(c.a * 255):X2}";
}
