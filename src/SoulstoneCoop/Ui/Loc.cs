using System.Globalization;
using System.Runtime.InteropServices;
using System.Text;
using BepInEx.Configuration;
using TMPro;
using UnityEngine;

namespace SoulstoneCoop.Ui;

/// <summary>The languages the game ships (resources.assets "Localization-Config": en, pt-BR, ja, zh, ru, de, fr, es).
/// The order is the column order of <see cref="Loc"/>'s table.</summary>
public enum Lang { En, PtBr, Es, Fr, De, Ru, Zh, Ja }

/// <summary>
/// Text the mod shows to the player, in the language of the game of THAT player (docs/i18n-notas.md).
/// - Language: the game's LocalizationManager.CurrentLanguageCode (what the Options menu sets), polled so a change
///   applies right away; before the game has one, Steam's game language (ISteamApps::GetCurrentGameLanguage); then
///   English. The config Coop.Language forces one (testing).
/// - <see cref="T"/> renders a key now. <see cref="Msg"/> encodes a key + parameters as a string that is rendered
///   later, where it is shown (<see cref="Show"/>): notices and errors keep their string fields and follow a language
///   change, and a message sent over the network is rendered by the receiver in its own language.
///   Encoding: &lt;link="ssc:key|name=value|..."&gt;English text&lt;/link&gt;. TMP draws a link tag as its inner text,
///   so an older mod (handshake refusals cross versions) still shows readable English.
/// - Logs stay in English: <see cref="Plain"/>.
/// - Fonts: the game swaps its TMP fonts per language (LanguageFontConfig, LocalizationManager._fontDatas);
///   <see cref="GameFont"/> returns the one for the current language.
/// </summary>
public static partial class Loc
{
    public static readonly string[] Codes = { "en", "pt-BR", "es", "fr", "de", "ru", "zh", "ja" };

    public static Lang Current { get; private set; } = Lang.En;
    /// <summary>Bumped on every language change: UIs compare it to rebuild text and fonts.</summary>
    public static int Version { get; private set; }

    private const string WirePrefix = "<link=\"ssc:";
    private const string WireEnd = "</link>";
    private const float PollInterval = 0.5f;

    private static ConfigEntry<string>? _override;
    private static float _pollTimer;
    private static string _lastSource = "";
    private static bool _gameReadFailed, _steamReadFailed;
    private static int _gameReadErrors;
    private static string? _steamLanguage;

    public static void Init(ConfigFile config)
    {
        _override = config.Bind("Coop", "Language", "Auto",
            "Language of the mod's texts: Auto = the game's language (Options menu), else en, pt-BR, es, fr, de, ru, zh or ja.");
        try { Validate(); }
        catch (Exception ex) { Plugin.StaticLog.LogWarning($"[SoulstoneCoop][i18n] Table check failed: {ex.Message}"); }
        // The language itself is read from the first Tick on (the game isn't up during Load).
    }

    public static void Tick()
    {
        _pollTimer -= Time.unscaledDeltaTime;
        if (_pollTimer > 0f) return;
        _pollTimer = PollInterval;
        Refresh(force: !_started);
        _started = true;
    }

    private static bool _started;

    private static void Refresh(bool force)
    {
        string source;
        Lang lang;
        string? forced = _override?.Value?.Trim();
        if (!string.IsNullOrEmpty(forced) && !forced!.Equals("Auto", StringComparison.OrdinalIgnoreCase)
            && TryParseCode(forced, out lang))
            source = $"config '{forced}'";
        else if (TryGameCode(out var code) && TryParseCode(code, out lang))
            source = $"game '{code}'";
        else if (TrySteamLanguage(out var steam) && TryParseSteam(steam, out lang))
            source = $"Steam '{steam}'";
        else
        {
            lang = Lang.En;
            source = "default";
        }
        // The game loads a language's fonts in a coroutine (LoadAndSetupWithLanguage): its code and its fonts may
        // change a few frames apart, and the UIs must pick up both.
        var font = GameFont(FontType.Regular);
        IntPtr fontPtr = font != null ? font.Pointer : IntPtr.Zero;
        bool fontChanged = fontPtr != _fontPtr;
        _fontPtr = fontPtr;

        bool changed = force || lang != Current;
        if (changed || source != _lastSource)
            Plugin.StaticLog.LogInfo($"[SoulstoneCoop][i18n] Language: {Codes[(int)lang]} (from {source}).");
        _lastSource = source;
        Current = lang;
        if (changed || fontChanged) Version++;
    }

    private static IntPtr _fontPtr;

    private static bool TryGameCode(out string code)
    {
        code = "";
        if (_gameReadFailed) return false;
        try
        {
            code = LocalizationManager.CurrentLanguageCode ?? "";
            return code.Length > 0;
        }
        catch (Exception ex)
        {
            _gameReadFailed = ++_gameReadErrors >= 5; // then Steam's language for good
            Plugin.StaticLog.LogWarning($"[SoulstoneCoop][i18n] Reading the game's language failed ({_gameReadErrors}): {ex.Message}");
            return false;
        }
    }

    public static bool TryParseCode(string code, out Lang lang)
    {
        string c = code.Trim().ToLowerInvariant();
        lang = Lang.En;
        if (c.StartsWith("pt")) lang = Lang.PtBr;
        else if (c.StartsWith("es")) lang = Lang.Es;
        else if (c.StartsWith("fr")) lang = Lang.Fr;
        else if (c.StartsWith("de")) lang = Lang.De;
        else if (c.StartsWith("ru")) lang = Lang.Ru;
        else if (c.StartsWith("zh")) lang = Lang.Zh;
        else if (c.StartsWith("ja")) lang = Lang.Ja;
        else if (!c.StartsWith("en")) return false;
        return true;
    }

    // ---------------- Steam fallback (flat API, like Lobby.SteamName) ----------------

    [DllImport("steam_api64", CallingConvention = CallingConvention.Cdecl)]
    private static extern IntPtr SteamAPI_SteamApps_v008();

    [DllImport("steam_api64", CallingConvention = CallingConvention.Cdecl)]
    private static extern IntPtr SteamAPI_ISteamApps_GetCurrentGameLanguage(IntPtr self);

    private static bool TrySteamLanguage(out string language)
    {
        language = _steamLanguage ?? "";
        if (_steamLanguage != null) return true;
        if (_steamReadFailed) return false;
        try
        {
            if (!SteamManager.Initialized) return false; // maybe later
            var apps = SteamAPI_SteamApps_v008();
            if (apps == IntPtr.Zero) return false;
            var p = SteamAPI_ISteamApps_GetCurrentGameLanguage(apps);
            var s = p != IntPtr.Zero ? Marshal.PtrToStringUTF8(p) : null;
            if (string.IsNullOrWhiteSpace(s)) return false;
            _steamLanguage = language = s!.Trim();
            return true;
        }
        catch (Exception ex)
        {
            _steamReadFailed = true;
            Plugin.StaticLog.LogWarning($"[SoulstoneCoop][i18n] Steam game language unavailable: {ex.Message}");
            return false;
        }
    }

    /// <summary>Steam API language names (partner.steamgames.com/doc/store/localization/languages).</summary>
    private static bool TryParseSteam(string steam, out Lang lang)
    {
        lang = steam.ToLowerInvariant() switch
        {
            "english" => Lang.En,
            "brazilian" or "portuguese" => Lang.PtBr,
            "spanish" or "latam" => Lang.Es,
            "french" => Lang.Fr,
            "german" => Lang.De,
            "russian" or "ukrainian" => Lang.Ru,
            "schinese" or "tchinese" => Lang.Zh,
            "japanese" => Lang.Ja,
            _ => (Lang)(-1),
        };
        return (int)lang >= 0;
    }

    // ---------------- rendering ----------------

    /// <summary>The text of <paramref name="key"/> in the current language, placeholders filled.</summary>
    public static string T(string key, params (string Name, object? Value)[] args) => Render(Current, key, args);

    /// <summary>The text of <paramref name="key"/> in English (logs).</summary>
    public static string En(string key, params (string Name, object? Value)[] args) => Render(Lang.En, key, args);

    /// <summary>A key + parameters as a string, rendered where it is shown (<see cref="Show"/>) in that player's
    /// language. Parameters may be messages themselves.</summary>
    public static string Msg(string key, params (string Name, object? Value)[] args)
    {
        var sb = new StringBuilder(WirePrefix).Append(key);
        foreach (var (name, value) in args)
            sb.Append('|').Append(name).Append('=').Append(Uri.EscapeDataString(Convert.ToString(value, CultureInfo.InvariantCulture) ?? ""));
        sb.Append("\">").Append(Render(Lang.En, key, args)).Append(WireEnd);
        return sb.ToString();
    }

    /// <summary>A message from <see cref="Msg"/> in the current language; any other string as it is (a text from an
    /// older mod, a name).</summary>
    public static string Show(string? s)
    {
        if (s == null) return "";
        if (!s.StartsWith(WirePrefix, StringComparison.Ordinal)) return s;
        // The banner and the panel show the same notice/error every frame: decode it once per language.
        if (_showVersion != Version || _showCache.Count > 64) { _showCache.Clear(); _showVersion = Version; }
        if (_showCache.TryGetValue(s, out var cached)) return cached;
        string r = TryDecode(s, Current, out var decoded) ? decoded : s;
        _showCache[s] = r;
        return r;
    }

    private static readonly Dictionary<string, string> _showCache = new();
    private static int _showVersion = -1;

    /// <summary>A message from <see cref="Msg"/> in English, for logs.</summary>
    public static string Plain(string? s) => s == null ? "" : TryDecode(s, Lang.En, out var r) ? r : s;

    private static bool TryDecode(string s, Lang lang, out string rendered)
    {
        rendered = s;
        if (!s.StartsWith(WirePrefix, StringComparison.Ordinal) || !s.EndsWith(WireEnd, StringComparison.Ordinal)) return false;
        int close = s.IndexOf("\">", WirePrefix.Length, StringComparison.Ordinal);
        if (close < 0) return false;
        var parts = s.Substring(WirePrefix.Length, close - WirePrefix.Length).Split('|');
        if (!Table.ContainsKey(parts[0]))
        {
            // A key from a newer mod: its English text is inside.
            rendered = s.Substring(close + 2, s.Length - close - 2 - WireEnd.Length);
            return true;
        }
        var args = new (string, object?)[parts.Length - 1];
        for (int i = 1; i < parts.Length; i++)
        {
            int eq = parts[i].IndexOf('=');
            if (eq <= 0) return false;
            string value;
            try { value = Uri.UnescapeDataString(parts[i].Substring(eq + 1)); }
            catch { return false; }
            args[i - 1] = (parts[i].Substring(0, eq), value);
        }
        rendered = Render(lang, parts[0], args);
        return true;
    }

    private static string Render(Lang lang, string key, (string Name, object? Value)[] args)
    {
        string text;
        if (!Table.TryGetValue(key, out var row)) text = key;
        else text = row[(int)lang] ?? row[(int)Lang.En] ?? key;
        if (args.Length == 0 || text.IndexOf('{') < 0) return text;
        var sb = new StringBuilder(text);
        foreach (var (name, value) in args)
        {
            string v = value is string str ? (TryDecode(str, lang, out var nested) ? nested : str)
                : Convert.ToString(value, CultureInfo.InvariantCulture) ?? "";
            sb.Replace("{" + name + "}", v);
        }
        return sb.ToString();
    }

    // ---------------- floating text (GPU combat text) ----------------

    /// <summary>Max characters of the game's floating text: GPUIndirectInstancedCombatTextManager has a fixed buffer
    /// per text (MaxCharacterCountPerText, 16 in this build); past it the text errors out and doesn't show.</summary>
    public const int MaxFloatChars = 16;

    /// <summary>Keys drawn with OverheadCombatTextPanelManager.RequestCombatTextWithString, with their widest
    /// parameters (checked against <see cref="MaxFloatChars"/> at startup).</summary>
    private static readonly (string Key, (string, object?)[] Widest)[] FloatKeys =
    {
        ("float.down", Array.Empty<(string, object?)>()),
        ("float.revive", new (string, object?)[] { ("pct", 100) }),
        ("float.revived", Array.Empty<(string, object?)>()),
    };

    private static int _floatFontVersion = -1;
    private static IntPtr _floatFontPtr;
    private static readonly Dictionary<string, string> _floatCache = new();

    /// <summary>A floating text in the current language, if it fits in <paramref name="max"/> characters and the
    /// combat text's font has every glyph; else the same text without accents; else English.</summary>
    public static string Float(string key, int max, params (string Name, object? Value)[] args)
    {
        string text = T(key, args);
        if (Current == Lang.En) return text;
        TMP_FontAsset? font = null;
        try { font = GPUIndirectInstancedCombatTextManager.Instance?._currentFont; } catch { }
        IntPtr ptr = font != null ? font.Pointer : IntPtr.Zero;
        if (ptr != _floatFontPtr || _floatFontVersion != Version)
        {
            _floatCache.Clear();
            _floatFontPtr = ptr;
            _floatFontVersion = Version;
        }
        if (_floatCache.TryGetValue(text, out var cached)) return cached;

        string result = text;
        if (!FloatFits(text, max, font))
        {
            string folded = FoldAccents(text);
            result = FloatFits(folded, max, font) ? folded : En(key, args);
            Plugin.StaticLog.LogInfo($"[SoulstoneCoop][i18n] Floating text '{text}' doesn't fit/draw (max {max}, font {font?.name ?? "?"}): using '{result}'.");
        }
        _floatCache[text] = result;
        return result;
    }

    private static bool FloatFits(string text, int max, TMP_FontAsset? font)
    {
        if (text.Length > max) return false;
        if (font == null) return true; // nothing to check against
        foreach (char c in text)
        {
            if (c == ' ') continue;
            try { if (!font.HasCharacter(c, false, false)) return false; }
            catch { return true; }
        }
        return true;
    }

    private static string FoldAccents(string s)
    {
        var sb = new StringBuilder(s.Length);
        foreach (char c in s.Normalize(NormalizationForm.FormD))
            if (CharUnicodeInfo.GetUnicodeCategory(c) != UnicodeCategory.NonSpacingMark) sb.Append(c);
        return sb.ToString().Normalize(NormalizationForm.FormC).Replace('¡', ' ').Trim();
    }

    // ---------------- fonts ----------------

    /// <summary>The game's font of <paramref name="type"/> for the current language (what LabelReferences.UpdateFont
    /// puts on the game's own labels), or null.</summary>
    public static TMP_FontAsset? GameFont(FontType type)
    {
        try
        {
            var datas = LocalizationManager._fontDatas;
            if (datas == null) return null;
            TMP_FontAsset? regular = null;
            for (int i = 0; i < datas.Count; i++)
            {
                var d = datas[i];
                if (d == null || d.Font == null) continue;
                if (d.Type == type) return d.Font;
                if (d.Type == FontType.Regular) regular = d.Font;
            }
            return regular;
        }
        catch { return null; }
    }

    private static readonly HashSet<string> _glyphChecked = new();

    /// <summary>Logs (once per font and language) the characters of the current language's table that
    /// <paramref name="font"/> can't draw, fallbacks included.</summary>
    public static void CheckGlyphs(TMP_FontAsset? font, string where)
    {
        if (font == null) return;
        string id = $"{where}/{font.name}/{Current}";
        if (!_glyphChecked.Add(id)) return;
        try
        {
            var missing = new SortedSet<char>();
            foreach (var row in Table.Values)
            {
                string? s = row[(int)Current];
                if (s == null) continue;
                foreach (char c in s)
                    if (c > 127 && !char.IsWhiteSpace(c) && !missing.Contains(c) && !font.HasCharacter(c, true, true)) missing.Add(c);
            }
            if (missing.Count == 0)
                Plugin.StaticLog.LogInfo($"[SoulstoneCoop][i18n] {where}: font {font.name} draws every {Codes[(int)Current]} character.");
            else
                Plugin.StaticLog.LogWarning($"[SoulstoneCoop][i18n] {where}: font {font.name} lacks {missing.Count} {Codes[(int)Current]} character(s): " +
                                            string.Join(" ", missing.Take(40)) + (missing.Count > 40 ? " ..." : ""));
        }
        catch (Exception ex)
        {
            Plugin.StaticLog.LogWarning($"[SoulstoneCoop][i18n] {where}: glyph check failed: {ex.Message}");
        }
    }

    // ---------------- startup check ----------------

    /// <summary>Every row has English; every floating text fits <see cref="MaxFloatChars"/>; every translation has
    /// the English placeholders.</summary>
    private static void Validate()
    {
        int rows = 0, problems = 0;
        var counts = new int[Codes.Length];
        foreach (var (key, row) in Table)
        {
            rows++;
            if (row.Length != Codes.Length || row[(int)Lang.En] == null)
            {
                problems++;
                Plugin.StaticLog.LogWarning($"[SoulstoneCoop][i18n] '{key}': bad row (needs {Codes.Length} columns and English).");
                continue;
            }
            var holes = Placeholders(row[(int)Lang.En]!);
            for (int l = 0; l < row.Length; l++)
            {
                if (row[l] == null) continue;
                counts[l]++;
                var mine = Placeholders(row[l]!);
                if (!mine.SetEquals(holes))
                {
                    problems++;
                    Plugin.StaticLog.LogWarning($"[SoulstoneCoop][i18n] '{key}' [{Codes[l]}]: placeholders {{{string.Join(",", mine)}}} differ from English {{{string.Join(",", holes)}}}.");
                }
            }
        }
        foreach (var (key, widest) in FloatKeys)
        {
            for (int l = 0; l < Codes.Length; l++)
            {
                string s = Render((Lang)l, key, widest);
                if (s.Length > MaxFloatChars)
                {
                    problems++;
                    Plugin.StaticLog.LogWarning($"[SoulstoneCoop][i18n] Floating text '{key}' [{Codes[l]}] = '{s}' has {s.Length} characters (max {MaxFloatChars}): it would not show.");
                }
            }
        }
        var langs = string.Join(", ", Codes.Select((c, i) => $"{c} {counts[i]}"));
        if (problems == 0) Plugin.StaticLog.LogInfo($"[SoulstoneCoop][i18n] {rows} texts OK ({langs}).");
        else Plugin.StaticLog.LogWarning($"[SoulstoneCoop][i18n] {rows} texts, {problems} problem(s) ({langs}).");
    }

    private static HashSet<string> Placeholders(string s)
    {
        var set = new HashSet<string>();
        int i = 0;
        while ((i = s.IndexOf('{', i)) >= 0)
        {
            int j = s.IndexOf('}', i + 1);
            if (j < 0) break;
            set.Add(s.Substring(i + 1, j - i - 1));
            i = j + 1;
        }
        return set;
    }
}
