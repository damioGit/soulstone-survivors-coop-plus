using Il2CppInterop.Runtime.InteropTypes.Arrays;
using UnityEngine;
using UnityEngine.Rendering;

namespace SoulstoneCoop;

/// <summary>
/// Issue #2: the dash's after-image trail on a ghost. On the real player it's DashEffect, a component on the Entity
/// root (StripToVisualOnly removes it from the clone, and it needs ModifiedCharacter._isDashing and a Rigidbody).
/// This is the same thing, visual only: DashEffect.Setup (0x753540) pool, UpdateAll (0x7539F0) spawn and fade,
/// CreateAfterImage (0x752CF0) baked and combined mesh, with the settings of the local Entity's DashEffect (one Entity
/// prefab for every character) and the partner's CharacterDefinition.GetAccentColor. Animator-wise there's nothing to
/// play: no controller has a dash state, the "Dash" bool PlayerAnimator writes has no visible effect. See
/// docs/issue2-dash-investigacao.md.
/// </summary>
internal sealed class GhostDashTrail
{
    private sealed class Template
    {
        public GameObject Prefab = null!;
        public float Interval, Life;
        public int PoolSize, ColorId, FadeId;
        public string ColorProperty = "", FadeProperty = "";
    }

    private struct Image { public GameObject Go; public MeshRenderer Renderer; public float Remaining; }

    private static Template? _template;
    private static bool _templateMissingLogged;
    public static int ImagesShown; // telemetry, every ghost

    private readonly byte _slot;
    private readonly Color _color;
    private GameObject? _holder;
    private Mesh? _combined;
    private MaterialPropertyBlock? _block;
    private readonly List<Mesh> _baked = new();
    private readonly List<Image> _active = new();
    private readonly Stack<Image> _pool = new();
    private float _spawnTimer;
    private bool _wasDashing, _meshReady, _broken, _bakeLogged;

    public GhostDashTrail(byte slot, byte characterId)
    {
        _slot = slot;
        _color = Color.white;
        try { if (characterId != 0) _color = CharacterDefinition.GetAccentColor((CharactersType)characterId); }
        catch (Exception ex) { Plugin.StaticLog.LogWarning($"[SoulstoneCoop][dash] Ghost {slot} accent color: {ex.Message}"); }
    }

    /// <summary>Every frame. <paramref name="speed"/>: the ghost's interpolated planar speed (the game checks its
    /// Rigidbody's). Timers run on Time.deltaTime like DashEffect.UpdateAll, so a pause freezes the images.</summary>
    public void Update(Transform ghostRoot, Transform? modelRoot, bool dashing, float speed)
    {
        if (_broken) return;
        try
        {
            // The images live in the scene (the game instantiates them without a parent): a scene change took them.
            if (_holder is not null && _holder == null) Forget();
            float dt = Time.deltaTime;
            _spawnTimer -= dt;
            // The game re-bakes only when its renderers change, so one dash shows one pose. Here once per dash.
            if (dashing && !_wasDashing) _meshReady = modelRoot != null && EnsurePool() && Rebake(ghostRoot, modelRoot);
            _wasDashing = dashing;
            if (dashing && _meshReady && _spawnTimer < 0f && speed > 0.1f)
            {
                _spawnTimer = _template!.Interval;
                Spawn(ghostRoot);
            }
            Fade(dt);
        }
        catch (Exception ex)
        {
            _broken = true;
            Plugin.StaticLog.LogWarning($"[SoulstoneCoop][dash] Ghost {_slot} dash trail off: {ex.Message}");
            Dispose();
        }
    }

    /// <summary>The ghost is gone or rebuilt: its images and meshes go too.</summary>
    public void Dispose()
    {
        try
        {
            if (_holder != null) UnityEngine.Object.Destroy(_holder);
            foreach (var m in _baked) if (m != null) UnityEngine.Object.Destroy(m);
            if (_combined != null) UnityEngine.Object.Destroy(_combined);
        }
        catch { /* the scene may already have taken them */ }
        _baked.Clear();
        _combined = null;
        Forget();
    }

    private void Forget()
    {
        _holder = null;
        _active.Clear();
        _pool.Clear();
        _meshReady = false;
    }

    /// <summary>The local Entity's DashEffect: same prefab for every player, so its settings are everybody's.</summary>
    private static Template? GetTemplate()
    {
        if (_template != null && _template.Prefab != null) return _template;
        var local = GameState.LocalPlayerEntity;
        var fx = local != null ? local.gameObject.GetComponent<DashEffect>() : null;
        if (fx == null || fx.DashEffectPrefab == null)
        {
            if (!_templateMissingLogged)
            {
                _templateMissingLogged = true;
                Plugin.StaticLog.LogWarning("[SoulstoneCoop][dash] No DashEffect on the local player (yet): ghost dash trails wait for it.");
            }
            return null;
        }
        var t = new Template
        {
            Prefab = fx.DashEffectPrefab,
            Interval = fx.TimeBetweenImages,
            Life = fx.FadeSpeed > 0.01f ? fx.FadeSpeed : 0.3f,
            PoolSize = Mathf.Clamp(fx.PoolSize, 1, 64),
            ColorProperty = fx.ColorProperty ?? "",
            FadeProperty = fx.FadeProperty ?? "",
        };
        t.ColorId = Shader.PropertyToID(t.ColorProperty);
        t.FadeId = Shader.PropertyToID(t.FadeProperty);
        Plugin.StaticLog.LogInfo($"[SoulstoneCoop][dash] Dash trail template: prefab '{t.Prefab.name}', pool {t.PoolSize}, " +
            $"every {t.Interval:0.###} s, life {t.Life:0.###} s, color '{t.ColorProperty}', fade '{t.FadeProperty}'.");
        return _template = t;
    }

    /// <summary>DashEffect.Setup: PoolSize copies of the prefab sharing one combined mesh, tinted once, inactive.</summary>
    private bool EnsurePool()
    {
        if (_holder != null) return true;
        var t = GetTemplate();
        if (t == null) return false;
        _holder = new GameObject($"SoulstoneCoop_DashTrail{_slot}");
        if (_combined == null) _combined = new Mesh { name = $"SoulstoneCoop_DashTrail{_slot}", indexFormat = IndexFormat.UInt32 };
        _block ??= new MaterialPropertyBlock();
        for (int i = 0; i < t.PoolSize; i++)
        {
            var go = UnityEngine.Object.Instantiate(t.Prefab, _holder.transform);
            var mf = go.GetComponent<MeshFilter>();
            var mr = go.GetComponent<MeshRenderer>();
            if (mf == null || mr == null) throw new InvalidOperationException($"prefab '{t.Prefab.name}' has no MeshFilter/MeshRenderer");
            mf.sharedMesh = _combined;
            mr.GetPropertyBlock(_block);
            _block.SetColor(t.ColorId, _color);
            mr.SetPropertyBlock(_block);
            go.SetActive(false);
            _pool.Push(new Image { Go = go, Renderer = mr });
        }
        return true;
    }

    /// <summary>CreateAfterImage's mesh: the model's SkinnedMeshRenderers baked (useScale) and its MeshRenderers' meshes,
    /// in the ghost root's local space, combined into one. Only the model subtree (as the game's PlayerPrefabReferences
    /// arrays), so the ground ring and the name tag stay out.</summary>
    private bool Rebake(Transform ghostRoot, Transform modelRoot)
    {
        var toLocal = ghostRoot.worldToLocalMatrix;
        var parts = new List<CombineInstance>();
        int skinned = 0, meshes = 0, skipped = 0;
        foreach (var smr in modelRoot.GetComponentsInChildren<SkinnedMeshRenderer>())
        {
            if (smr == null || smr.sharedMesh == null) { skipped++; continue; }
            if (_baked.Count <= skinned) _baked.Add(new Mesh());
            var mesh = _baked[skinned];
            smr.BakeMesh(mesh, true);
            parts.Add(Part(mesh, toLocal * smr.localToWorldMatrix));
            skinned++;
        }
        foreach (var mr in modelRoot.GetComponentsInChildren<MeshRenderer>())
        {
            var mf = mr != null ? mr.GetComponent<MeshFilter>() : null;
            var mesh = mf != null ? mf.sharedMesh : null;
            // A mesh without Read/Write can't be combined (Unity refuses the whole CombineMeshes).
            if (mesh == null || !mesh.isReadable) { skipped++; continue; }
            parts.Add(Part(mesh, toLocal * mr!.transform.localToWorldMatrix));
            meshes++;
        }
        if (!_bakeLogged)
        {
            _bakeLogged = true;
            Plugin.StaticLog.LogInfo($"[SoulstoneCoop][dash] Ghost {_slot} dash trail: {skinned} skinned + {meshes} mesh renderer(s)" +
                $"{(skipped > 0 ? $", {skipped} skipped" : "")}, accent {_color}.");
        }
        if (parts.Count == 0) return false;
        var arr = new Il2CppStructArray<CombineInstance>(parts.Count);
        for (int i = 0; i < parts.Count; i++) arr[i] = parts[i];
        _combined!.Clear();
        _combined.CombineMeshes(arr);
        return true;
    }

    // Straight into the fields, as CombineInstance's own setters do: no interop call on a struct.
    private static CombineInstance Part(Mesh mesh, Matrix4x4 m) =>
        new CombineInstance { m_MeshInstanceID = mesh.GetInstanceID(), m_SubMeshIndex = 0, m_Transform = m };

    /// <summary>One image from the pool at the ghost root's position, rotation and scale; none left = none shown.</summary>
    private void Spawn(Transform ghostRoot)
    {
        var t = _template!;
        while (_pool.Count > 0)
        {
            var img = _pool.Pop();
            if (img.Go == null || img.Renderer == null) continue;
            img.Renderer.GetPropertyBlock(_block);
            _block!.SetFloat(t.FadeId, 1f);
            img.Renderer.SetPropertyBlock(_block);
            var tr = img.Go.transform;
            tr.position = ghostRoot.position;
            tr.rotation = ghostRoot.rotation;
            tr.localScale = ghostRoot.localScale;
            img.Go.SetActive(true);
            img.Remaining = t.Life;
            _active.Add(img);
            ImagesShown++;
            return;
        }
    }

    /// <summary>UpdateAll's fade: FadeProperty = remaining / life, back to the pool at zero.</summary>
    private void Fade(float dt)
    {
        if (_active.Count == 0) return;
        var t = _template!;
        for (int i = _active.Count - 1; i >= 0; i--)
        {
            var img = _active[i];
            if (img.Go == null || img.Renderer == null) { _active.RemoveAt(i); continue; }
            img.Remaining -= dt;
            img.Renderer.GetPropertyBlock(_block);
            _block!.SetFloat(t.FadeId, img.Remaining / t.Life);
            img.Renderer.SetPropertyBlock(_block);
            if (img.Remaining <= 0f)
            {
                img.Go.SetActive(false);
                _active.RemoveAt(i);
                _pool.Push(img);
            }
            else _active[i] = img;
        }
    }
}
