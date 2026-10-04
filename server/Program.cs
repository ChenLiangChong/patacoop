using PataCoop.Server;

int port = 27015;
for (int i = 0; i < args.Length - 1; i++)
    if (args[i] == "--port" && int.TryParse(args[i + 1], out var p)) port = p;

string logPath = Path.Combine(AppContext.BaseDirectory, "server.log");
var logLock = new object();
void Log(string msg)
{
    string line = $"{DateTime.Now:HH:mm:ss.fff} {msg}";
    lock (logLock)
    {
        Console.WriteLine(line);
        File.AppendAllText(logPath, line + Environment.NewLine);
    }
}

var server = new RelayServer(Log);
if (!server.Start(port))
{
    Log($"could not listen on UDP {port} (already in use?)");
    return 1;
}
Log($"PataCoop server listening on UDP {port} (protocol v{RelayServer.ProtocolVersion}). Ctrl+C to stop.");

using var cts = new CancellationTokenSource();
Console.CancelKeyPress += (_, e) => { e.Cancel = true; cts.Cancel(); };

var lastStatus = DateTime.MinValue;
string lastText = "";
while (!cts.IsCancellationRequested)
{
    server.Poll();
    if (DateTime.UtcNow - lastStatus > TimeSpan.FromSeconds(30))
    {
        lastStatus = DateTime.UtcNow;
        string text = server.Status();
        if (text != lastText) { Log("status: " + text); lastText = text; }
    }
    Thread.Sleep(2);
}

server.Stop();
Log("stopped");
return 0;
