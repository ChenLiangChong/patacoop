using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net;
using System.Reflection;
using System.Text;
using BepInEx;
using BepInEx.Logging;
using BepInEx.Unity.IL2CPP;
using Il2CppInterop.Runtime.Injection;
using Microsoft.CodeAnalysis.CSharp.Scripting;
using Microsoft.CodeAnalysis.Scripting;
using UnityEngine;

namespace PataCoop.Dev;

/// <summary>
/// Development harness: a localhost-only HTTP endpoint that runs C# snippets on the
/// Unity main thread, takes screenshots and reports which instance this is.
/// Port 9100 for the first game copy, 9101 for the second, and so on.
/// </summary>
[BepInPlugin("com.patacoop.dev", "PataCoop.Dev", "0.1.0")]
public sealed class DevPlugin : BasePlugin
{
    internal static ManualLogSource L = null!;
    public static int Port { get; private set; }
    public static int Instance => Port - 9100;

    public override void Load()
    {
        L = Log;
        ClassInjector.RegisterTypeInIl2Cpp<MainThreadPump>();
        AddComponent<MainThreadPump>();
        // Claim the instance number (HTTP port) first: the save sandbox keys off it.
        Port = DevServer.Start();
        new HarmonyLib.Harmony("com.patacoop.dev").PatchAll(typeof(DevPlugin).Assembly);
        if (System.IO.File.Exists(System.IO.Path.Combine(BepInEx.Paths.PluginPath, "PataCoop.Dev", "nethooks.txt")))
        try { L.LogMessage("native packet hooks: " + NativePacketHooks.Install()); }
        catch (Exception e) { L.LogError("native packet hooks failed: " + e); }
        try { L.LogMessage("generic input hooks: " + GenericInputHooks.Install()); }
        catch (Exception e) { L.LogError("generic input hooks failed: " + e.Message); }
        L.LogMessage(Port > 0
            ? $"PataCoop.Dev listening on http://127.0.0.1:{Port}/ (instance {Instance}, pid {Environment.ProcessId})"
            : "PataCoop.Dev could not open a port in 9100-9107");
    }
}

/// <summary>Runs queued work on the Unity main thread once per frame.</summary>
public sealed class MainThreadPump : MonoBehaviour
{
    private static readonly ConcurrentQueue<Action> Queue = new();
    public static int Frame { get; private set; }

    public MainThreadPump(IntPtr ptr) : base(ptr) { }

    public static Task<T> Run<T>(Func<T> work)
    {
        var tcs = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
        Queue.Enqueue(() =>
        {
            try { tcs.SetResult(work()); }
            catch (Exception e) { tcs.SetException(e); }
        });
        return tcs.Task;
    }

    private void Update()
    {
        Frame++;
        if (Frame == 30 && !File.Exists(Path.Combine(BepInEx.Paths.PluginPath, "PataCoop.Dev", "unmute.txt")))
            AudioListener.volume = 0f; // dev copies stay silent unless unmute.txt exists
        int budget = 32;
        while (budget-- > 0 && Queue.TryDequeue(out var job)) job();
        Pending.Tick();
        SaveSandbox.Tick();
    }
}

/// <summary>Per-frame callbacks scripts can register (e.g. hold a key for N frames).</summary>
public static class Pending
{
    private static readonly List<Func<bool>> Jobs = new();
    public static void Add(Func<bool> stepReturnsTrueWhenDone) { lock (Jobs) Jobs.Add(stepReturnsTrueWhenDone); }
    internal static void Tick()
    {
        lock (Jobs)
        {
            for (int i = Jobs.Count - 1; i >= 0; i--)
            {
                bool done;
                try { done = Jobs[i](); }
                catch (Exception e) { DevPlugin.L.LogWarning("pending job failed: " + e.Message); done = true; }
                if (done) Jobs.RemoveAt(i);
            }
        }
    }
}

/// <summary>Members visible to scripts without qualification.</summary>
public class Globals
{
    public StringBuilder Out { get; } = new();
    public void Print(object? o) => Out.AppendLine(o?.ToString() ?? "null");
    public int Instance => DevPlugin.Instance;
    public int Frame => MainThreadPump.Frame;
    public Dictionary<string, object?> Vars => DevServer.Vars;

    /// <summary>Hold buttons (VirtualPad bit constants) for some frames, starting after a delay.</summary>
    public string Press(uint mask, int hold = 6, int delay = 1) { VirtualPad.Press(mask, hold, delay); return $"press 0x{mask:X} @frame {Time.frameCount + delay}"; }
    public string Seq(int gap, params uint[] masks) { VirtualPad.Sequence(masks, 6, gap); return $"queued {masks.Length} presses"; }

    /// <summary>Find a managed (interop or plain) type by full or simple name.</summary>
    public Type? T(string name) => DevServer.FindType(name);

    /// <summary>Public + private instance/static members of a type, one per line, filtered.</summary>
    public string Members(string typeName, string filter = "")
    {
        var t = T(typeName) ?? throw new ArgumentException("no type " + typeName);
        const BindingFlags all = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly;
        return string.Join("\n", t.GetMembers(all)
            .Where(m => m.MemberType is MemberTypes.Method or MemberTypes.Property or MemberTypes.Field)
            .Select(m => m switch
            {
                MethodInfo mi => $"M {(mi.IsStatic ? "static " : "")}{mi.ReturnType.Name} {mi.Name}({string.Join(", ", mi.GetParameters().Select(p => p.ParameterType.Name + " " + p.Name))})",
                PropertyInfo pi => $"P {pi.PropertyType.Name} {pi.Name}",
                FieldInfo fi => $"F {(fi.IsStatic ? "static " : "")}{fi.FieldType.Name} {fi.Name}",
                _ => m.Name
            })
            .Where(s => !s.Contains("NativeMethodInfoPtr") && !s.Contains("NativeFieldInfoPtr") && !s.Contains("NativeClassPtr"))
            .Where(s => filter.Length == 0 || s.Contains(filter, StringComparison.OrdinalIgnoreCase))
            .Distinct());
    }
}

public static class DevServer
{
    public static readonly Dictionary<string, object?> Vars = new();
    private static ScriptOptions? _options;
    private static readonly Dictionary<string, Type?> TypeCache = new();

    public static int Start()
    {
        for (int port = 9100; port < 9108; port++)
        {
            var listener = new HttpListener();
            listener.Prefixes.Add($"http://127.0.0.1:{port}/");
            try { listener.Start(); }
            catch (HttpListenerException) { continue; }
            var thread = new Thread(() => Serve(listener)) { IsBackground = true, Name = "PataCoop.Dev http" };
            thread.Start();
            return port;
        }
        return -1;
    }

    public static Type? FindType(string name)
    {
        lock (TypeCache)
        {
            if (TypeCache.TryGetValue(name, out var cached)) return cached;
            Type? found = null;
            foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
            {
                if (asm.IsDynamic) continue;
                found = asm.GetType(name, false);
                if (found != null) break;
            }
            if (found == null)
            {
                foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
                {
                    if (asm.IsDynamic) continue;
                    Type[] types;
                    try { types = asm.GetTypes(); } catch (ReflectionTypeLoadException e) { types = e.Types.Where(t => t != null).ToArray()!; }
                    found = types.FirstOrDefault(t => t.Name == name || t.FullName?.Replace('+', '.') == name);
                    if (found != null) break;
                }
            }
            TypeCache[name] = found;
            return found;
        }
    }

    private static void Serve(HttpListener listener)
    {
        while (listener.IsListening)
        {
            HttpListenerContext ctx;
            try { ctx = listener.GetContext(); }
            catch { break; }
            ThreadPool.QueueUserWorkItem(_ => Handle(ctx));
        }
    }

    private static void Handle(HttpListenerContext ctx)
    {
        string reply;
        int status = 200;
        try
        {
            string path = ctx.Request.Url!.AbsolutePath.TrimEnd('/');
            string body = new StreamReader(ctx.Request.InputStream, Encoding.UTF8).ReadToEnd();
            reply = path switch
            {
                "/ping" => $"instance={DevPlugin.Instance} port={DevPlugin.Port} pid={Environment.ProcessId} frame={MainThreadPump.Frame}",
                "/eval" => Eval(body),
                "/shot" => Shot(ctx.Request.QueryString["path"] ?? Path.Combine(System.Environment.GetEnvironmentVariable("PATACOOP_WORK_WIN") ?? System.IO.Path.Combine(System.Environment.GetFolderPath(System.Environment.SpecialFolder.UserProfile), "PataCoop"), "shots", $"i{DevPlugin.Instance}.png")),
                _ => throw new InvalidOperationException("endpoints: /ping /eval /shot?path=")
            };
        }
        catch (Exception e)
        {
            status = 500;
            reply = e.ToString();
        }
        var bytes = Encoding.UTF8.GetBytes(reply);
        ctx.Response.StatusCode = status;
        ctx.Response.ContentType = "text/plain; charset=utf-8";
        ctx.Response.OutputStream.Write(bytes);
        ctx.Response.Close();
    }

    private static ScriptOptions Options()
    {
        if (_options != null) return _options;
        var refs = AppDomain.CurrentDomain.GetAssemblies()
            .Where(a => !a.IsDynamic && !string.IsNullOrEmpty(a.Location))
            .GroupBy(a => a.GetName().Name).Select(g => g.First());
        _options = ScriptOptions.Default
            .WithReferences(refs)
            .WithImports("System", "System.Linq", "System.Collections.Generic", "System.Reflection", "System.Text", "UnityEngine", "PataCoop.Dev")
            .WithAllowUnsafe(true);
        return _options;
    }

    private static string Eval(string code)
    {
        var sw = Stopwatch.StartNew();
        var script = CSharpScript.Create<object>(code, Options(), typeof(Globals));
        var diags = script.Compile().Where(d => d.Severity == Microsoft.CodeAnalysis.DiagnosticSeverity.Error).ToList();
        if (diags.Count > 0) return "COMPILE ERROR\n" + string.Join("\n", diags);
        var run = script.CreateDelegate();
        long compileMs = sw.ElapsedMilliseconds;
        var globals = new Globals();
        var task = MainThreadPump.Run<object?>(() =>
        {
            try
            {
                var t = run(globals);
                return t.IsCompleted ? t.Result : "(script is still running asynchronously)";
            }
            catch (Exception e)
            {
                var inner = e is AggregateException ae ? ae.Flatten().InnerException ?? e : e;
                return "EXCEPTION " + inner;
            }
        });
        if (!task.Wait(TimeSpan.FromSeconds(30))) return globals.Out + "TIMEOUT waiting for the main thread (is the game frozen or minimised?)";
        object? result = task.Result;
        return $"{globals.Out}{Format(result)}\n[compile {compileMs} ms, total {sw.ElapsedMilliseconds} ms]";
    }

    private static string Format(object? o) => o switch
    {
        null => "null",
        string s => s,
        System.Collections.IEnumerable e => string.Join("\n", e.Cast<object?>().Select(x => x?.ToString() ?? "null")),
        _ => o.ToString() ?? ""
    };

    private static string Shot(string path)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        if (File.Exists(path)) File.Delete(path);
        MainThreadPump.Run(() => { ScreenCapture.CaptureScreenshot(path); return 0; }).Wait(5000);
        var until = DateTime.UtcNow.AddSeconds(5);
        while (DateTime.UtcNow < until && !File.Exists(path)) Thread.Sleep(50);
        Thread.Sleep(100);
        return File.Exists(path) ? path : "screenshot not written (window minimised?)";
    }
}
