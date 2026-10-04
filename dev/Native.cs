using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using Iced.Intel;
using Decoder = Iced.Intel.Decoder;
using Il2CppInterop.Runtime;

namespace PataCoop.Dev;

/// <summary>
/// In-process reverse engineering of the game's native code. Disassembles IL2CPP methods,
/// labelling call targets, vtable slots, globals (type infos, method infos, string literals)
/// and field accesses with names read from the live runtime, and finds who calls or
/// references a function. Research-only; nothing here is used by the co-op plugin.
/// </summary>
public static unsafe class Native
{
    private const int VTableOffset = 0x138, VTableEntry = 16, StaticFieldsOffset = 0xB8;
    private const int MethodStatic = 0x10, FieldStatic = 0x10, FieldLiteral = 0x40;

    private sealed class Method
    {
        public IntPtr Info, Klass;
        public string Name = "";
        public ulong Code;
        public bool IsStatic;
        public int Params;
        private IntPtr _ret = (IntPtr)(-1);
        /// <summary>Class of the returned object, or zero for value types / void.</summary>
        public IntPtr Ret
        {
            get
            {
                if (_ret != (IntPtr)(-1)) return _ret;
                IntPtr rt = IL2CPP.il2cpp_method_get_return_type(Info);
                IntPtr rc = rt == IntPtr.Zero ? IntPtr.Zero : IL2CPP.il2cpp_class_from_type(rt);
                return _ret = rc != IntPtr.Zero && !IL2CPP.il2cpp_class_is_valuetype(rc) ? rc : IntPtr.Zero;
            }
        }
        public override string ToString() => $"{Name}/{Params}";
    }

    private sealed record FieldRec(int Offset, string Name, IntPtr TypeClass, bool ValueType, IntPtr Owner);

    private enum TK { Obj, TypeInfo, Statics, KlassOf, VFunc }
    private readonly record struct Track(IntPtr Klass, TK Kind, int Slot = 0);

    private static readonly Dictionary<ulong, List<Method>> ByCode = new();
    private static readonly Dictionary<ulong, Method> ByInfo = new();
    private static readonly Dictionary<string, List<Method>> ByName = new();
    private static readonly Dictionary<ulong, string> Classes = new();
    private static readonly HashSet<ulong> Images = new();
    private static readonly Dictionary<IntPtr, List<FieldRec>> InstFields = new(), StatFields = new();
    private static ulong[] _starts = Array.Empty<ulong>();
    private static readonly Dictionary<ulong, string> Aliases = new();
    /// <summary>Globals the code initialises with InitializeRuntimeMetadata: only these may be resolved.</summary>
    private static readonly HashSet<ulong> MetadataGlobals = new();
    private static delegate* unmanaged<ulong*, void> _initMetadata;
    private static IntPtr _stringClass;
    private static bool _built;
    private static uint _iflags;

    // ------------------------------------------------------------------ symbol tables

    public static string Build()
    {
        var sw = Stopwatch.StartNew();
        ByCode.Clear(); ByInfo.Clear(); ByName.Clear(); Classes.Clear(); Images.Clear();
        uint n = 0;
        IntPtr* asms = IL2CPP.il2cpp_domain_get_assemblies(IL2CPP.il2cpp_domain_get(), ref n);
        int classes = 0;
        for (uint a = 0; a < n; a++)
        {
            IntPtr image = IL2CPP.il2cpp_assembly_get_image(asms[a]);
            Images.Add((ulong)image);
            uint count = IL2CPP.il2cpp_image_get_class_count(image);
            for (uint c = 0; c < count; c++)
            {
                IntPtr klass = IL2CPP.il2cpp_image_get_class(image, c);
                if (klass == IntPtr.Zero) continue;
                classes++;
                string cname = ClassName(klass);
                IntPtr iter = IntPtr.Zero, mi;
                while ((mi = IL2CPP.il2cpp_class_get_methods(klass, ref iter)) != IntPtr.Zero)
                {
                    var m = new Method
                    {
                        Info = mi, Klass = klass,
                        Name = cname + "::" + Utf8(IL2CPP.il2cpp_method_get_name(mi)),
                        Code = (ulong)*(IntPtr*)mi,
                        IsStatic = (IL2CPP.il2cpp_method_get_flags(mi, ref _iflags) & MethodStatic) != 0,
                        Params = (int)IL2CPP.il2cpp_method_get_param_count(mi),
                    };
                    ByInfo[(ulong)mi] = m;
                    if (!ByName.TryGetValue(m.Name, out var same)) ByName[m.Name] = same = new List<Method>();
                    same.Add(m);
                    if (m.Code == 0) continue;
                    if (!ByCode.TryGetValue(m.Code, out var at)) ByCode[m.Code] = at = new List<Method>();
                    at.Add(m);
                }
            }
        }
        _starts = ByCode.Keys.OrderBy(k => k).ToArray();
        _stringClass = (IntPtr)Classes.FirstOrDefault(kv => kv.Value == "System.String").Key;
        _built = true;
        FindHelpers();
        return $"{classes} classes, {ByInfo.Count} methods, {ByCode.Count} code addresses, {sw.ElapsedMilliseconds} ms";
    }

    private static void EnsureBuilt() { if (!_built) Build(); }

    private static string Utf8(nint p) => p == 0 ? "" : Marshal.PtrToStringUTF8((IntPtr)p) ?? "";

    private static string ClassName(IntPtr klass)
    {
        if (Classes.TryGetValue((ulong)klass, out var s)) return s;
        s = Utf8(IL2CPP.il2cpp_type_get_name(IL2CPP.il2cpp_class_get_type(klass)));
        if (s.Length == 0) s = Utf8(IL2CPP.il2cpp_class_get_name(klass));
        Classes[(ulong)klass] = s;
        return s;
    }

    private static string Short(IntPtr klass)
    {
        string s = ClassName(klass);
        int lt = s.IndexOf('<');
        int dot = (lt < 0 ? s : s[..lt]).LastIndexOf('.');
        return dot < 0 ? s : s[(dot + 1)..];
    }

    /// <summary>Is <paramref name="v"/> an Il2CppClass*? Known classes, or plausible ones (inflated generics).</summary>
    private static bool IsClass(ulong v)
    {
        if (v == 0) return false;
        if (Classes.ContainsKey(v)) return true;
        if (!Readable(v, VTableOffset)) return false;
        if (!Images.Contains(*(ulong*)v)) return false;
        ulong name = *(ulong*)(v + 0x10);
        if (!Readable(name, 1)) return false;
        ClassName((IntPtr)v);
        return true;
    }

    private static string? StringAt(ulong v)
    {
        if (_stringClass == IntPtr.Zero || !Readable(v, 0x18) || *(IntPtr*)v != _stringClass) return null;
        int len = *(int*)(v + 0x10);
        if (len < 0 || len > 4096 || !Readable(v + 0x14, len * 2)) return null;
        return new string((char*)(v + 0x14), 0, Math.Min(len, 120));
    }

    private static List<FieldRec> Fields(IntPtr klass, bool statics)
    {
        var cache = statics ? StatFields : InstFields;
        if (cache.TryGetValue(klass, out var list)) return list;
        list = new List<FieldRec>();
        for (IntPtr k = klass; k != IntPtr.Zero; k = statics ? IntPtr.Zero : IL2CPP.il2cpp_class_get_parent(k))
        {
            IntPtr iter = IntPtr.Zero, f;
            while ((f = IL2CPP.il2cpp_class_get_fields(k, ref iter)) != IntPtr.Zero)
            {
                int flags = IL2CPP.il2cpp_field_get_flags(f);
                if (((flags & FieldStatic) != 0) != statics || (flags & FieldLiteral) != 0) continue;
                int off = (int)IL2CPP.il2cpp_field_get_offset(f);
                if (off < 0) continue; // thread-static
                IntPtr type = IL2CPP.il2cpp_field_get_type(f);
                IntPtr tc = type == IntPtr.Zero ? IntPtr.Zero : IL2CPP.il2cpp_class_from_type(type);
                bool vt = tc == IntPtr.Zero || IL2CPP.il2cpp_class_is_valuetype(tc);
                list.Add(new FieldRec(off, Utf8(IL2CPP.il2cpp_field_get_name(f)), tc, vt, k));
            }
        }
        list.Sort((x, y) => x.Offset.CompareTo(y.Offset));
        cache[klass] = list;
        return list;
    }

    private static string FieldName(IntPtr klass, long off, bool statics)
    {
        FieldRec? best = null;
        foreach (var f in Fields(klass, statics)) if (f.Offset <= off) best = f; else break;
        if (best == null) return $"+0x{off:X}";
        return off == best.Offset ? best.Name : $"{best.Name}+0x{off - best.Offset:X}";
    }

    private static FieldRec? FieldExact(IntPtr klass, long off, bool statics) =>
        Fields(klass, statics).FirstOrDefault(f => f.Offset == off);

    private static string? CodeName(ulong address)
    {
        if (Aliases.TryGetValue(address, out var alias)) return alias;
        if (!ByCode.TryGetValue(address, out var ms)) return null;
        return ms.Count == 1 ? ms[0].Name : $"{ms[0].Name} (+{ms.Count - 1} folded)";
    }

    private static Method? Containing(ulong address)
    {
        int i = Array.BinarySearch(_starts, address);
        if (i < 0) i = ~i - 1;
        return i < 0 ? null : ByCode[_starts[i]][0];
    }

    private static List<Method> Lookup(string spec)
    {
        EnsureBuilt();
        if (ByName.TryGetValue(spec, out var exact)) return exact;
        var hits = ByName.Where(kv => kv.Key.EndsWith("." + spec) || kv.Key.EndsWith("+" + spec)).SelectMany(kv => kv.Value).ToList();
        if (hits.Count == 0) hits = ByName.Where(kv => kv.Key.Contains(spec)).SelectMany(kv => kv.Value).ToList();
        return hits;
    }

    /// <summary>Methods whose name contains <paramref name="pattern"/>, with code addresses.</summary>
    public static string Find(string pattern, int max = 60)
    {
        EnsureBuilt();
        var ms = ByName.Where(kv => kv.Key.Contains(pattern)).SelectMany(kv => kv.Value).Take(max);
        return string.Join("\n", ms.Select(m => $"{m}  @0x{m.Code:X}{(m.Code != 0 && ByCode[m.Code].Count > 1 ? $"  (shared by {ByCode[m.Code].Count})" : "")}"));
    }

    /// <summary>Instance and static field layout of a class (by full or partial name).</summary>
    public static string Layout(string className)
    {
        EnsureBuilt();
        var k = Classes.Where(kv => kv.Value == className || kv.Value.EndsWith("." + className)).Select(kv => (IntPtr)kv.Key).ToList();
        if (k.Count == 0) return "no class " + className;
        var sb = new StringBuilder();
        foreach (var klass in k)
        {
            sb.AppendLine($"{ClassName(klass)} @0x{klass.ToInt64():X} size={IL2CPP.il2cpp_class_instance_size(klass)}");
            foreach (var f in Fields(klass, false)) sb.AppendLine($"  0x{f.Offset:X3} {f.Name} : {(f.TypeClass == IntPtr.Zero ? "?" : Short(f.TypeClass))}");
            foreach (var f in Fields(klass, true)) sb.AppendLine($"  static 0x{f.Offset:X3} {f.Name} : {(f.TypeClass == IntPtr.Zero ? "?" : Short(f.TypeClass))}");
        }
        return sb.ToString();
    }

    /// <summary>Virtual method table of a class: slot number, implementation and name.</summary>
    public static string VTable(string className, int maxSlots = 120)
    {
        EnsureBuilt();
        var klass = (IntPtr)Classes.FirstOrDefault(kv => kv.Value == className || kv.Value.EndsWith("." + className)).Key;
        if (klass == IntPtr.Zero) return "no class " + className;
        var sb = new StringBuilder();
        for (int s = 0; s < maxSlots; s++)
        {
            ulong e = (ulong)klass + VTableOffset + (ulong)(s * VTableEntry);
            if (!Readable(e, 16)) break;
            ulong code = *(ulong*)e, mi = *(ulong*)(e + 8);
            if (!ByInfo.TryGetValue(mi, out var m)) break;
            sb.AppendLine($"  slot {s,3} @0x{code:X} {m}");
        }
        return sb.ToString();
    }

    // ------------------------------------------------------------------ disassembly

    /// <summary>Disassemble a method by name ("Class::method", partial names allowed; index picks an overload).</summary>
    public static string Dis(string spec, int index = 0, int maxBytes = 0x8000, int maxLines = 2500)
    {
        var ms = Lookup(spec);
        if (ms.Count == 0) return "no method matches " + spec;
        if (index >= ms.Count) return "only " + ms.Count + " matches: " + string.Join(", ", ms);
        var m = ms[index];
        if (m.Code == 0) return m + " has no code (abstract or generic definition)";
        var sb = new StringBuilder();
        sb.AppendLine($"{m} @0x{m.Code:X}{(m.IsStatic ? " static" : "")}{(ms.Count > 1 ? $"   [{index + 1}/{ms.Count}: {string.Join(", ", ms)}]" : "")}");
        if (ByCode[m.Code].Count > 1) sb.AppendLine("  same code as: " + string.Join(", ", ByCode[m.Code].Where(x => x != m).Take(6)));
        sb.Append(DisAt(m.Code, m, maxBytes, maxLines));
        return sb.ToString();
    }

    public static string DisAddr(ulong address, int maxBytes = 0x2000, int maxLines = 800)
    {
        EnsureBuilt();
        return DisAt(address, ByCode.TryGetValue(address, out var ms) ? ms[0] : null, maxBytes, maxLines);
    }

    private static readonly Register[] ArgRegs = { Register.RCX, Register.RDX, Register.R8, Register.R9 };
    private static readonly Register[] Volatile = { Register.RAX, Register.RCX, Register.RDX, Register.R8, Register.R9, Register.R10, Register.R11 };

    private delegate void Visit(in Instruction ins, Dictionary<Register, Track> regs);

    /// <summary>
    /// Decode one function from <paramref name="start"/>, tracking which registers hold which objects,
    /// and stop at its end: a return/jump past every forward branch, followed by padding or the next function.
    /// </summary>
    private static void Walk(ulong start, Method? m, int maxBytes, int maxInstructions, Visit visit)
    {
        maxBytes = ReadableLength(start, maxBytes);
        if (maxBytes == 0) return;
        var regs = new Dictionary<Register, Track>();
        if (m != null) SeedArgs(m, regs);
        ulong end = start + (ulong)maxBytes;
        var decoder = Decoder.Create(64, new MemReader(start, maxBytes), start);
        ulong furthest = start;
        int count = 0;
        while (decoder.IP < end && count++ < maxInstructions)
        {
            decoder.Decode(out var ins);
            if (ins.IsInvalid) break;
            visit(ins, regs);
            Update(ins, regs, Infos);
            if (ins.Op0Kind == OpKind.NearBranch64 && (ins.FlowControl == FlowControl.ConditionalBranch || ins.FlowControl == FlowControl.UnconditionalBranch))
            {
                ulong t = ins.NearBranchTarget;
                if (t > start && t < end && t > furthest) furthest = t;
            }
            bool terminal = ins.FlowControl is FlowControl.Return or FlowControl.UnconditionalBranch or FlowControl.IndirectBranch
                            || ins.Mnemonic == Mnemonic.Int3;
            if (terminal && ins.NextIP > furthest && (ins.NextIP >= end || *(byte*)ins.NextIP == 0xCC || ByCode.ContainsKey(ins.NextIP))) break;
        }
    }

    private static string DisAt(ulong start, Method? m, int maxBytes, int maxLines)
    {
        var fmt = new IntelFormatter(new Resolver(start, start + (ulong)maxBytes));
        fmt.Options.FirstOperandCharIndex = 7;
        fmt.Options.HexPrefix = "0x"; fmt.Options.HexSuffix = null;
        fmt.Options.SpaceAfterOperandSeparator = true;
        var output = new StringOutput();
        var sb = new StringBuilder();
        Walk(start, m, maxBytes, maxLines, (in Instruction ins, Dictionary<Register, Track> regs) =>
        {
            fmt.Format(ins, output);
            string text = output.ToStringAndReset();
            string note = Annotate(ins, regs);
            sb.Append($"{ins.IP - start,5:X}  ").Append(text);
            if (note.Length > 0) sb.Append(' ', Math.Max(1, 46 - text.Length)).Append("; ").Append(note);
            sb.AppendLine();
        });
        return sb.Length == 0 ? "unreadable" : sb.ToString();
    }

    // ------------------------------------------------------------------ whole-program index

    private readonly record struct FieldKey(IntPtr Owner, int Offset, bool Static);
    private static Dictionary<FieldKey, List<ulong>> _fieldRefs = new();   // site | (1<<63 if write)
    private static Dictionary<ulong, List<ulong>> _callRefs = new();       // target -> call/jmp sites
    private static Dictionary<(IntPtr, int), List<ulong>> _vcallRefs = new(); // (static class, slot) -> sites
    private static Dictionary<ulong, int> _throwVotes = new(), _newVotes = new(), _cinitVotes = new();
    private static int _indexPos = -1;
    private static string _indexInfo = "";
    private const ulong WriteBit = 1UL << 63;

    public static string IndexStatus => _indexPos < 0 ? "not indexed" : _indexPos >= _starts.Length ? _indexInfo : $"indexing {_indexPos}/{_starts.Length}";

    /// <summary>Index every function's field accesses and calls, a slice per frame so the game keeps running.</summary>
    public static string Index(int perFrame = 300)
    {
        EnsureBuilt();
        if (_indexPos >= 0 && _indexPos < _starts.Length) return IndexStatus;
        ResolveAll();
        _fieldRefs = new(); _callRefs = new(); _vcallRefs = new(); _throwVotes = new(); _newVotes = new(); _cinitVotes = new();
        _indexPos = 0;
        var sw = Stopwatch.StartNew();
        Pending.Add(() =>
        {
            int stop = Math.Min(_starts.Length, _indexPos + perFrame);
            for (; _indexPos < stop; _indexPos++)
            {
                try { IndexOne(_indexPos); } catch { /* unreadable or odd code: skip */ }
            }
            if (_indexPos < _starts.Length) return false;
            NameHelpers();
            _indexInfo = $"indexed {_starts.Length} functions in {sw.Elapsed.TotalSeconds:F1}s: {_fieldRefs.Count} fields, {_callRefs.Count} call targets, {_vcallRefs.Count} virtual slots";
            return true;
        });
        return "indexing started";
    }

    private static void IndexOne(int i)
    {
        ulong start = _starts[i];
        ulong limit = i + 1 < _starts.Length ? Math.Min(_starts[i + 1] - start, 0x40000UL) : 0x4000UL;
        var m = ByCode[start][0];
        Instruction prev = default, prev2 = default;
        Walk(start, ByCode[start].Count == 1 ? m : null, (int)limit, 20000, (in Instruction ins, Dictionary<Register, Track> regs) =>
        {
            if (ins.FlowControl is FlowControl.Call or FlowControl.UnconditionalBranch && ins.Op0Kind == OpKind.NearBranch64)
            {
                ulong t = ins.NearBranchTarget;
                if (ins.FlowControl == FlowControl.Call || t < start || t >= start + limit) Add(_callRefs, t, ins.IP);
            }
            if (ins.FlowControl is FlowControl.IndirectCall or FlowControl.IndirectBranch && ins.Op0Kind == OpKind.Register
                && regs.TryGetValue(ins.Op0Register.GetFullRegister(), out var vf) && vf.Kind == TK.VFunc)
                Add(_vcallRefs, (vf.Klass, vf.Slot), ins.IP);
            for (int op = 0; op < ins.OpCount; op++)
            {
                if (ins.GetOpKind(op) != OpKind.Memory || ins.IsIPRelativeMemoryOperand || ins.MemoryBase == Register.None) continue;
                if (!regs.TryGetValue(ins.MemoryBase.GetFullRegister(), out var t)) continue;
                long d = (long)ins.MemoryDisplacement64;
                ulong site = ins.IP | (op == 0 && ins.Mnemonic != Mnemonic.Cmp && ins.Mnemonic != Mnemonic.Test ? WriteBit : 0);
                switch (t.Kind)
                {
                    case TK.Obj when d > 0 && ins.MemoryIndex == Register.None:
                        if (FieldAt(t.Klass, d, false) is { } f) Add(_fieldRefs, new FieldKey(f.Owner, f.Offset, false), site);
                        break;
                    case TK.Statics:
                        if (FieldAt(t.Klass, d, true) is { } sf) Add(_fieldRefs, new FieldKey(sf.Owner, sf.Offset, true), site);
                        break;
                    case TK.KlassOf when d >= VTableOffset && (d - VTableOffset) % VTableEntry == 0 && ins.FlowControl == FlowControl.IndirectCall:
                        Add(_vcallRefs, (t.Klass, (int)((d - VTableOffset) / VTableEntry)), ins.IP);
                        break;
                }
            }
            // runtime helper fingerprints
            if (ins.Mnemonic == Mnemonic.Int3 && prev.Mnemonic == Mnemonic.Call && prev.Op0Kind == OpKind.NearBranch64) Vote(_throwVotes, prev.NearBranchTarget);
            if (ins.Mnemonic == Mnemonic.Call && ins.Op0Kind == OpKind.NearBranch64)
            {
                if (prev.Mnemonic == Mnemonic.Mov && prev.Op0Register == Register.RCX && prev.IsIPRelativeMemoryOperand && IsClass(Readable(prev.IPRelativeMemoryAddress, 8) ? *(ulong*)prev.IPRelativeMemoryAddress : 0))
                    Vote(_newVotes, ins.NearBranchTarget);
                if (prev2.Mnemonic == Mnemonic.Cmp && prev2.MemoryDisplacement64 == 0xE0 && prev.FlowControl == FlowControl.ConditionalBranch)
                    Vote(_cinitVotes, ins.NearBranchTarget);
            }
            prev2 = prev; prev = ins;
        });
    }

    private static void Add<TK2>(Dictionary<TK2, List<ulong>> d, TK2 key, ulong site) where TK2 : notnull
    {
        if (!d.TryGetValue(key, out var l)) d[key] = l = new List<ulong>(2);
        l.Add(site);
    }

    private static void Vote(Dictionary<ulong, int> votes, ulong target) => votes[target] = votes.TryGetValue(target, out var n) ? n + 1 : 1;

    private static void NameHelpers()
    {
        var thrown = _throwVotes.Where(kv => !ByCode.ContainsKey(kv.Key)).OrderByDescending(kv => kv.Value).Take(2).ToList();
        if (thrown.Count > 0) Aliases[thrown[0].Key] = "il2cpp::ThrowNullReference";
        if (thrown.Count > 1) Aliases[thrown[1].Key] = "il2cpp::ThrowIndexOutOfRange?";
        var made = _newVotes.Where(kv => !ByCode.ContainsKey(kv.Key)).OrderByDescending(kv => kv.Value).FirstOrDefault();
        if (made.Key != 0) Aliases[made.Key] = "il2cpp::ObjectNew";
        var cinit = _cinitVotes.Where(kv => !ByCode.ContainsKey(kv.Key)).OrderByDescending(kv => kv.Value).FirstOrDefault();
        if (cinit.Key != 0) Aliases[cinit.Key] = "il2cpp::RunClassConstructor";
    }

    private static FieldRec? FieldAt(IntPtr klass, long off, bool statics)
    {
        FieldRec? best = null;
        foreach (var f in Fields(klass, statics)) if (f.Offset <= off) best = f; else break;
        return best;
    }

    private static string Site(ulong site)
    {
        ulong ip = site & ~WriteBit;
        var fn = Containing(ip);
        return $"{((site & WriteBit) != 0 ? "W" : "R")} {(fn == null ? $"0x{ip:X}" : $"{fn}+0x{ip - fn.Code:X}")}";
    }

    /// <summary>Functions reading (R) or writing (W) a field, from the index.</summary>
    public static string FieldRefs(string className, string field, int max = 80)
    {
        if (_indexPos < _starts.Length) return IndexStatus;
        var hits = new List<string>();
        foreach (var kv in Classes.Where(kv => kv.Value == className || kv.Value.EndsWith("." + className)))
            foreach (bool stat in new[] { false, true })
                foreach (var f in Fields((IntPtr)kv.Key, stat).Where(f => f.Name == field))
                    if (_fieldRefs.TryGetValue(new FieldKey(f.Owner, f.Offset, stat), out var sites))
                        hits.AddRange(sites.Select(Site));
        return hits.Count == 0 ? "no indexed access" : string.Join("\n", hits.Distinct().Take(max)) + (hits.Count > max ? $"\n... {hits.Count} total" : "");
    }

    /// <summary>Direct call sites of a method, from the index.</summary>
    public static string CallSites(string spec, int index = 0, int max = 80)
    {
        if (_indexPos < _starts.Length) return IndexStatus;
        var ms = Lookup(spec);
        if (ms.Count <= index) return "no method " + spec;
        var m = ms[index];
        var lines = new List<string>();
        if (_callRefs.TryGetValue(m.Code, out var sites)) lines.AddRange(sites.Select(Site));
        // virtual call sites: same slot in this class or any class it derives from
        for (IntPtr k = m.Klass; k != IntPtr.Zero; k = IL2CPP.il2cpp_class_get_parent(k))
            for (int slot = 0; slot < 400; slot++)
            {
                ulong e = (ulong)k + VTableOffset + (ulong)slot * VTableEntry;
                if (!Readable(e, 16) || !ByInfo.TryGetValue(*(ulong*)(e + 8), out var vm)) break;
                if (vm.Name.EndsWith("::" + m.Name.Split("::")[1]) && _vcallRefs.TryGetValue((k, slot), out var vs))
                    lines.AddRange(vs.Select(v => "virtual " + Site(v)));
            }
        return $"{m} @0x{m.Code:X}{(ByCode[m.Code].Count > 1 ? $" (code shared by {ByCode[m.Code].Count})" : "")}:\n" +
               (lines.Count == 0 ? "no indexed call" : string.Join("\n", lines.Distinct().Take(max)));
    }

    /// <summary>
    /// Resolve every metadata global the code initialises lazily ("lea rcx,[global]; call InitializeRuntimeMetadata"),
    /// so type/method/string references can be found even in functions that have not run yet.
    /// </summary>
    public static string ResolveAll()
    {
        EnsureBuilt();
        if (_initMetadata == null) return "metadata initialiser not found";
        int resolved = 0;
        foreach (var g in MetadataGlobals)
        {
            if (!Readable(g, 8)) continue;
            ulong v = *(ulong*)g;
            if ((v & 1) == 1 && v <= 0xFFFFFFFFUL) { _initMetadata((ulong*)g); resolved++; }
        }
        return $"{MetadataGlobals.Count} metadata globals, {resolved} resolved now";
    }


    // ------------------------------------------------------------------ annotation and register tracking

    private static void SeedArgs(Method m, Dictionary<Register, Track> regs)
    {
        int slot = 0;
        if (!m.IsStatic)
        {
            if (!IL2CPP.il2cpp_class_is_valuetype(m.Klass)) regs[Register.RCX] = new Track(m.Klass, TK.Obj);
            slot = 1;
        }
        for (int p = 0; p < m.Params && slot < 4; p++, slot++)
        {
            IntPtr type = IL2CPP.il2cpp_method_get_param(m.Info, (uint)p);
            if (type == IntPtr.Zero || IL2CPP.il2cpp_type_is_byref(type)) continue;
            IntPtr c = IL2CPP.il2cpp_class_from_type(type);
            if (c != IntPtr.Zero && !IL2CPP.il2cpp_class_is_valuetype(c)) regs[ArgRegs[slot]] = new Track(c, TK.Obj);
        }
    }

    private static string Annotate(in Instruction ins, Dictionary<Register, Track> regs)
    {
        if (ins.FlowControl is FlowControl.IndirectCall or FlowControl.IndirectBranch && ins.Op0Kind == OpKind.Register
            && regs.TryGetValue(ins.Op0Register.GetFullRegister(), out var vf) && vf.Kind == TK.VFunc)
            return "virtual " + SlotMethod(vf.Klass, vf.Slot);
        for (int i = 0; i < ins.OpCount; i++)
        {
            if (ins.GetOpKind(i) != OpKind.Memory) continue;
            if (ins.IsIPRelativeMemoryOperand) return DescribeGlobal(ins.IPRelativeMemoryAddress, ins.Mnemonic == Mnemonic.Lea);
            var b = ins.MemoryBase == Register.None ? Register.None : ins.MemoryBase.GetFullRegister();
            long d = (long)ins.MemoryDisplacement64;
            if (b != Register.None && regs.TryGetValue(b, out var t)) return DescribeMember(t, d, ins.MemoryIndex != Register.None);
        }
        return "";
    }

    private static string DescribeGlobal(ulong ea, bool isLea)
    {
        if (isLea) return CodeName(ea) is { } fn ? "&" + fn : Aliases.TryGetValue(ea, out var al) ? "&" + al : "";
        if (!Readable(ea, 8)) return "";
        ulong v = ResolveGlobal(ea);
        if (ByInfo.TryGetValue(v, out var m)) return "MethodInfo " + m;
        if (IsClass(v)) return "TypeInfo " + ClassName((IntPtr)v);
        if (StringAt(v) is { } s) return "\"" + s + "\"";
        if (MetadataGlobals.Contains(ea) && InflatedMethod(v) is { } im) return "MethodInfo " + im;
        return "";
    }

    /// <summary>
    /// Generic method instances are not listed by their classes. Recognise a MethodInfo by its layout
    /// (code, virtual code, invoker, name, class, ...) and remember its shared code address.
    /// </summary>
    private static string? InflatedMethod(ulong v)
    {
        if (!Readable(v, 0x40)) return null;
        ulong name = *(ulong*)(v + 0x18), klass = *(ulong*)(v + 0x20), code = *(ulong*)v;
        if (!IsClass(klass) || !Readable(name, 1)) return null;
        string n = ClassName((IntPtr)klass) + "::" + Marshal.PtrToStringUTF8((IntPtr)(long)name);
        string full = Utf8(IL2CPP.il2cpp_type_get_name(IL2CPP.il2cpp_method_get_return_type((IntPtr)(long)v)));
        if (code != 0 && !ByCode.ContainsKey(code)) Aliases.TryAdd(code, n);
        return $"{n} -> {full}";
    }

    private static string SlotMethod(IntPtr klass, int slot)
    {
        ulong e = (ulong)klass + VTableOffset + (ulong)slot * VTableEntry + 8;
        return Readable(e, 8) && ByInfo.TryGetValue(*(ulong*)e, out var vm) ? vm.ToString() : $"{Short(klass)} slot {slot}";
    }

    private static string DescribeMember(Track t, long d, bool indexed)
    {
        switch (t.Kind)
        {
            case TK.Obj:
                if (d == 0) return Short(t.Klass) + ".klass";
                string cn = ClassName(t.Klass);
                if (cn.EndsWith("[]")) return d == 0x18 ? "array.length" : indexed || d >= 0x20 ? $"array[{(d - 0x20)}+]" : "";
                if (t.Klass == _stringClass) return d == 0x10 ? "string.length" : "string.chars";
                return Short(t.Klass) + "." + FieldName(t.Klass, d, false);
            case TK.TypeInfo:
            case TK.KlassOf:
                if (d == StaticFieldsOffset) return Short(t.Klass) + ".static_fields";
                if (d >= VTableOffset && (d - VTableOffset) % 8 == 0)
                {
                    int slot = (int)((d - VTableOffset) / VTableEntry);
                    return $"vslot {slot}{((d - VTableOffset) % VTableEntry == 8 ? " (MethodInfo)" : "")} {SlotMethod(t.Klass, slot)}";
                }
                return $"{Short(t.Klass)} class+0x{d:X}";
            case TK.Statics:
                return "static " + Short(t.Klass) + "." + FieldName(t.Klass, d, true);
        }
        return "";
    }

    private static readonly InstructionInfoFactory Infos = new();

    private static void Update(in Instruction ins, Dictionary<Register, Track> regs, InstructionInfoFactory infos)
    {
        Track? value = null;
        Register dst = Register.None;
        if (ins.Mnemonic == Mnemonic.Mov && ins.Op0Kind == OpKind.Register && ins.Op0Register.GetSize() == 8)
        {
            dst = ins.Op0Register;
            if (ins.Op1Kind == OpKind.Register && regs.TryGetValue(ins.Op1Register.GetFullRegister(), out var src)) value = src;
            else if (ins.Op1Kind == OpKind.Memory)
            {
                if (ins.IsIPRelativeMemoryOperand)
                {
                    ulong ea = ins.IPRelativeMemoryAddress;
                    ulong gv = Readable(ea, 8) ? ResolveGlobal(ea) : 0;
                    if (IsClass(gv)) value = new Track((IntPtr)gv, TK.TypeInfo);
                }
                else if (ins.MemoryIndex == Register.None && ins.MemoryBase != Register.None && regs.TryGetValue(ins.MemoryBase.GetFullRegister(), out var b))
                {
                    long d = (long)ins.MemoryDisplacement64;
                    FieldRec? f;
                    switch (b.Kind)
                    {
                        case TK.Obj when d == 0: value = new Track(b.Klass, TK.KlassOf); break;
                        case TK.Obj when (f = FieldExact(b.Klass, d, false)) != null && !f.ValueType: value = new Track(f.TypeClass, TK.Obj); break;
                        case TK.TypeInfo when d == StaticFieldsOffset: value = new Track(b.Klass, TK.Statics); break;
                        case TK.KlassOf or TK.TypeInfo when d >= VTableOffset && (d - VTableOffset) % VTableEntry == 0:
                            value = new Track(b.Klass, TK.VFunc, (int)((d - VTableOffset) / VTableEntry)); break;
                        case TK.Statics when (f = FieldExact(b.Klass, d, true)) != null && !f.ValueType: value = new Track(f.TypeClass, TK.Obj); break;
                    }
                }
            }
        }

        foreach (var used in infos.GetInfo(ins).GetUsedRegisters())
            if (used.Access is OpAccess.Write or OpAccess.ReadWrite or OpAccess.CondWrite or OpAccess.ReadCondWrite)
                regs.Remove(used.Register.GetFullRegister());

        if (ins.FlowControl is FlowControl.Call or FlowControl.IndirectCall)
        {
            foreach (var r in Volatile) regs.Remove(r);
            if (ins.Op0Kind == OpKind.NearBranch64 && ByCode.TryGetValue(ins.NearBranchTarget, out var ms) && ms.Count == 1 && ms[0].Ret != IntPtr.Zero)
                regs[Register.RAX] = new Track(ms[0].Ret, TK.Obj);
        }
        if (value != null && dst != Register.None) regs[dst] = value.Value;
    }

    private sealed class MemReader : CodeReader
    {
        private byte* _p;
        private readonly byte* _end;
        public MemReader(ulong start, int length) { _p = (byte*)start; _end = _p + length; }
        public override int ReadByte() => _p < _end ? *_p++ : -1;
    }

    private sealed class Resolver : ISymbolResolver
    {
        private readonly ulong _start, _end;
        public Resolver(ulong start, ulong end) { _start = start; _end = end; }
        public bool TryGetSymbol(in Instruction instruction, int operand, int instructionOperand, ulong address, int addressSize, out SymbolResult symbol)
        {
            if (instructionOperand >= 0 && instruction.GetOpKind(instructionOperand) == OpKind.NearBranch64)
            {
                if (address >= _start && address < _end) { symbol = new SymbolResult(address, $"L{address - _start:X}"); return true; }
                if (CodeName(address) is { } n) { symbol = new SymbolResult(address, n); return true; }
            }
            symbol = default;
            return false;
        }
    }

    // ------------------------------------------------------------------ cross references by scanning

    /// <summary>Who calls / jumps to / takes the address of a method (direct references only).</summary>
    public static string Callers(string spec, int index = 0)
    {
        var ms = Lookup(spec);
        if (ms.Count <= index) return "no method " + spec;
        return $"refs to {ms[index]} @0x{ms[index].Code:X}:\n" + Refs(ms[index].Code);
    }

    /// <summary>Code that loads the MethodInfo of a method (delegate creation, reflection, generic calls).</summary>
    public static string InfoUses(string spec, int index = 0)
    {
        var ms = Lookup(spec);
        if (ms.Count <= index) return "no method " + spec;
        return ValueUses((ulong)ms[index].Info, ms[index] + " MethodInfo");
    }

    /// <summary>Code that loads a class's type info (object creation, static field access, casts).</summary>
    public static string ClassUses(string className)
    {
        EnsureBuilt();
        var k = Classes.FirstOrDefault(kv => kv.Value == className || kv.Value.EndsWith("." + className)).Key;
        return k == 0 ? "no class " + className : ValueUses(k, ClassName((IntPtr)k) + " TypeInfo");
    }

    private static string ValueUses(ulong value, string what)
    {
        var sb = new StringBuilder();
        foreach (var (s, len) in Sections(write: true))
            for (ulong p = s; p + 8 <= s + len; p += 8)
                if (*(ulong*)p == value) sb.Append($"global 0x{p:X} holds {what}:\n").Append(Refs(p));
        return sb.Length == 0 ? "no global holds " + what : sb.ToString();
    }

    public static string Refs(ulong target, int max = 300)
    {
        EnsureBuilt();
        var sites = new List<ulong>();
        foreach (var (s, len) in Sections(write: false))
        {
            byte* p = (byte*)s, last = (byte*)(s + len - 8);
            for (; p < last; p++)
            {
                if ((*p == 0xE8 || *p == 0xE9) && (ulong)(p + 5 + *(int*)(p + 1)) == target) sites.Add((ulong)p);
                int d = *(int*)p;
                if ((ulong)(p + 4 + d) == target || (ulong)(p + 5 + d) == target || (ulong)(p + 8 + d) == target) sites.Add((ulong)p);
                if (sites.Count > max * 4) break;
            }
        }
        var lines = new List<string>();
        var fmt = new IntelFormatter();
        var output = new StringOutput();
        foreach (var site in sites.Distinct())
        {
            var fn = Containing(site);
            if (fn == null || site - fn.Code > 0x40000) continue;
            var dec = Decoder.Create(64, new MemReader(fn.Code, (int)(site - fn.Code) + 16), fn.Code);
            while (dec.IP <= site)
            {
                dec.Decode(out var ins);
                if (ins.IsInvalid) break;
                if (ins.NextIP <= site) continue;
                bool hit = (ins.Op0Kind == OpKind.NearBranch64 && ins.NearBranchTarget == target)
                           || (ins.IsIPRelativeMemoryOperand && ins.IPRelativeMemoryAddress == target);
                if (hit)
                {
                    fmt.Format(ins, output);
                    lines.Add($"  {fn}+0x{ins.IP - fn.Code:X}: {output.ToStringAndReset()}");
                }
                break;
            }
            if (lines.Count >= max) break;
        }
        return lines.Count == 0 ? "  (no direct references)\n" : string.Join("\n", lines.Distinct()) + "\n";
    }

    // ------------------------------------------------------------------ runtime helpers

    /// <summary>Name a native address (runtime helper functions are not IL methods).</summary>
    public static void Alias(ulong address, string name) => Aliases[address] = name;

    /// <summary>
    /// Metadata globals (type infos, method infos, string literals) hold an encoded token until the
    /// function that uses them first runs. Resolve them the same way the game does, so a function can
    /// be read before it has ever executed.
    /// </summary>
    private static ulong ResolveGlobal(ulong ea)
    {
        ulong v = *(ulong*)ea;
        if ((v & 1) == 0 || v > 0xFFFFFFFFUL) return v;
        // An odd small value may just be an ordinary static int; never feed those to the resolver.
        if (_initMetadata == null || !MetadataGlobals.Contains(ea)) return v;
        _initMetadata((ulong*)ea);
        return *(ulong*)ea;
    }

    /// <summary>
    /// Find the IL2CPP runtime helpers by their call patterns: every method prologue does
    /// "lea rcx,[global]; call InitializeRuntimeMetadata; lock or [rsp],0", and allocation is
    /// "mov rcx,[TypeInfo]; call ObjectNew" right before a constructor call.
    /// </summary>
    private static void FindHelpers()
    {
        var votes = new Dictionary<ulong, int>();
        foreach (var start in _starts.Take(4000))
        {
            var dec = Decoder.Create(64, new MemReader(start, 96), start);
            Instruction prev = default;
            for (int i = 0; i < 12; i++)
            {
                dec.Decode(out var ins);
                if (ins.IsInvalid) break;
                if (prev.Mnemonic == Mnemonic.Call && prev.Op0Kind == OpKind.NearBranch64 && ins.HasLockPrefix && ins.Mnemonic == Mnemonic.Or)
                    votes[prev.NearBranchTarget] = votes.TryGetValue(prev.NearBranchTarget, out var n) ? n + 1 : 1;
                prev = ins;
            }
        }
        if (votes.Count == 0) return;
        ulong init = votes.OrderByDescending(kv => kv.Value).First().Key;
        Aliases[init] = "il2cpp::InitializeRuntimeMetadata";
        _initMetadata = (delegate* unmanaged<ulong*, void>)init;
        MetadataGlobals.Clear();
        foreach (var (s, len) in Sections(write: false))
        {
            byte* p = (byte*)s, last = (byte*)(s + len - 12);
            for (; p < last; p++)
            {
                if (p[0] != 0x48 || p[1] != 0x8D || p[2] != 0x0D || p[7] != 0xE8) continue;
                ulong g = (ulong)(p + 7 + *(int*)(p + 3)), t = (ulong)(p + 12 + *(int*)(p + 8));
                if (t == init) MetadataGlobals.Add(g);
            }
        }
    }

    /// <summary>Safe hex dump of qwords at an address (unreadable memory is reported, not touched).</summary>
    public static string Peek(ulong address, int qwords = 4)
    {
        var sb = new StringBuilder();
        for (int i = 0; i < qwords; i++)
        {
            ulong p = address + (ulong)(i * 8);
            sb.Append(Readable(p, 8) ? $"0x{p:X}: 0x{*(ulong*)p:X16}\n" : $"0x{p:X}: unreadable\n");
        }
        return sb.ToString();
    }

    /// <summary>Describe what a global or pointer value refers to (method info, type info, string, code).</summary>
    public static string What(ulong value)
    {
        EnsureBuilt();
        if (ByInfo.TryGetValue(value, out var m)) return "MethodInfo " + m;
        if (CodeName(value) is { } fn) return "code " + fn;
        if (IsClass(value)) return "TypeInfo " + ClassName((IntPtr)value);
        if (StringAt(value) is { } s) return "string \"" + s + "\"";
        if (InflatedMethod(value) is { } im) return "MethodInfo " + im;
        var c = Containing(value);
        return c != null && value - c.Code < 0x10000 ? $"inside {c}+0x{value - c.Code:X}" : "unknown";
    }

    // ------------------------------------------------------------------ memory helpers

    [DllImport("kernel32.dll")] private static extern IntPtr GetModuleHandleW([MarshalAs(UnmanagedType.LPWStr)] string name);
    [DllImport("kernel32.dll")] private static extern bool IsBadReadPtr(IntPtr p, UIntPtr size);

    internal static bool Readable(ulong p, int size) => p > 0x10000 && !IsBadReadPtr((IntPtr)(long)p, (UIntPtr)(uint)Math.Max(1, size));

    private static int ReadableLength(ulong p, int max)
    {
        int ok = 0;
        while (ok < max && Readable(p + (ulong)ok, Math.Min(0x1000, max - ok))) ok += Math.Min(0x1000, max - ok);
        return ok;
    }

    /// <summary>GameAssembly.dll sections: executable ones, or writable data ones.</summary>
    private static List<(ulong start, ulong len)> Sections(bool write)
    {
        var list = new List<(ulong, ulong)>();
        ulong mod = (ulong)GetModuleHandleW("GameAssembly.dll");
        if (mod == 0) return list;
        byte* nt = (byte*)(mod + *(uint*)(mod + 0x3C));
        int count = *(ushort*)(nt + 6), optSize = *(ushort*)(nt + 20);
        byte* sec = nt + 24 + optSize;
        for (int i = 0; i < count; i++, sec += 40)
        {
            uint size = *(uint*)(sec + 8), va = *(uint*)(sec + 12), ch = *(uint*)(sec + 36);
            bool exec = (ch & 0x20000000) != 0, writable = (ch & 0x80000000) != 0;
            if (write ? writable && !exec : exec) list.Add((mod + va, size));
        }
        return list;
    }
}
