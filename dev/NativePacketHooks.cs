using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;
using BepInEx.Unity.IL2CPP.Hook;
using Il2CppInterop.Runtime.Runtime;

namespace PataCoop.Dev;

/// <summary>
/// Native detours for the game's packet functions. HarmonyX mis-marshals their
/// SecurePacket&amp; parameter on IL2CPP, so we hook the native entry points with our
/// own signatures and dereference the packet pointer ourselves.
/// </summary>
public static unsafe class NativePacketHooks
{
    // bool queuePacket(SecurePacket& packet, uint dstId)  -> native: (this, SecurePacket** , uint, MethodInfo*)
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate byte QueuePacketFn(IntPtr self, IntPtr* packetRef, uint dstId, IntPtr methodInfo);

    // void callbackReceive(SecurePacket& packet, uint srcId)
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate void CallbackFn(IntPtr self, IntPtr* packetRef, uint peerId, IntPtr methodInfo);

    private static readonly List<object> Keep = new(); // detours + delegates must stay alive
    private static readonly Dictionary<string, QueuePacketFn> QueueOriginals = new();
    private static CallbackFn? _receiveOriginal;

    /// <summary>Optional interceptors used by the co-op bridge; return true to swallow the call.</summary>
    public static Func<string, P2.GameSystem.Network.SecurePacket, uint, bool>? OnQueue;

    public static string Install()
    {
        var done = new List<string>();
        // Not the Mock: its queuePacket is a trivial "return true" that the linker folded together with
        // unrelated functions (identical COMDAT folding), so detouring it intercepts foreign calls.
        foreach (var t in new[] { typeof(P2.GameSystem.Network.SecureTransceiverHost), typeof(P2.GameSystem.Network.SecureTransceiverClient) })
        {
            IntPtr target = NativePointer(t, "queuePacket");
            if (target == IntPtr.Zero) { done.Add($"{t.Name}.queuePacket: not found"); continue; }
            string name = t.Name;
            QueuePacketFn hook = (self, pkt, dst, mi) => QueueHook(name, self, pkt, dst, mi);
            var detour = INativeDetour.CreateAndApply(target, hook, out QueuePacketFn original);
            QueueOriginals[name] = original;
            Keep.Add(hook); Keep.Add(detour); Keep.Add(original);
            done.Add($"{name}.queuePacket hooked @0x{target.ToInt64():X}");
        }

        // ProtocolBase.callbackReceive is a default (likely folded) implementation too; leave it alone.
        IntPtr recv = IntPtr.Zero;
        if (recv != IntPtr.Zero)
        {
            CallbackFn hook = ReceiveHook;
            var detour = INativeDetour.CreateAndApply(recv, hook, out CallbackFn original);
            _receiveOriginal = original;
            Keep.Add(hook); Keep.Add(detour); Keep.Add(original);
            done.Add($"ProtocolBase.callbackReceive hooked @0x{recv.ToInt64():X}");
        }
        return string.Join("; ", done);
    }

    /// <summary>Native code pointer of an interop method, via its NativeMethodInfoPtr_* field.</summary>
    internal static IntPtr NativePointer(Type interopType, string methodName)
    {
        var field = interopType.GetFields(BindingFlags.NonPublic | BindingFlags.Static)
            .FirstOrDefault(f => f.Name.StartsWith("NativeMethodInfoPtr_" + methodName + "_"));
        if (field == null) return IntPtr.Zero;
        var methodInfo = (IntPtr)field.GetValue(null)!;
        if (methodInfo == IntPtr.Zero) return IntPtr.Zero;
        return UnityVersionHandler.Wrap((Il2CppMethodInfo*)methodInfo).MethodPointer;
    }

    [DllImport("kernel32.dll")] private static extern bool IsBadReadPtr(IntPtr p, UIntPtr size);
    private static bool Readable(IntPtr p, int size) => p != IntPtr.Zero && !IsBadReadPtr(p, (UIntPtr)size);
    private static IntPtr _packetClass;
    private static int _rawDumps;

    /// <summary>Describe a pointer without creating interop objects (no risk of access violations).</summary>
    private static string Probe(IntPtr p)
    {
        if (!Readable(p, 32)) return $"0x{p.ToInt64():X}(unreadable)";
        IntPtr klass = *(IntPtr*)p;
        if (_packetClass == IntPtr.Zero) _packetClass = Il2CppInterop.Runtime.Il2CppClassPointerStore<P2.GameSystem.Network.SecurePacket>.NativeClassPtr;
        int id = *(int*)(p + 16); uint dst = *(uint*)(p + 20); IntPtr data = *(IntPtr*)(p + 24);
        return $"0x{p.ToInt64():X} klass{(klass == _packetClass ? "=SecurePacket" : $"=0x{klass.ToInt64():X}")} id={id} dst={dst} data=0x{data.ToInt64():X}{(Readable(data, 24) ? $"(len {*(long*)(data + 24)})" : "")}";
    }

    private static byte QueueHook(string who, IntPtr self, IntPtr* packetRef, uint dstId, IntPtr mi)
    {
        if (_rawDumps < 40)
        {
            _rawDumps++;
            IntPtr asIs = (IntPtr)packetRef;
            IntPtr deref = Readable(asIs, 8) ? *packetRef : IntPtr.Zero;
            NetLog.Note($"{who}.queuePacket raw", $"{who}.queuePacket dst={dstId} | arg-as-object: {Probe(asIs)} | arg-dereferenced: {Probe(deref)}");
        }
        else NetLog.Note($"{who}.queuePacket", $"{who}.queuePacket dst={dstId}");
        return QueueOriginals[who](self, packetRef, dstId, mi);
    }

    private static void ReceiveHook(IntPtr self, IntPtr* packetRef, uint srcId, IntPtr mi)
    {
        IntPtr asIs = (IntPtr)packetRef;
        IntPtr deref = Readable(asIs, 8) ? *packetRef : IntPtr.Zero;
        NetLog.Note("callbackReceive", $"callbackReceive src={srcId} | as-object: {Probe(asIs)} | dereferenced: {Probe(deref)}");
        _receiveOriginal!(self, packetRef, srcId, mi);
    }
}
