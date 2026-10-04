using System;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;
using BepInEx.Unity.IL2CPP.Hook;
using MenuAction = MultiPlatformInputManager.EDigitalActions_MenuControls;

namespace PataCoop.Dev;

/// <summary>
/// Some screens (the mission result) call the generic MultiPlatformInputManager.isActionThisFrame&lt;T&gt;
/// directly instead of the public wrappers we patch with Harmony. Detour the MenuControls
/// instantiation natively so scripted presses reach those screens too.
/// </summary>
public static unsafe class GenericInputHooks
{
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate byte IsActionFn(IntPtr self, int action, int controller, int devices, IntPtr methodInfo);

    private static IsActionFn? _original;
    private static readonly System.Collections.Generic.List<object> Keep = new();

    public static string Install()
    {
        var store = typeof(MultiPlatformInputManager).GetNestedTypes(BindingFlags.NonPublic | BindingFlags.Public)
            .FirstOrDefault(t => t.Name.StartsWith("MethodInfoStoreGeneric_isActionThisFrame_"));
        if (store == null) return "generic store not found";
        var pointer = store.MakeGenericType(typeof(MenuAction)).GetField("Pointer", BindingFlags.Static | BindingFlags.NonPublic | BindingFlags.Public);
        var mi = pointer == null ? IntPtr.Zero : (IntPtr)pointer.GetValue(null)!;
        if (mi == IntPtr.Zero) return "no MenuControls instantiation";
        IntPtr code = *(IntPtr*)mi;
        if (code == IntPtr.Zero) return "instantiation has no code";
        IsActionFn hook = Hook;
        var detour = INativeDetour.CreateAndApply(code, hook, out IsActionFn original);
        _original = original;
        Keep.Add(hook); Keep.Add(detour); Keep.Add(original);
        return $"isActionThisFrame<MenuControls> hooked @0x{code.ToInt64():X}";
    }

    private static byte Hook(IntPtr self, int action, int controller, int devices, IntPtr methodInfo)
    {
        uint bit = InputStats.Bit((MenuAction)action);
        if (bit != 0 && (VirtualPad.Direct(bit) & bit) != 0) return 1;
        return _original!(self, action, controller, devices, methodInfo);
    }
}
