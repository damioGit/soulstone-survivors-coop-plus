using TMPro;
using UnityEngine;

namespace SoulstoneCoop;

/// <summary>
/// Phase 5: the partner's name above the ghost, as a world-space TextMeshPro child of the ghost. The game's combat text
/// is out (16-character buffer and short-lived), and so is OnGUI (stripped in this build). The text follows the ghost
/// because it's its child, faces the camera every tick and picks up a name that arrives after the ghost (Hello).
/// </summary>
public static class GhostNameTag
{
    private const int MaxChars = 24;
    private const float FontSize = 5f;

    private static GameObject? _tag;
    private static TextMeshPro? _text;
    private static string _shown = "";
    private static bool _failedLogged;

    public static void Attach(GameObject ghost, float height)
    {
        _tag = null;
        _text = null;
        _shown = "";
        try
        {
            var go = new GameObject("SoulstoneCoop_NameTag");
            go.transform.SetParent(ghost.transform, false);
            float sy = Mathf.Abs(ghost.transform.lossyScale.y);
            go.transform.localPosition = new Vector3(0f, height / (sy > 0.01f ? sy : 1f), 0f);

            var t = go.AddComponent<TextMeshPro>();
            var font = FindFont();
            if (font != null) t.font = font;
            t.fontSize = FontSize;
            t.alignment = TextAlignmentOptions.Center;
            t.enableWordWrapping = false;
            t.richText = false;
            t.color = new Color(0.55f, 0.85f, 1f, 1f);
            t.rectTransform.sizeDelta = new Vector2(12f, 2f);
            t.sortingOrder = 200;
            try
            {
                t.outlineWidth = 0.25f;
                t.outlineColor = new Color32(0, 0, 0, 255);
            }
            catch (Exception ex) { Plugin.StaticLog.LogWarning($"[SoulstoneCoop] Name tag outline: {ex.Message}"); }

            _tag = go;
            _text = t;
            Refresh();
            Plugin.StaticLog.LogInfo($"[SoulstoneCoop] Name tag '{_shown}' attached {height:0.0} above the ghost (font={(font != null ? font.name : "TMP default")}).");
        }
        catch (Exception ex)
        {
            if (!_failedLogged)
            {
                _failedLogged = true;
                Plugin.StaticLog.LogError($"[SoulstoneCoop] Name tag could not be created (ghost goes without it): {ex}");
            }
        }
    }

    public static void Tick()
    {
        if (_tag == null || _text == null) return;
        try
        {
            Refresh();
            var cam = Camera.main;
            if (cam != null) _tag.transform.rotation = cam.transform.rotation; // billboard: same facing as the camera
        }
        catch (Exception ex)
        {
            Plugin.StaticLog.LogWarning($"[SoulstoneCoop] Name tag tick: {ex.Message}");
            _tag = null;
            _text = null;
        }
    }

    private static void Refresh()
    {
        string name = Lobby.CoopSession.PartnerName;
        if (name.Length > MaxChars) name = name.Substring(0, MaxChars - 3) + "...";
        if (name == _shown || _text == null) return;
        _shown = name;
        _text.text = name;
    }

    /// <summary>The co-op UI's font (the game's menu font, found in the menu), else any TMP font in the scene.</summary>
    private static TMP_FontAsset? FindFont()
    {
        var f = Ui.CoopUi.GameFont;
        if (f != null) return f;
        try
        {
            foreach (var t in UnityEngine.Object.FindObjectsOfType<TextMeshProUGUI>(true))
            {
                if (t != null && t.font != null) return t.font;
            }
        }
        catch { /* TMP default below */ }
        return TMP_Settings.defaultFontAsset;
    }
}
