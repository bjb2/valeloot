using System;
using BepInEx.Unity.IL2CPP.Hook;

namespace ValeLoot;

/// <summary>
/// The one place that touches BepInEx's detour API.
///
/// It exists as a shim for a specific reason: BepInEx 6 is a bleeding-edge (`-be.*`) dependency and
/// its hooking surface has moved more than once — MonoMod NativeDetour, then INativeDetour, and the
/// underlying MonoMod major has its own churn (this build resolves MonoMod.RuntimeDetour 22.7.31.1).
/// Every call site in InventoryPaint and TooltipInject goes through Apply/Undo, so a BepInEx bump is
/// a two-method edit here instead of a hunt through the hook bodies.
/// </summary>
internal static class Detours
{
    /// <summary>Detour <paramref name="target"/> to <paramref name="hook"/>, yielding a trampoline to the original.</summary>
    public static object Apply<T>(IntPtr target, T hook, out T? original) where T : Delegate
    {
        INativeDetour detour = INativeDetour.CreateAndApply(target, hook, out T orig);
        original = orig;
        return detour;
    }

    /// <summary>Undo a detour created by <see cref="Apply"/>. Safe to call on a null/failed handle.</summary>
    public static void Undo(ref object? handle)
    {
        if (handle is IDisposable disposable)
        {
            try { disposable.Dispose(); } catch { /* teardown must never throw */ }
        }
        handle = null;
    }
}
