// Turn on PataCoop.Coop.SharedRandom.Trace (every shared roll's key: phase, clock, kind, who, count) on this copy.
// Read: return string.Join("\n", (System.Collections.Generic.List<string>)Vars["randTrace"]);
var f = typeof(PataCoop.CoopPlugin).Assembly.GetType("PataCoop.Coop.SharedRandom").GetField("Trace", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static);
var list = new System.Collections.Generic.List<string>(); f.SetValue(null, list); Vars["randTrace"] = list;
return $"[{Instance}] shared random trace on";
