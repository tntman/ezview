using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;

namespace EzView;

// One window per ezview.exe: opening another file hands it to the running window (no second startup).
// Keyed on the exe path, so a debug build and an installed copy don't talk to each other.
static class SingleInstance
{
    static readonly string Name = "ezview-" + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(Environment.ProcessPath!.ToLowerInvariant())))[..16];
    static Mutex? _mutex;

    [DllImport("user32.dll")] static extern bool AllowSetForegroundWindow(int processId);

    // True when a running window took the file; the caller then exits.
    public static bool HandOff(string? file)
    {
        _mutex = new Mutex(true, @"Local\" + Name, out bool first);
        if (first || file == null) return false;
        try
        {
            AllowSetForegroundWindow(-1); // let the running window come to the front
            using var pipe = new NamedPipeClientStream(".", Name, PipeDirection.Out);
            pipe.Connect(2000);
            pipe.Write(Encoding.UTF8.GetBytes(Path.GetFullPath(file)));
            return true;
        }
        catch { return false; } // window busy or closing: start normally
    }

    // Runs in the first instance: each connection sends one path.
    public static void Listen(Action<string> open)
    {
        Task.Run(async () =>
        {
            while (true)
            {
                try
                {
                    await using var server = new NamedPipeServerStream(Name, PipeDirection.In, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous);
                    await server.WaitForConnectionAsync();
                    using var reader = new StreamReader(server, Encoding.UTF8);
                    var path = await reader.ReadToEndAsync();
                    if (path.Length > 0) Avalonia.Threading.Dispatcher.UIThread.Post(() => open(path));
                }
                catch (IOException) { return; } // another window already serves this name
            }
        });
    }
}
