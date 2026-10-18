using Il2CppInterop.Runtime;
using UnityEngine;

namespace SoulstoneCoop.Skills;

/// <summary>
/// Raw access to the Nullable&lt;Vector3&gt; fields of SkillActivationParameters. The interop getters/setters
/// for them throw (NRE in Il2CppObjectBase.CreateGCHandle from Il2CppSystem.Nullable`1..ctor: the class pointer
/// for Nullable&lt;Vector3&gt; is never resolved), so we read and write the struct in place. Layout from dump.cs
/// (SkillActivationParameters, TypeDefIndex 1722): Nullable&lt;Vector3&gt; = { bool hasValue @0; Vector3 value @4 },
/// 0x10 bytes each, at SourcePositionOverride 0x0, TargetGroundPosition 0x10, TargetFrontPosition 0x20,
/// TargetDirection 0x30; then references TargetTransform 0x40, TargetEntity 0x48, IsBeingChanneled 0x50,
/// GetTargetFrontPosition 0x58, GetTargetFrontPositionGround 0x60.
/// </summary>
internal static unsafe class ActivationParamsRaw
{
    public const int SourcePositionOverride = 0x00, TargetGroundPosition = 0x10, TargetFrontPosition = 0x20, TargetDirection = 0x30;
    public const int IsBeingChanneled = 0x50, GetTargetFrontPosition = 0x58, GetTargetFrontPositionGround = 0x60;

    private static byte* Data(SkillActivationParameters p) => (byte*)IL2CPP.il2cpp_object_unbox(p.Pointer);

    public static bool TryGetVector(SkillActivationParameters p, int offset, out Vector3 v)
    {
        byte* d = Data(p);
        if (d == null || d[offset] == 0) { v = default; return false; }
        v = *(Vector3*)(d + offset + 4);
        return true;
    }

    public static void SetVector(SkillActivationParameters p, int offset, Vector3 v)
    {
        byte* d = Data(p);
        if (d == null) return;
        *(Vector3*)(d + offset + 4) = v;
        d[offset] = 1;
    }

    /// <summary>Only "is a delegate there", without wrapping it.</summary>
    public static bool HasReference(SkillActivationParameters p, int offset)
    {
        byte* d = Data(p);
        return d != null && *(IntPtr*)(d + offset) != IntPtr.Zero;
    }
}
