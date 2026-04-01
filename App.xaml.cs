using System;
using System.IO;
using System.IO.Pipes;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;

namespace TS3ScreenShare
{
    public partial class App : System.Windows.Application
    {
        public const string PipeName = "TS3ScreenShare";
        private const string MutexName = "TS3ScreenShare-Instance";

        // Fired on the UI thread when a command arrives from the plugin (or another instance)
        public static event Action<string>? PipeCommandReceived;

        private Mutex? _instanceMutex;
        private CancellationTokenSource? _pipeCts;

        // Command-line args parsed at startup, read by MainWindow
        public static string? StartupRelayUrl { get; private set; }

        protected override void OnStartup(StartupEventArgs e)
        {
            _instanceMutex = new Mutex(true, MutexName, out bool isFirst);

            if (!isFirst)
            {
                // Another instance is already running — forward args and exit
                ForwardArgsToRunningInstance(e.Args);
                Shutdown();
                return;
            }

            ParseArgs(e.Args);

            _pipeCts = new CancellationTokenSource();
            _ = Task.Run(() => PipeServerLoopAsync(_pipeCts.Token));

            base.OnStartup(e);
        }

        protected override void OnExit(ExitEventArgs e)
        {
            _pipeCts?.Cancel();
            _instanceMutex?.ReleaseMutex();
            _instanceMutex?.Dispose();
            base.OnExit(e);
        }

        // ── Named pipe server ─────────────────────────────────────────────────

        private async Task PipeServerLoopAsync(CancellationToken ct)
        {
            while (!ct.IsCancellationRequested)
            {
                try
                {
                    using var server = new NamedPipeServerStream(
                        PipeName,
                        PipeDirection.In,
                        NamedPipeServerStream.MaxAllowedServerInstances,
                        PipeTransmissionMode.Byte,
                        PipeOptions.Asynchronous);

                    await server.WaitForConnectionAsync(ct);

                    using var reader = new StreamReader(server);
                    var line = await reader.ReadLineAsync(ct);
                    if (!string.IsNullOrWhiteSpace(line))
                        Dispatcher.Invoke(() => PipeCommandReceived?.Invoke(line.Trim()));
                }
                catch (OperationCanceledException) { break; }
                catch { /* client disconnected or pipe error — restart loop */ }
            }
        }

        // ── Arg helpers ───────────────────────────────────────────────────────

        private static void ParseArgs(string[] args)
        {
            for (int i = 0; i < args.Length - 1; i++)
            {
                if (args[i].Equals("--relay", StringComparison.OrdinalIgnoreCase))
                    StartupRelayUrl = args[i + 1];
            }
        }

        private static void ForwardArgsToRunningInstance(string[] args)
        {
            for (int i = 0; i < args.Length - 1; i++)
            {
                if (!args[i].Equals("--relay", StringComparison.OrdinalIgnoreCase)) continue;
                try
                {
                    using var client = new NamedPipeClientStream(".", PipeName, PipeDirection.Out);
                    client.Connect(2000);
                    using var writer = new StreamWriter(client) { AutoFlush = true };
                    writer.WriteLine($"RELAY:{args[i + 1]}");
                }
                catch { }
                break;
            }

            try
            {
                using var client = new NamedPipeClientStream(".", PipeName, PipeDirection.Out);
                client.Connect(1000);
                using var writer = new StreamWriter(client) { AutoFlush = true };
                writer.WriteLine("FOCUS");
            }
            catch { }
        }
    }
}
