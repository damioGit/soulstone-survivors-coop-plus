using BepInEx.Configuration;
using SoulstoneCoop.Net;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

namespace SoulstoneCoop.Lobby;

/// <summary>
/// Experimental: in the main menu, each connected partner's chosen character stands next to ours, with their name
/// over it, and changes when they pick another character, skin or weapon. Nothing new on the wire: the Hello already
/// carries the character, skin and weapon, and is sent again whenever they change. The model is the run ghost's
/// (VisualGhostFactory: the partner's skin and weapons, every gameplay script stripped), placed beside the menu's
/// CharacterModelHolder and shown only while that holder shows our own character. Local only.
/// </summary>
internal static class LobbyPartners
{
    internal static ConfigEntry<bool> Enabled = null!;

    internal static void Bind(ConfigFile cfg)
    {
        Enabled = cfg.Bind("Coop", "LobbyPartners", true,
            "Experimental: show the connected players' chosen characters (with their names) next to yours in the main menu.");
    }

    // Spots beside our character, fixed to the camera, not to the holder (the player turns their character by dragging
    // it, which turns the holder): x = to the screen's left, z = toward the camera. A bit in front of our character, so
    // its weapon passes behind the partner instead of through them.
    private static readonly Vector3[] Spots = { new(1.3f, 0f, 0.15f), new(-1.3f, 0f, 0.15f), new(2.2f, 0f, -0.6f), new(-2.2f, 0f, -0.6f), new(0f, 0f, -1.8f) };
    private const float NameSize = 44f;
    private const int MaxChars = 24;

    private sealed class Stand
    {
        public GameObject? Model;
        public string Key = "";
        public float Height = 2.4f;
        public RectTransform? Tag;
        public TextMeshProUGUI? Name;
        public float RetryAt;
    }

    private static readonly Dictionary<byte, Stand> Stands = new();
    private static GameObject? _canvas;
    private static float _logTimer;

    public static void Tick()
    {
        try { Update(); }
        catch (Exception ex)
        {
            Plugin.StaticLog.LogWarning($"[SoulstoneCoop][lobby] Partners in the menu: {ex.Message}");
            ClearAll();
        }
    }

    private static CharacterModelHolder? MenuHolder()
    {
        foreach (var h in UnityEngine.Object.FindObjectsOfType<CharacterModelHolder>())
            if (h != null && h.isActiveAndEnabled && h.SpawnedCharacter != null && h.SpawnedCharacter.gameObject.activeInHierarchy) return h;
        return null;
    }

    private static void Update()
    {
        var holder = Enabled.Value && NetworkManager.HasPeer && !GameManagerUtil.HasGameManager ? MenuHolder() : null;
        if (holder == null) { if (Stands.Count > 0) ClearAll(); return; }

        // Partners gone (left, disconnected): their stand goes.
        foreach (var slot in Stands.Keys.ToList())
            if (CoopSession.Remotes.All(r => r.Slot != slot)) Remove(slot);

        var cam = Camera.main;
        var center = holder.ModelPlaceholder != null ? holder.ModelPlaceholder.position : holder.transform.position;
        var toCam = cam != null ? cam.transform.position - center : -holder.transform.forward;
        toCam.y = 0f;
        toCam = toCam.sqrMagnitude > 0.001f ? toCam.normalized : Vector3.back;
        var left = Vector3.Cross(Vector3.up, toCam); // the screen's left, seen from the camera
        int i = 0;
        foreach (var r in CoopSession.Remotes.OrderBy(r => r.Slot))
        {
            var spot = Spots[Mathf.Min(i++, Spots.Length - 1)];
            if (!Stands.TryGetValue(r.Slot, out var s)) Stands[r.Slot] = s = new Stand();
            string key = r.Info.Known && r.Info.CharacterId != 0 ? VisualGhostFactory.KeyFor(r.Info) : ""; // the first Hello can come before the profile
            var pos = center + left * spot.x + toCam * spot.z;
            var rot = Quaternion.LookRotation(toCam) * Quaternion.Euler(0f, spot.x > 0f ? -20f : spot.x < 0f ? 20f : 0f, 0f);
            if (s.Model == null || s.Key != key)
            {
                if (s.Model != null) UnityEngine.Object.Destroy(s.Model);
                s.Model = null;
                s.Key = key;
                if (key.Length == 0 || Time.unscaledTime < s.RetryAt) { HideTag(s); continue; }
                s.Model = VisualGhostFactory.CreateModelOnly(r.Info, $"SoulstoneCoop_LobbyPartner{r.Slot}", pos, rot, out var what);
                if (s.Model == null)
                {
                    s.RetryAt = Time.unscaledTime + 5f;
                    Plugin.StaticLog.LogWarning($"[SoulstoneCoop][lobby] Slot {r.Slot}'s character can't be shown in the menu: {what}.");
                    continue;
                }
                s.Height = BodyHeight(s.Model);
                Plugin.StaticLog.LogInfo($"[SoulstoneCoop][lobby] {r.Name}'s character stands in the menu: {what}.");
            }
            s.Model.transform.SetPositionAndRotation(pos, rot);
            PlaceTag(s, r, cam);
        }

        _logTimer -= Time.unscaledDeltaTime;
        if (_logTimer <= 0f)
        {
            _logTimer = 30f;
            Plugin.StaticLog.LogInfo($"[SoulstoneCoop][lobby] Menu partners: {string.Join(", ", Stands.Select(kv => $"{kv.Key}={(kv.Value.Model != null ? kv.Value.Key : "-")}"))}.");
        }
    }

    /// <summary>Top of the body (skinned meshes only: a raised weapon or an effect would put the name far above the
    /// head at the menu camera's distance).</summary>
    private static float BodyHeight(GameObject root)
    {
        float baseY = root.transform.position.y, top = float.MinValue;
        foreach (var r in root.GetComponentsInChildren<SkinnedMeshRenderer>(false))
            if (r != null) top = Mathf.Max(top, r.bounds.max.y);
        float h = top > float.MinValue ? top - baseY : 0f;
        return h < 0.8f || h > 6f ? 2.2f : h + 0.1f;
    }

    private static void PlaceTag(Stand s, RemotePlayer r, Camera? cam)
    {
        if (s.Model == null || cam == null) { HideTag(s); return; }
        if (s.Tag == null) BuildTag(s, r.Slot);
        if (s.Tag == null || s.Name == null) return;
        var screen = cam.WorldToScreenPoint(s.Model.transform.position + Vector3.up * s.Height);
        if (screen.z <= 0f) { HideTag(s); return; }
        s.Tag.position = new Vector3(Mathf.Round(screen.x), Mathf.Round(screen.y), 0f);
        string name = r.Name;
        if (name.Length > MaxChars) name = name.Substring(0, MaxChars - 3) + "...";
        if (s.Name.text != name) s.Name.text = name;
        if (!s.Tag.gameObject.activeSelf) s.Tag.gameObject.SetActive(true);
    }

    private static void HideTag(Stand s)
    {
        if (s.Tag != null && s.Tag.gameObject.activeSelf) s.Tag.gameObject.SetActive(false);
    }

    private static void BuildTag(Stand s, byte slot)
    {
        if (_canvas == null)
        {
            _canvas = new GameObject("SoulstoneCoop_LobbyNames");
            UnityEngine.Object.DontDestroyOnLoad(_canvas);
            var canvas = _canvas.AddComponent<Canvas>();
            canvas.renderMode = RenderMode.ScreenSpaceOverlay;
            canvas.sortingOrder = -100; // under the menu's own panels
            var scaler = _canvas.AddComponent<CanvasScaler>();
            scaler.uiScaleMode = CanvasScaler.ScaleMode.ScaleWithScreenSize;
            scaler.referenceResolution = new Vector2(1920f, 1080f);
            scaler.matchWidthOrHeight = 0.5f;
        }
        var go = new GameObject($"Name{slot}");
        var rt = go.AddComponent<RectTransform>();
        rt.SetParent(_canvas.transform, false);
        rt.sizeDelta = new Vector2(360f, 40f);
        rt.pivot = new Vector2(0.5f, 0f);
        var t = go.AddComponent<TextMeshProUGUI>();
        var font = Ui.CoopUi.GameFont;
        if (font != null) t.font = font;
        t.fontSize = NameSize;
        t.fontStyle = FontStyles.Bold;
        t.alignment = TextAlignmentOptions.Bottom;
        t.enableWordWrapping = false;
        t.richText = false;
        t.raycastTarget = false;
        t.color = GhostOverhead.ColorFor(slot);
        try
        {
            t.outlineWidth = 0.3f;
            t.outlineColor = new Color32(0, 0, 0, 255);
        }
        catch { /* plain text */ }
        s.Tag = rt;
        s.Name = t;
    }

    private static void Remove(byte slot)
    {
        if (!Stands.TryGetValue(slot, out var s)) return;
        Stands.Remove(slot);
        if (s.Model != null) UnityEngine.Object.Destroy(s.Model);
        if (s.Tag != null) UnityEngine.Object.Destroy(s.Tag.gameObject);
    }

    private static void ClearAll()
    {
        foreach (var slot in Stands.Keys.ToList()) Remove(slot);
    }
}
