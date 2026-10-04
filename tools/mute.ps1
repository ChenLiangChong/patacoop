param([string]$ProcessName = 'PATAPON12_REPLAY', [int]$Mute = 1)
Add-Type -TypeDefinition @"
using System; using System.Runtime.InteropServices; using System.Collections.Generic;
[ComImport, Guid("BCDE0395-E52F-467C-8E3D-C4579291692E")] class MMDeviceEnumerator {}
[Guid("A95664D2-9614-4F35-A746-DE8DB63617E6"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
interface IMMDeviceEnumerator { int EnumAudioEndpoints(int dataFlow, int stateMask, out IMMDeviceCollection c); }
[Guid("0BD7A1BE-7A1A-44DB-8397-CC5392387B5E"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
interface IMMDeviceCollection { int GetCount(out int n); int Item(int i, out IMMDevice d); }
[Guid("D666063F-1587-4E43-81F1-B948E807363F"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
interface IMMDevice { int Activate(ref Guid iid, int clsCtx, IntPtr p, [MarshalAs(UnmanagedType.IUnknown)] out object o); }
[Guid("77AA99A0-1BD6-484F-8BC7-2C654C9A9B6F"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
interface IAudioSessionManager2 { int NotImpl1(); int NotImpl2(); int GetSessionEnumerator(out IAudioSessionEnumerator e); }
[Guid("E2F5BB11-0570-40CA-ACDD-3AA01277DEE8"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
interface IAudioSessionEnumerator { int GetCount(out int n); int GetSession(int i, out IAudioSessionControl2 s); }
[Guid("bfb7ff88-7239-4fc9-8fa2-07c950be9c6d"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
interface IAudioSessionControl2 { int a();int b();int c();int d();int e();int f();int g();int h();int i(); int GetSessionIdentifier(out IntPtr s); int GetSessionInstanceIdentifier(out IntPtr s); int GetProcessId(out uint pid); }
[Guid("87CE5498-68D6-44E5-9215-6DA47EF883D8"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
interface ISimpleAudioVolume { int SetMasterVolume(float v, ref Guid ctx); int GetMasterVolume(out float v); int SetMute(bool m, ref Guid ctx); int GetMute(out bool m); }
public static class Mixer {
  // every active output device: the game's session may sit on any of them (headphones, speakers...)
  public static List<string> SetMute(int[] pids, bool mute) {
    var log = new List<string>();
    var en = (IMMDeviceEnumerator)(new MMDeviceEnumerator());
    IMMDeviceCollection col; en.EnumAudioEndpoints(0, 1, out col); int nd; col.GetCount(out nd);
    for (int d = 0; d < nd; d++) {
      IMMDevice dev; col.Item(d, out dev);
      var iid = typeof(IAudioSessionManager2).GUID; object o; dev.Activate(ref iid, 23, IntPtr.Zero, out o);
      IAudioSessionEnumerator se; ((IAudioSessionManager2)o).GetSessionEnumerator(out se); int n; se.GetCount(out n);
      for (int i = 0; i < n; i++) {
        IAudioSessionControl2 s; se.GetSession(i, out s); uint pid; s.GetProcessId(out pid);
        if (Array.IndexOf(pids, (int)pid) < 0) continue;
        var v = (ISimpleAudioVolume)s; var g = Guid.Empty; v.SetMute(mute, ref g); bool m; v.GetMute(out m);
        log.Add("pid " + pid + " device " + d + " muted=" + m);
      }
    }
    return log;
  }
}
"@
$pids = @(Get-Process $ProcessName -EA SilentlyContinue | ForEach-Object Id)
$r = [Mixer]::SetMute($pids, [bool]$Mute)
if ($r.Count -eq 0) { "no audio sessions found for $ProcessName ($($pids -join ','))" } else { $r }
