using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using HarmonyLib;

namespace PataCoop.Dev;

/// <summary>
/// Research tracing for the game's own netcode: which transceiver gets built, what
/// packets protocols queue, and what they are handed on receive. Read with NetLog.Take().
/// </summary>
public static class NetLog
{
    private static readonly List<string> Lines = new();
    private static readonly Dictionary<string, int> Counts = new();
    public static int MaxDetailed = 400;

    internal static void Note(string key, string detail)
    {
        lock (Lines)
        {
            Counts[key] = Counts.TryGetValue(key, out var n) ? n + 1 : 1;
            if (Lines.Count < MaxDetailed) Lines.Add($"[f{UnityEngine.Time.frameCount}] {detail}");
        }
    }

    public static string Take()
    {
        lock (Lines)
        {
            var s = string.Join("\n", Lines) + "\n-- counts --\n" +
                    string.Join("\n", Counts.OrderByDescending(kv => kv.Value).Select(kv => $"{kv.Value,7} {kv.Key}"));
            Lines.Clear(); Counts.Clear();
            return s;
        }
    }

    internal static string Hex(Il2CppInterop.Runtime.InteropTypes.Arrays.Il2CppStructArray<uint>? data, int max = 8)
    {
        if (data == null) return "null";
        int n = Math.Min(max, data.Length);
        var words = new string[n];
        for (int i = 0; i < n; i++) words[i] = data[i].ToString("X8");
        return $"{data.Length}w {string.Join(" ", words)}{(data.Length > n ? " …" : "")}";
    }
}

// Disabled: HarmonyX passes a bad pointer for the SecurePacket& parameter on IL2CPP (AccessViolation).
// Packet capture is done with a native detour instead (NativePacketHooks).
internal static class TraceQueuePacket
{
    static IEnumerable<MethodBase> TargetMethods() =>
        new[] { typeof(P2.GameSystem.Network.SecureTransceiverHost), typeof(P2.GameSystem.Network.SecureTransceiverClient), typeof(P2.GameSystem.Network.SecureTransceiverMock) }
            .Select(t => t.GetMethod("queuePacket", BindingFlags.Public | BindingFlags.Instance))
            .Where(m => m != null)!;

    static void Prefix(object __instance, ref P2.GameSystem.Network.SecurePacket securePacket, uint dstId, MethodBase __originalMethod)
    {
        string who = __originalMethod.DeclaringType!.Name;
        NetLog.Note($"{who}.queuePacket id={securePacket.securePacketId}", $"{who}.queuePacket id={securePacket.securePacketId} pktDst={securePacket.dstId} dst={dstId} data={NetLog.Hex(securePacket.data)}");
    }
}

internal static class TraceProtocolCallbacks
{
    static IEnumerable<MethodBase> TargetMethods() =>
        new[] { "callbackSend", "callbackReceive" }
            .Select(n => typeof(P2.GameSystem.Network.ProtocolBase).GetMethod(n, BindingFlags.Public | BindingFlags.Instance))
            .Where(m => m != null)!;

    static void Prefix(object __instance, ref P2.GameSystem.Network.SecurePacket securePacket, MethodBase __originalMethod)
    {
        string proto = ((Il2CppInterop.Runtime.InteropTypes.Il2CppObjectBase)__instance).GetType().Name;
        NetLog.Note($"{proto}.{__originalMethod.Name} id={securePacket.securePacketId}",
            $"{proto}.{__originalMethod.Name} id={securePacket.securePacketId} data={NetLog.Hex(securePacket.data)}");
    }
}

[HarmonyPatch]
internal static class TraceControllerInit
{
    static IEnumerable<MethodBase> TargetMethods() =>
        new[] { "initialize", "initializeAdhoc", "terminate" }
            .Select(n => typeof(P2.GameSystem.Network.Controller).GetMethod(n, BindingFlags.Public | BindingFlags.Instance, null, Type.EmptyTypes, null))
            .Where(m => m != null)!;

    static void Postfix(P2.GameSystem.Network.Controller __instance, MethodBase __originalMethod)
    {
        string tx;
        try { tx = __instance.getSecureTransceiver()?.GetIl2CppType().FullName ?? "null"; } catch (Exception e) { tx = "?" + e.Message; }
        NetLog.Note($"Controller.{__originalMethod.Name}", $"Controller.{__originalMethod.Name} -> transceiver {tx}, host={SafeHost(__instance)}");
    }

    private static string SafeHost(P2.GameSystem.Network.Controller c) { try { return c.isHost().ToString(); } catch { return "?"; } }
}
