using System.Diagnostics;
using EdgeSpan.Net;

namespace EdgeSpan.Windows.Services;

public sealed class UsbHooks
{
    public required Func<bool> IsUsbConnected { get; init; }
    public required Func<bool> PairingOpen { get; init; }
    public required Action OpenPairing { get; init; }
    public required Func<string> Pin { get; init; }
    public required Action<string, string> Phase { get; init; }
    public Action<string>? Trace { get; init; }
    public Action<string>? Inject { get; init; }
}

/// <summary>Finds a phone with the bundled adb, then tunnels the session and the injection server.</summary>
public sealed class UsbLink : IDisposable
{
    private CancellationTokenSource? _cts;

    public void Start(UsbHooks hooks)
    {
        Stop();
        var cts = new CancellationTokenSource();
        _cts = cts;
        _ = Task.Run(async () =>
        {
            var adb = FindAdb();
            if (adb == null)
            {
                hooks.Phase("failed", "usbAdbMissing");
                hooks.Trace?.Invoke("adb-missing");
                return;
            }
            hooks.Trace?.Invoke("adb " + adb);
            var conductor = new UsbConductor
            {
                IsConnected = hooks.IsUsbConnected,
                PairingOpen = hooks.PairingOpen,
                OpenPairing = hooks.OpenPairing,
                Pin = hooks.Pin,
                Phase = hooks.Phase,
                Trace = hooks.Trace,
                Inject = hooks.Inject,
                DexPath = FindServer,
                StartUhid = !string.Equals(Environment.GetEnvironmentVariable("EDGESPAN_NO_UHID"), "1", StringComparison.Ordinal),
                PreferSerial = Environment.GetEnvironmentVariable("EDGESPAN_ADB_SERIAL")
            };
            try
            {
                await conductor.Run(new ProcessAdb(adb), cts.Token);
            }
            catch (OperationCanceledException)
            {
            }
            catch (ObjectDisposedException)
            {
            }
            catch (Exception ex)
            {
                hooks.Phase("failed", "connectFailed");
                hooks.Trace?.Invoke("usb-failed " + ex.Message);
            }
        });
    }

    public void Stop()
    {
        var cts = Interlocked.Exchange(ref _cts, null);
        if (cts == null)
            return;
        cts.Cancel();
        cts.Dispose();
    }

    public void Dispose() => Stop();

    public static string? FindAdb()
    {
        var env = Environment.GetEnvironmentVariable("EDGESPAN_ADB");
        if (!string.IsNullOrWhiteSpace(env) && File.Exists(env))
            return env;
        var names = OperatingSystem.IsWindows() ? new[] { "adb.exe" } : new[] { "adb" };
        var roots = new List<string> { AppContext.BaseDirectory, Path.Combine(AppContext.BaseDirectory, "platform-tools") };
        foreach (var key in new[] { "ANDROID_HOME", "ANDROID_SDK_ROOT" })
        {
            var home = Environment.GetEnvironmentVariable(key);
            if (!string.IsNullOrWhiteSpace(home))
                roots.Add(Path.Combine(home, "platform-tools"));
        }
        var path = Environment.GetEnvironmentVariable("PATH");
        if (!string.IsNullOrWhiteSpace(path))
            roots.AddRange(path.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries));
        foreach (var root in roots)
        {
            foreach (var name in names)
            {
                var candidate = Path.Combine(root, name);
                if (File.Exists(candidate))
                    return candidate;
            }
        }
        return null;
    }

    public static string? FindServer()
    {
        var env = Environment.GetEnvironmentVariable("EDGESPAN_SERVER");
        if (!string.IsNullOrWhiteSpace(env) && File.Exists(env))
            return env;
        var dirs = new List<string> { AppContext.BaseDirectory };
        var process = Environment.ProcessPath;
        if (!string.IsNullOrWhiteSpace(process))
        {
            var dir = Path.GetDirectoryName(process);
            if (!string.IsNullOrWhiteSpace(dir))
                dirs.Add(dir);
        }
        foreach (var name in new[] { "edgestpan-server.dex", "edgestpan-server.bin" })
        {
            foreach (var dir in dirs)
            {
                var path = Path.Combine(dir, name);
                if (File.Exists(path))
                    return path;
            }
        }
        return null;
    }

    private sealed class ProcessAdb : IAdbClient
    {
        private readonly string _adb;
        public ProcessAdb(string adb) => _adb = adb;

        public async Task<AdbCall> Call(int timeoutMs, CancellationToken cancel, params string[] args)
        {
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancel);
            linked.CancelAfter(timeoutMs);
            var psi = new ProcessStartInfo(_adb)
            {
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true
            };
            foreach (var arg in args)
                psi.ArgumentList.Add(arg);
            Process? process = null;
            try
            {
                process = Process.Start(psi);
                if (process == null)
                    return new AdbCall(-1, "adb", false);
                var stdout = process.StandardOutput.ReadToEndAsync(linked.Token);
                var stderr = process.StandardError.ReadToEndAsync(linked.Token);
                await process.WaitForExitAsync(linked.Token);
                var text = "";
                try { text = await stdout + await stderr; } catch (OperationCanceledException) { }
                return new AdbCall(process.ExitCode, text, false);
            }
            catch (OperationCanceledException) when (!cancel.IsCancellationRequested)
            {
                try { process?.Kill(entireProcessTree: true); } catch (Exception) { }
                return new AdbCall(-1, "timeout", true);
            }
            finally
            {
                process?.Dispose();
            }
        }
    }
}
