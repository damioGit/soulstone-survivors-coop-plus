using System.Text;
using UnityEngine;

namespace SoulstoneCoop;

/// <summary>
/// Test only ("cmd savemerge A B OUT_DIR"): merges two save slots into one, keeping the best of each: the files are
/// read and written with the game's own serializer (GSSaveFileSystemUtil, the same as its profile load/save), slot A
/// is the base and slot B's progress is folded in: higher numbers win (currencies, materials, weapon levels,
/// prestige, mastery/crusade tiers, map completion, achievement progress, kill totals), and unlock lists are joined
/// (characters, weapons, skill tree nodes, affixes, runes presets of characters A never played). Selections, names
/// and records stay A's. The result goes to OUT_DIR as playerProfile-OUT-*.savgs; the slot files of the game are
/// never touched here, and with SOURCE_DIR they're not even read.
/// </summary>
internal static class SaveMerge
{
    private static readonly SaveFileKind[] Kinds =
    {
        SaveFileKind.Meta, SaveFileKind.Character, SaveFileKind.Currencies, SaveFileKind.UnlockedCharacters,
        SaveFileKind.UnlockedWeapons, SaveFileKind.WeaponPerCharacters, SaveFileKind.SkinPerCharacters, SaveFileKind.Inventory,
        SaveFileKind.SkillTree, SaveFileKind.CompletedCharacterSkillTrees, SaveFileKind.MapProgression, SaveFileKind.GameStats,
        SaveFileKind.GameStatsMatchHistory, SaveFileKind.CharacterProgression, SaveFileKind.AchievementProgression,
        SaveFileKind.RunesPresets, SaveFileKind.CharacterMasteryProgression, SaveFileKind.WeaponsLevel, SaveFileKind.TrackableUnlocks,
        SaveFileKind.PlayerProfileEnabledResources, SaveFileKind.ProgressCheckpoint, SaveFileKind.VoiceOverTriggers,
        SaveFileKind.CharacterCrusadeProgression,
    };

    private static string FileName(SaveFileKind k) => k == SaveFileKind.VoiceOverTriggers ? "playedvoiceovertrigger" : k.ToString().ToLowerInvariant();

    internal static string PathOf(string dir, int slot, SaveFileKind k) => System.IO.Path.Combine(dir, $"playerProfile-{slot}-{FileName(k)}.savgs");

    internal static string Run(string[] a)
    {
        if (a.Length < 4) return "usage: savemerge SLOT_A SLOT_B OUT_SLOT OUT_DIR [SOURCE_DIR]";
        int sa = int.Parse(a[0]), sb = int.Parse(a[1]), so = int.Parse(a[2]);
        string outDir = a[3];
        // SOURCE_DIR: a copy of the save folder (the live one is rewritten by a running game and Steam Cloud).
        string dir = a.Length > 4 ? a[4] : Application.persistentDataPath;
        System.IO.Directory.CreateDirectory(outDir);
        var log = new StringBuilder();
        foreach (var k in Kinds)
        {
            try
            {
                var A = Newest(dir, sa, k, out var fa);
                var B = Newest(dir, sb, k, out var fb);
                if (A == null && B == null) { log.AppendLine($"{k}: in neither"); continue; }
                string what;
                if (A == null) { A = B; what = "only in B, taken"; }
                else if (B == null) what = "only in A, kept";
                else what = Merge(k, A, B);
                GSSaveFileSystemUtil.SerializeToFile(PathOf(outDir, so, k), k, A!);
                // read back: what we wrote must load
                var check = GSSaveFileSystemUtil.DeserializeFromFile(PathOf(outDir, so, k), k);
                log.AppendLine($"{k} [{fa} + {fb}]: {what}{(check == null ? " | READ-BACK FAILED" : "")}");
            }
            catch (Exception ex) { log.AppendLine($"{k}: ERROR {ex.Message}"); }
        }
        var report = log.ToString();
        System.IO.File.WriteAllText(System.IO.Path.Combine(outDir, "merge-report.txt"), report);
        Plugin.StaticLog.LogInfo("[SoulstoneCoop][savemerge]\n" + report);
        return report.Replace("\r", "").Replace("\n", " || ");
    }

    /// <summary>The game keeps each file in 10 copies (base, -1 .. -9), writing them in turn, and loads the one with the
    /// highest Version: the base file isn't the latest.</summary>
    private static SaveVersioning? Newest(string dir, int slot, SaveFileKind k, out string picked)
    {
        SaveVersioning? best = null;
        picked = "-";
        for (int i = 0; i <= 9; i++)
        {
            string path = i == 0 ? PathOf(dir, slot, k) : PathOf(dir, slot, k).Replace(".savgs", $"-{i}.savgs");
            if (!System.IO.File.Exists(path)) continue;
            SaveVersioning? s = null;
            try { s = GSSaveFileSystemUtil.DeserializeFromFile(path, k); } catch { /* a broken copy: skip it, as the game does */ }
            if (s == null || (best != null && s.Version <= best.Version)) continue;
            best = s;
            picked = $"{(i == 0 ? "base" : "-" + i)} v{s.Version}";
        }
        return best;
    }

    private static string Merge(SaveFileKind k, SaveVersioning a, SaveVersioning b)
    {
        switch (k)
        {
            case SaveFileKind.Currencies:
            {
                var x = a.Cast<PlayerProfileCurrencies>(); var y = b.Cast<PlayerProfileCurrencies>();
                string before = $"{x.MinorSoulstones}/{x.CorruptedSoulstones}/{x.VileSoulstones}/{x.WickedSoulstones}/{x.HatefulSoulstones}/{x.RogueSoulstones}";
                x.MinorSoulstones = Math.Max(x.MinorSoulstones, y.MinorSoulstones);
                x.CorruptedSoulstones = Math.Max(x.CorruptedSoulstones, y.CorruptedSoulstones);
                x.VileSoulstones = Math.Max(x.VileSoulstones, y.VileSoulstones);
                x.WickedSoulstones = Math.Max(x.WickedSoulstones, y.WickedSoulstones);
                x.HatefulSoulstones = Math.Max(x.HatefulSoulstones, y.HatefulSoulstones);
                x.RogueSoulstones = Math.Max(x.RogueSoulstones, y.RogueSoulstones);
                return $"A {before}, B {y.MinorSoulstones}/{y.CorruptedSoulstones}/{y.VileSoulstones}/{y.WickedSoulstones}/{y.HatefulSoulstones}/{y.RogueSoulstones} -> " +
                       $"{x.MinorSoulstones}/{x.CorruptedSoulstones}/{x.VileSoulstones}/{x.WickedSoulstones}/{x.HatefulSoulstones}/{x.RogueSoulstones}";
            }
            case SaveFileKind.UnlockedCharacters:
            {
                var x = a.Cast<PlayerProfileUnlockedCharacters>(); var y = b.Cast<PlayerProfileUnlockedCharacters>();
                int n = UnionStrings(x.UnlockedCharacterIds, y.UnlockedCharacterIds, out var added);
                int m = 0;
                foreach (var c in y.UnlockedCharacterIdsEnums) if (!x.UnlockedCharacterIdsEnums.Contains(c)) { x.UnlockedCharacterIdsEnums.Add(c); m++; }
                return $"+{n} ids ({added}), +{m} enums, now {x.UnlockedCharacterIds.Count}";
            }
            case SaveFileKind.UnlockedWeapons:
            {
                var x = a.Cast<PlayerProfileUnlockedWeapons>(); var y = b.Cast<PlayerProfileUnlockedWeapons>();
                int n = UnionStrings(x.UnlockedWeaponIds, y.UnlockedWeaponIds, out var added);
                return $"+{n} ({added}), now {x.UnlockedWeaponIds.Count}";
            }
            case SaveFileKind.WeaponPerCharacters:
            {
                var x = a.Cast<PlayerProfileSelectedWeaponPerCharacter>(); var y = b.Cast<PlayerProfileSelectedWeaponPerCharacter>();
                int n = 0;
                foreach (var e in y.WeaponPerCharacter) if (!Has(x.WeaponPerCharacter, w => w.CharacterId == e.CharacterId)) { x.WeaponPerCharacter.Add(e); n++; }
                return $"+{n} characters' selections";
            }
            case SaveFileKind.SkinPerCharacters:
            {
                var x = a.Cast<PlayerProfileSelectedSkinPerCharacter>(); var y = b.Cast<PlayerProfileSelectedSkinPerCharacter>();
                int n = 0;
                foreach (var e in y.SkinsPerCharacter) if (!Has(x.SkinsPerCharacter, w => w.CharacterId == e.CharacterId)) { x.SkinsPerCharacter.Add(e); n++; }
                return $"+{n} characters' skins";
            }
            case SaveFileKind.Inventory:
            {
                var x = a.Cast<PlayerProfileInventory>(); var y = b.Cast<PlayerProfileInventory>();
                int up = 0, add = 0;
                foreach (var e in y.Items)
                {
                    InventoryItemData? mine = null;
                    foreach (var m in x.Items) if (m.ItemId == e.ItemId) { mine = m; break; }
                    if (mine == null) { x.Items.Add(e); add++; }
                    else if (e.Quantity > mine.Quantity) { mine.Quantity = e.Quantity; up++; }
                }
                return $"{up} raised, {add} added, {x.Items.Count} kinds";
            }
            case SaveFileKind.SkillTree:
            {
                var x = a.Cast<PlayerProfileSkillTree>(); var y = b.Cast<PlayerProfileSkillTree>();
                int up = 0, add = 0, miss = 0;
                // A's nodes by id, read the way the game lists them (its TryGetSkillTreeItemLevel didn't find any of them)
                var mineById = new Dictionary<string, (int index, int level)>();
                for (int i = 0; i < x.GetCount(); i++)
                    if (x.TryGetIndex(i, out int l, out var s) && s != null) mineById[s.Id] = (i, l);
                for (int i = 0; i < y.GetCount(); i++)
                {
                    if (!y.TryGetIndex(i, out int lvl, out var so) || so == null) { miss++; continue; }
                    if (!mineById.TryGetValue(so.Id, out var mine)) { x.Add(lvl, so); mineById[so.Id] = (x.GetCount() - 1, lvl); add++; }
                    else if (lvl > mine.level)
                    {
                        x.RemoveAt(mine.index);
                        x.Add(lvl, so);
                        mineById.Clear();
                        for (int j = 0; j < x.GetCount(); j++)
                            if (x.TryGetIndex(j, out int l2, out var s2) && s2 != null) mineById[s2.Id] = (j, l2);
                        up++;
                    }
                }
                return $"A {x.GetCount() - add} nodes, B {y.GetCount()}: {up} raised, {add} added, {miss} unreadable";
            }
            case SaveFileKind.CompletedCharacterSkillTrees:
            {
                var x = a.Cast<PlayerProfileCompletedCharacterSkillTrees>(); var y = b.Cast<PlayerProfileCompletedCharacterSkillTrees>();
                int n = 0;
                foreach (var c in y.Characters) if (!x.Characters.Contains(c)) { x.Characters.Add(c); n++; }
                return $"+{n}";
            }
            case SaveFileKind.MapProgression:
            {
                var x = a.Cast<PlayerProfileMapProgression>(); var y = b.Cast<PlayerProfileMapProgression>();
                int add = 0, up = 0, aff = 0;
                foreach (var e in y.MapProgression)
                {
                    PlayerProfileMapProgressionData? m = null;
                    foreach (var q in x.MapProgression) if (q.MapId == e.MapId) { m = q; break; }
                    if (m == null) { x.MapProgression.Add(e); add++; continue; }
                    if (e.NormalModeCompleteness > m.NormalModeCompleteness) { m.NormalModeCompleteness = e.NormalModeCompleteness; up++; }
                    aff += UnionStrings(m.CompletedAffixes, e.CompletedAffixes, out _);
                    foreach (var t in e.AffixTierProgression)
                    {
                        PlayerProfileAffixTierProgressionData? mt = null;
                        foreach (var q in m.AffixTierProgression) if (q.Tier == t.Tier) { mt = q; break; }
                        if (mt == null) { m.AffixTierProgression.Add(t); up++; }
                        else if (t.Progression > mt.Progression) { mt.Progression = t.Progression; up++; }
                    }
                }
                return $"{add} maps added, {up} values raised, +{aff} completed affixes";
            }
            case SaveFileKind.GameStats:
            {
                var x = a.Cast<PlayerProfileGameStats>(); var y = b.Cast<PlayerProfileGameStats>();
                x.TotalEnemiesKilled = Math.Max(x.TotalEnemiesKilled, y.TotalEnemiesKilled);
                x.TotalElitesKilled = Math.Max(x.TotalElitesKilled, y.TotalElitesKilled);
                x.TotalBossesKilled = Math.Max(x.TotalBossesKilled, y.TotalBossesKilled);
                x.TotalEnemiesThrownIntoTheVoid = Math.Max(x.TotalEnemiesThrownIntoTheVoid, y.TotalEnemiesThrownIntoTheVoid);
                x.HighestAffixLevelMatch ??= y.HighestAffixLevelMatch;
                x.HighestEndlessLevelMatch ??= y.HighestEndlessLevelMatch;
                x.HighestBossRushLevelMatch ??= y.HighestBossRushLevelMatch;
                x.FastestMatch ??= y.FastestMatch;
                x.FastestMatchMastery ??= y.FastestMatchMastery;
                x.HighestMasteryTierAndDynamicLevel ??= y.HighestMasteryTierAndDynamicLevel;
                x.FastestMatchCrusade ??= y.FastestMatchCrusade;
                x.HighestCrusadeTierAndDynamicLevel ??= y.HighestCrusadeTierAndDynamicLevel;
                return $"totals max (kills {x.TotalEnemiesKilled}, elites {x.TotalElitesKilled}, bosses {x.TotalBossesKilled}); records A's";
            }
            case SaveFileKind.GameStatsMatchHistory:
            {
                var x = a.Cast<PlayerProfileGameStatsMatchHistory>(); var y = b.Cast<PlayerProfileGameStatsMatchHistory>();
                int n = y.MatchesHistoryData.Count + y.MasteryMatchesHistoryData.Count + y.CrusadeMatchesHistoryData.Count;
                foreach (var e in y.MatchesHistoryData) x.MatchesHistoryData.Add(e);
                foreach (var e in y.MasteryMatchesHistoryData) x.MasteryMatchesHistoryData.Add(e);
                foreach (var e in y.CrusadeMatchesHistoryData) x.CrusadeMatchesHistoryData.Add(e);
                return $"+{n} matches of B";
            }
            case SaveFileKind.CharacterProgression:
            {
                var x = a.Cast<PlayerProfileCharacterProgression>(); var y = b.Cast<PlayerProfileCharacterProgression>();
                var sb = new StringBuilder();
                foreach (var e in y.Progress)
                {
                    PlayerProfileCharacterProgressionData? m = null;
                    foreach (var q in x.Progress) if (q.CharacterId == e.CharacterId) { m = q; break; }
                    if (m == null) { x.Progress.Add(e); sb.Append($" +{e.CharacterId}(p{e.CharacterPrestigeLevel})"); continue; }
                    if (e.CharacterPrestigeLevel > m.CharacterPrestigeLevel || (e.CharacterPrestigeLevel == m.CharacterPrestigeLevel && e.CharacterPrestige > m.CharacterPrestige))
                    {
                        sb.Append($" {e.CharacterId} p{m.CharacterPrestigeLevel}->p{e.CharacterPrestigeLevel}");
                        m.CharacterPrestigeLevel = e.CharacterPrestigeLevel;
                        m.CharacterPrestige = e.CharacterPrestige;
                    }
                }
                return sb.Length == 0 ? "A's higher everywhere" : sb.ToString().Trim();
            }
            case SaveFileKind.AchievementProgression:
            {
                var x = a.Cast<PlayerProfileAchievementProgression>(); var y = b.Cast<PlayerProfileAchievementProgression>();
                int add = 0, up = 0;
                foreach (var e in y.Achievements)
                {
                    PlayerProfileAchievementProgressionData? m = null;
                    foreach (var q in x.Achievements) if (q.AchievementId == e.AchievementId) { m = q; break; }
                    if (m == null) { x.Achievements.Add(e); add++; continue; }
                    if (e.Progression > m.Progression) { m.Progression = e.Progression; up++; }
                    if (m.UnlockDateAsTicks == 0 && e.UnlockDateAsTicks != 0) m.UnlockDateAsTicks = e.UnlockDateAsTicks;
                }
                return $"{add} added, {up} raised";
            }
            case SaveFileKind.RunesPresets:
            {
                var x = a.Cast<PlayerProfileRunesPresets>(); var y = b.Cast<PlayerProfileRunesPresets>();
                int n = 0;
                foreach (var e in y.RunesPresets) if (!Has(x.RunesPresets, w => w.CharacterId == e.CharacterId)) { x.RunesPresets.Add(e); n++; }
                return $"+{n} characters' presets";
            }
            case SaveFileKind.CharacterMasteryProgression:
            {
                var x = a.Cast<PlayerProfileMasteryProgression>(); var y = b.Cast<PlayerProfileMasteryProgression>();
                var sb = new StringBuilder();
                foreach (var e in y.Progress)
                {
                    PlayerProfileCharacterMasteryData? m = null;
                    foreach (var q in x.Progress) if (q.CharacterId == e.CharacterId) { m = q; break; }
                    if (m == null) { x.Progress.Add(e); sb.Append($" +{e.CharacterId}"); continue; }
                    if (e.CharacterMasteryDifficultyTier > m.CharacterMasteryDifficultyTier || (e.CharacterMasteryDifficultyTier == m.CharacterMasteryDifficultyTier && e.CharacterMasteryDynamicLevel > m.CharacterMasteryDynamicLevel))
                    {
                        sb.Append($" {e.CharacterId} {m.CharacterMasteryDifficultyTier}/{m.CharacterMasteryDynamicLevel}->{e.CharacterMasteryDifficultyTier}/{e.CharacterMasteryDynamicLevel}");
                        m.CharacterMasteryDifficultyTier = e.CharacterMasteryDifficultyTier;
                        m.CharacterMasteryDynamicLevel = e.CharacterMasteryDynamicLevel;
                    }
                }
                return sb.Length == 0 ? "A's higher everywhere" : sb.ToString().Trim();
            }
            case SaveFileKind.CharacterCrusadeProgression:
            {
                var x = a.Cast<PlayerProfileCrusadeProgression>(); var y = b.Cast<PlayerProfileCrusadeProgression>();
                var sb = new StringBuilder();
                foreach (var e in y.Progress)
                {
                    PlayerProfileCharacterCrusadeData? m = null;
                    foreach (var q in x.Progress) if (q.CharacterId == e.CharacterId) { m = q; break; }
                    if (m == null) { x.Progress.Add(e); sb.Append($" +{e.CharacterId}"); continue; }
                    if (e.DifficultyTier > m.DifficultyTier || (e.DifficultyTier == m.DifficultyTier && e.DynamicLevel > m.DynamicLevel))
                    {
                        sb.Append($" {e.CharacterId} {m.DifficultyTier}/{m.DynamicLevel}->{e.DifficultyTier}/{e.DynamicLevel}");
                        m.DifficultyTier = e.DifficultyTier;
                        m.DynamicLevel = e.DynamicLevel;
                    }
                }
                if (y.OverallMaxTier > x.OverallMaxTier || (y.OverallMaxTier == x.OverallMaxTier && y.OverallMaxDynamicLevel > x.OverallMaxDynamicLevel))
                {
                    x.OverallMaxTier = y.OverallMaxTier;
                    x.OverallMaxDynamicLevel = y.OverallMaxDynamicLevel;
                }
                return sb.Length == 0 ? "A's higher everywhere" : sb.ToString().Trim();
            }
            case SaveFileKind.WeaponsLevel:
            {
                var x = a.Cast<PlayerProfileWeaponsLevel>(); var y = b.Cast<PlayerProfileWeaponsLevel>();
                var sb = new StringBuilder();
                foreach (var e in y.WeaponsLevel)
                {
                    WeaponLevelByWeaponId? m = null;
                    foreach (var q in x.WeaponsLevel) if (q.WeaponId == e.WeaponId) { m = q; break; }
                    if (m == null) { x.WeaponsLevel.Add(e); sb.Append($" +{e.WeaponId}({e.WeaponLevel})"); }
                    else if (e.WeaponLevel > m.WeaponLevel) { sb.Append($" {e.WeaponId} {m.WeaponLevel}->{e.WeaponLevel}"); m.WeaponLevel = e.WeaponLevel; }
                }
                return sb.Length == 0 ? "A's higher everywhere" : sb.ToString().Trim();
            }
            case SaveFileKind.TrackableUnlocks:
            {
                var x = a.Cast<PlayerProfileTrackableUnlocks>(); var y = b.Cast<PlayerProfileTrackableUnlocks>();
                foreach (var v in y.NewMaps) if (!x.NewMaps.Contains(v)) x.NewMaps.Add(v);
                foreach (var v in y.NewCharacters) if (!x.NewCharacters.Contains(v)) x.NewCharacters.Add(v);
                foreach (var v in y.NewCraftables) if (!x.NewCraftables.Contains(v)) x.NewCraftables.Add(v);
                foreach (var v in y.NewRunes) if (!x.NewRunes.Contains(v)) x.NewRunes.Add(v);
                foreach (var v in y.NewAchievements) if (!x.NewAchievements.Contains(v)) x.NewAchievements.Add(v);
                return "'new' badges joined";
            }
            case SaveFileKind.PlayerProfileEnabledResources:
            {
                var x = a.Cast<PlayerProfileResourcesStates>(); var y = b.Cast<PlayerProfileResourcesStates>();
                int n = 0;
                foreach (var e in y.PlayerResourcesStates) if (!Has(x.PlayerResourcesStates, w => w.CharacterId == e.CharacterId)) { x.PlayerResourcesStates.Add(e); n++; }
                return $"+{n} characters";
            }
            case SaveFileKind.ProgressCheckpoint:
            {
                var x = a.Cast<PlayerProfileProgressCheckpoint>(); var y = b.Cast<PlayerProfileProgressCheckpoint>();
                var before = x.ProgressSteps;
                x.ProgressSteps = (PlayerProgressSteps)((int)x.ProgressSteps | (int)y.ProgressSteps);
                return $"{(int)before} | {(int)y.ProgressSteps} -> {(int)x.ProgressSteps}";
            }
            case SaveFileKind.VoiceOverTriggers:
            {
                var x = a.Cast<PlayerProfileVoiceOverTriggers>(); var y = b.Cast<PlayerProfileVoiceOverTriggers>();
                int n = 0;
                foreach (var v in y.PlayedVoiceOverTriggersIds) if (!x.PlayedVoiceOverTriggersIds.Contains(v)) { x.PlayedVoiceOverTriggersIds.Add(v); n++; }
                return $"+{n}";
            }
            case SaveFileKind.Meta:
            {
                var x = a.Cast<PlayerProfileMetadata>(); var y = b.Cast<PlayerProfileMetadata>();
                x.LastPlayedUTCTicks = Math.Max(x.LastPlayedUTCTicks, y.LastPlayedUTCTicks);
                return $"A's ('{x.ProfileName}', cheated={x.IsCheatedSave}), B '{y.ProfileName}' cheated={y.IsCheatedSave}";
            }
            default:
                return "A's kept";
        }
    }

    // ---- "cmd slotcheck N": the game's own profile load of slot N (what it does when you pick that slot), stepped one
    // MoveNext per frame; "cmd slotcheck" then gives the corrupted / restored-from-backup files and a few values ----
    private static Il2CppSystem.Collections.IEnumerator? _check;
    private static PlayerProfile? _checkProfile;
    private static Il2CppSystem.Collections.Generic.List<SaveFileKind>? _corrupted, _backups;
    private static string _checkResult = "not run";

    internal static string Check(string[] a)
    {
        if (a.Length == 0) return _checkResult;
        if (PlayerDataUtil.Default == null) return "no PlayerDataUtil";
        _checkProfile = new PlayerProfile();
        _corrupted = new Il2CppSystem.Collections.Generic.List<SaveFileKind>();
        _backups = new Il2CppSystem.Collections.Generic.List<SaveFileKind>();
        _check = PlayerDataUtil.Default.RequestReadOrCreateNewProfile(int.Parse(a[0]), _checkProfile, _corrupted, _backups);
        _checkResult = "running";
        return "started";
    }

    internal static void Tick()
    {
        if (_check == null) return;
        try
        {
            if (_check.MoveNext()) return;
            var p = _checkProfile!;
            var sb = new StringBuilder();
            sb.Append($"corrupted=[{string.Join(",", Enumerate(_corrupted!))}] fromBackup=[{string.Join(",", Enumerate(_backups!))}]");
            var c = p.Currencies;
            if (c != null) sb.Append($" | soulstones {c.MinorSoulstones}/{c.CorruptedSoulstones}/{c.VileSoulstones}/{c.WickedSoulstones}/{c.HatefulSoulstones}/{c.RogueSoulstones}");
            if (p.UnlockedCharacters != null) sb.Append($" | characters {p.UnlockedCharacters.UnlockedCharacterIds.Count}");
            if (p.UnlockedWeapons != null) sb.Append($" | weapons {p.UnlockedWeapons.UnlockedWeaponIds.Count}");
            if (p.SkillTree != null) sb.Append($" | tree nodes {p.SkillTree.GetCount()}");
            if (p.Inventory != null) sb.Append($" | materials {p.Inventory.Items.Count}");
            if (p.CharacterProgression != null)
                foreach (var e in p.CharacterProgression.Progress) sb.Append($" | {e.CharacterId} p{e.CharacterPrestigeLevel}");
            _checkResult = sb.ToString();
        }
        catch (Exception ex) { _checkResult = "ERROR " + ex.Message; }
        _check = null;
        Plugin.StaticLog.LogInfo("[SoulstoneCoop][savemerge] slotcheck: " + _checkResult);
    }

    /// <summary>"cmd treediff DIR SLOT_A SLOT_B": the skill tree nodes of two slots side by side (id:level).</summary>
    internal static string TreeDiff(string[] a)
    {
        if (a.Length < 3) return "usage: treediff DIR SLOT_A SLOT_B";
        var x = Newest(a[0], int.Parse(a[1]), SaveFileKind.SkillTree, out var fa)?.Cast<PlayerProfileSkillTree>();
        var y = Newest(a[0], int.Parse(a[2]), SaveFileKind.SkillTree, out var fb)?.Cast<PlayerProfileSkillTree>();
        if (x == null || y == null) return "missing";
        string Nodes(PlayerProfileSkillTree t)
        {
            var parts = new List<string>();
            for (int i = 0; i < t.GetCount(); i++)
                parts.Add(t.TryGetIndex(i, out int l, out var so) && so != null ? $"{so.Id}#{so.IdHash}:{l}" : $"?{i}");
            return $"count={t.GetCount()} list={t.UnlockedItems?.Count} [{string.Join(", ", parts)}]";
        }
        return $"A {fa}: {Nodes(x)} || B {fb}: {Nodes(y)}";
    }

    private static IEnumerable<string> Enumerate(Il2CppSystem.Collections.Generic.List<SaveFileKind> l)
    {
        foreach (var k in l) yield return k.ToString();
    }

    private static bool Has<T>(Il2CppSystem.Collections.Generic.List<T> list, Func<T, bool> pred)
    {
        foreach (var e in list) if (pred(e)) return true;
        return false;
    }

    private static int UnionStrings(Il2CppSystem.Collections.Generic.List<string> into, Il2CppSystem.Collections.Generic.List<string> from, out string added)
    {
        var names = new List<string>();
        foreach (var s in from) if (!into.Contains(s)) { into.Add(s); names.Add(s); }
        added = string.Join(",", names.Take(12)) + (names.Count > 12 ? ",..." : "");
        return names.Count;
    }
}
