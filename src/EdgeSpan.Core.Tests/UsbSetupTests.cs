using EdgeSpan.Net;

namespace EdgeSpan.Tests;

public class UsbSetupTests
{
    [Fact]
    public async Task Removes_stale_rules_before_opening_the_tunnel()
    {
        var adb = new ScriptAdb(args =>
        {
            var line = string.Join(" ", args);
            if (line == "devices")
                return new AdbCall(0, "List of devices attached\nemulator-5554\tdevice\n", false);
            if (line.Contains("reverse tcp:45455"))
                return new AdbCall(0, "", false);
            return new AdbCall(0, line.Contains("cat ") ? "edgestpan-server mouse-relative 27183 uhid=true\n" : "", false);
        });
        var hooks = new Probe();
        hooks.ConnectedAfter = "usb-launch";
        await RunOnce(adb, hooks);
        var reverseAt = adb.Calls.FindIndex(c => c.Contains("reverse tcp:45455 tcp:45455"));
        var removeAt = adb.Calls.FindIndex(c => c.Contains("reverse --remove tcp:45455"));
        var forwardRemove = adb.Calls.FindIndex(c => c.Contains("forward --remove tcp:27183"));
        Assert.True(removeAt >= 0 && removeAt < reverseAt);
        Assert.True(forwardRemove >= 0 && forwardRemove < reverseAt);
        Assert.Contains(adb.Calls, c => c.Contains("am start"));
        Assert.Equal("injectUhid", hooks.InjectNote);
    }

    [Fact]
    public async Task Version_mismatch_restarts_our_adb_server()
    {
        var devices = 0;
        var adb = new ScriptAdb(args =>
        {
            var line = string.Join(" ", args);
            if (line == "devices")
            {
                devices++;
                if (devices == 1)
                    return new AdbCall(1, "adb server version (41) doesn't match this client (36); killing...\n", false);
                return new AdbCall(0, "List of devices attached\nR58M\tdevice\n", false);
            }
            if (line.Contains("cat "))
                return new AdbCall(0, "uhid-denied SecurityException\n", false);
            return new AdbCall(0, "", false);
        });
        var hooks = new Probe { ConnectedAfter = "usb-launch" };
        await RunOnce(adb, hooks);
        Assert.Contains(adb.Calls, c => c == "kill-server");
        Assert.Equal("injectDown", hooks.InjectNote);
        Assert.DoesNotContain(hooks.Phases, p => p.Phase == "unauthorized");
    }

    [Fact]
    public async Task Timeout_does_not_wait_forever_and_restarts_adb()
    {
        var devices = 0;
        var adb = new ScriptAdb(args =>
        {
            var line = string.Join(" ", args);
            if (line == "devices")
            {
                devices++;
                if (devices == 1)
                    return new AdbCall(-1, "timeout", true);
                return new AdbCall(0, "List of devices attached\nemulator-5554\tdevice\n", false);
            }
            if (line.Contains("cat "))
                return new AdbCall(0, "edgestpan-server mouse-relative 27183 uhid=true\n", false);
            return new AdbCall(0, "", false);
        });
        var hooks = new Probe { ConnectedAfter = "usb-launch" };
        await RunOnce(adb, hooks);
        Assert.Contains(adb.Calls, c => c == "kill-server");
        Assert.Contains(hooks.Phases, p => p.Detail == "usbAdbRestart");
        Assert.Contains(hooks.Phases, p => p.Detail == "pairOkUsb");
    }

    [Fact]
    public async Task Missing_phone_explains_the_samsung_driver_case()
    {
        var adb = new ScriptAdb(_ => new AdbCall(0, "List of devices attached\n", false));
        var hooks = new Probe();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(4));
        await new UsbConductor
        {
            IsConnected = () => false,
            PairingOpen = () => true,
            OpenPairing = () => { },
            Pin = () => "123456",
            Phase = hooks.Mark,
            Trace = hooks.Log,
            Inject = note => hooks.InjectNote = note,
            DexPath = () => null,
            StartUhid = false
        }.Run(adb, cts.Token);
        Assert.Contains(hooks.Phases, p => p.Phase == "needDebug" && p.Detail == "usbNoDevice");
        Assert.DoesNotContain(adb.Calls, c => c.Contains("reverse tcp:"));
    }

    [Fact]
    public async Task Unauthorized_phone_stops_before_the_tunnel()
    {
        var adb = new ScriptAdb(args =>
            string.Join(" ", args) == "devices"
                ? new AdbCall(0, "List of devices attached\nR58M\tunauthorized\n", false)
                : new AdbCall(0, "", false));
        var hooks = new Probe();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(3));
        await new UsbConductor
        {
            IsConnected = () => false,
            PairingOpen = () => true,
            OpenPairing = () => { },
            Pin = () => "123456",
            Phase = hooks.Mark,
            Trace = hooks.Log,
            Inject = note => hooks.InjectNote = note
        }.Run(adb, cts.Token);
        Assert.Contains(hooks.Phases, p => p.Phase == "unauthorized" && p.Detail == "usbUnauthorized");
        Assert.DoesNotContain(adb.Calls, c => c.Contains("reverse tcp:"));
    }

    [Fact]
    public async Task Unplug_tears_down_and_reconnects_without_the_grace_lockout()
    {
        var plugged = true;
        var launched = false;
        var connects = 0;
        var drops = 0;
        var adb = new ScriptAdb(args =>
        {
            var line = string.Join(" ", args);
            if (line == "devices")
                return new AdbCall(0, plugged ? "List of devices attached\nR58M\tdevice\n" : "List of devices attached\n", false);
            return new AdbCall(0, line.Contains("cat ") ? "edgestpan-server mouse-relative 27183 uhid=true\n" : "", false);
        });
        var hooks = new Probe();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        string? last = null;
        await new UsbConductor
        {
            IsConnected = () => launched && plugged,
            PairingOpen = () => false,
            OpenPairing = () => { },
            Pin = () => "445566",
            WatchMs = 50,
            ReconnectGraceMs = 60000, // must not block reconnect after a real unplug
            Drop = () =>
            {
                drops++;
                launched = false;
                plugged = true; // cable goes back in right away
            },
            Phase = (phase, detail) =>
            {
                hooks.Mark(phase, detail);
                if (phase == "connected" && last != "connected")
                {
                    connects++;
                    if (connects == 1)
                        plugged = false; // pull the cable
                    else
                        cts.Cancel();
                }
                last = phase;
            },
            Trace = line =>
            {
                hooks.Log(line);
                if (line.StartsWith("usb-launch ", StringComparison.Ordinal))
                    launched = true;
            },
            Inject = note => hooks.InjectNote = note,
            DexPath = () => Path.GetTempFileName()
        }.Run(adb, cts.Token);
        Assert.Equal(2, connects);
        Assert.Equal(1, drops);
        Assert.Contains(hooks.Phases, p => p.Phase == "disconnected");
        Assert.Contains(hooks.Phases, p => p.Phase == "reconnecting");
        Assert.Contains(hooks.Lines, l => l.StartsWith("usb-device-removed", StringComparison.Ordinal));
        Assert.Contains(hooks.Lines, l => l.StartsWith("usb-teardown", StringComparison.Ordinal));
        Assert.Equal(2, adb.Calls.Count(c => c.Contains("reverse tcp:45455 tcp:45455")));
    }

    private static async Task RunOnce(ScriptAdb adb, Probe hooks)
    {
        var launched = false;
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(8));
        await new UsbConductor
        {
            IsConnected = () => launched,
            PairingOpen = () => false,
            OpenPairing = () => { },
            Pin = () => "445566",
            Phase = (phase, detail) =>
            {
                hooks.Mark(phase, detail);
                if (phase == "connected")
                    cts.Cancel();
            },
            Trace = line =>
            {
                hooks.Log(line);
                if (line.StartsWith("usb-launch ", StringComparison.Ordinal))
                    launched = true;
            },
            Inject = note => hooks.InjectNote = note,
            DexPath = () => Path.GetTempFileName(),
            StartUhid = true
        }.Run(adb, cts.Token);
    }

    private sealed class Probe
    {
        public List<(string Phase, string Detail)> Phases { get; } = new();
        public List<string> Lines { get; } = new();
        public string InjectNote { get; set; } = "";
        public string ConnectedAfter { get; set; } = "";
        public void Mark(string phase, string detail) => Phases.Add((phase, detail));
        public void Log(string line) => Lines.Add(line);
    }

    private sealed class ScriptAdb : IAdbClient
    {
        private readonly Func<string[], AdbCall> _handle;
        public List<string> Calls { get; } = new();
        public ScriptAdb(Func<string[], AdbCall> handle) => _handle = handle;
        public Task<AdbCall> Call(int timeoutMs, CancellationToken cancel, params string[] args)
        {
            var line = string.Join(" ", args);
            Calls.Add(line);
            return Task.FromResult(_handle(args));
        }
    }
}
