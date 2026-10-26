namespace EdgeSpan.Net;

public interface IAdbClient
{
    Task<AdbCall> Call(int timeoutMs, CancellationToken cancel, params string[] args);
}

public readonly record struct AdbCall(int Code, string Text, bool TimedOut)
{
    public bool NeedsRestart =>
        TimedOut
        || Text.Contains("doesn't match this client", StringComparison.OrdinalIgnoreCase)
        || Text.Contains("protocol fault", StringComparison.OrdinalIgnoreCase)
        || Text.Contains("failed to start daemon", StringComparison.OrdinalIgnoreCase)
        || Text.Contains("connection reset", StringComparison.OrdinalIgnoreCase);
}

/// <summary>
/// Opens the USB session one step at a time. Each adb call has a timeout.
/// Stale reverses are removed before they are added again, and a mismatched
/// adb server is restarted with the adb binary we invoked.
/// </summary>
public sealed class UsbConductor
{
    public const int StepTimeoutMs = 8000;
    public const int JoinTimeoutMs = 15000;
    public const string AccessComponent = "com.edgestpan.app/com.edgestpan.app.EdgeAccessibility";
    public const string PackageName = "com.edgestpan.app";

    public required Func<bool> IsConnected { get; init; }
    public required Func<bool> PairingOpen { get; init; }
    public required Action OpenPairing { get; init; }
    public required Func<string> Pin { get; init; }
    public required Action<string, string> Phase { get; init; }
    public Action<string>? Trace { get; init; }
    public Action<string>? Inject { get; init; }
    public Func<string?>? DexPath { get; init; }
    public bool StartUhid { get; init; } = true;
    public string? PreferSerial { get; init; }

    /// <summary>How long the session may look disconnected before the USB setup is redone.</summary>
    public int ReconnectGraceMs { get; init; } = 4000;

    public async Task Run(IAdbClient adb, CancellationToken token)
    {
        var lastConnected = long.MinValue;
        while (!token.IsCancellationRequested)
        {
            try
            {
                if (IsConnected())
                {
                    lastConnected = Environment.TickCount64;
                    Phase("connected", "pairOkUsb");
                    await Pause(1000, token);
                    continue;
                }
                // A short blip of IsConnected() must not rerun the setup: StartInjection used to
                // kill and restart the injection server, which silently broke the phone's socket
                // to it and left "control on Android" with no cursor and no input.
                if (lastConnected != long.MinValue && Environment.TickCount64 - lastConnected < ReconnectGraceMs)
                {
                    await Pause(500, token);
                    continue;
                }
                var serial = await Prepare(adb, token);
                if (serial == null)
                {
                    await Pause(2000, token);
                    continue;
                }
                if (!await OpenTunnel(adb, serial, token))
                {
                    await Pause(2000, token);
                    continue;
                }
                await StartInjection(adb, serial, token);
                await EnableAccess(adb, serial, token);
                await LaunchPhone(adb, serial, token);
                if (await WaitForPhone(token))
                    continue;
                Phase("failed", "usbJoinTimeout");
                await Pause(2000, token);
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }
    }

    private async Task<string?> Prepare(IAdbClient adb, CancellationToken token)
    {
        Phase("searching", "usbStepAdb");
        var started = await Call(adb, token, "start-server");
        if (started.NeedsRestart)
        {
            Phase("searching", "usbAdbRestart");
            await Restart(adb, token);
        }
        Phase("searching", "usbSearching");
        var listed = await Devices(adb, token);
        if (listed.NeedsRestart)
        {
            Phase("searching", "usbAdbRestart");
            await Restart(adb, token);
            listed = await Devices(adb, token);
        }
        if (listed.NeedsRestart)
        {
            Phase("failed", "usbAdbTimeout");
            return null;
        }
        var serial = Pick(AdbText.Parse(listed.Text), out var kind);
        if (serial != null)
            return serial;
        if (kind == "unauthorized")
        {
            Phase("unauthorized", "usbUnauthorized");
            return null;
        }
        if (kind == "offline")
        {
            Phase("offline", "usbOffline");
            return null;
        }
        await Pause(1200, token);
        listed = await Devices(adb, token);
        serial = Pick(AdbText.Parse(listed.Text), out kind);
        if (serial != null)
            return serial;
        if (kind == "unauthorized")
        {
            Phase("unauthorized", "usbUnauthorized");
            return null;
        }
        if (kind == "offline")
        {
            Phase("offline", "usbOffline");
            return null;
        }
        Phase("needDebug", "usbNoDevice");
        return null;
    }

    private async Task<bool> OpenTunnel(IAdbClient adb, string serial, CancellationToken token)
    {
        Phase("connecting", "usbStepReverse");
        await RemoveRules(adb, serial, token);
        var reverse = await Reverse(adb, serial, token);
        if (reverse.Code != 0 || reverse.NeedsRestart)
        {
            Phase("searching", "usbAdbRestart");
            await Restart(adb, token);
            await RemoveRules(adb, serial, token);
            reverse = await Reverse(adb, serial, token);
        }
        if (reverse.Code != 0 || reverse.TimedOut)
        {
            Phase("failed", "usbReverseFail");
            Note("usb-reverse-failed " + First(reverse.Text));
            return false;
        }
        Note("usb-reverse " + serial);
        var video = await Call(adb, token, "-s", serial, "reverse", "tcp:" + Product.ProductInfo.HandoffPort, "tcp:" + Product.ProductInfo.HandoffPort);
        Note(video.Code == 0 && !video.TimedOut ? "usb-video-reverse" : "usb-video-reverse-failed " + First(video.Text));
        Phase("connecting", "usbStepForward");
        var forward = await Call(adb, token, "-s", serial, "forward", "tcp:" + Product.ProductInfo.AdbServerPort, "tcp:" + Product.ProductInfo.AdbServerPort);
        if (forward.Code != 0 || forward.TimedOut)
            Note("usb-forward-failed " + First(forward.Text));
        return true;
    }

    private async Task StartInjection(IAdbClient adb, string serial, CancellationToken token)
    {
        Phase("connecting", "usbStepInject");
        if (!StartUhid)
        {
            Inject?.Invoke("injectDown");
            Note("uhid-skipped");
            return;
        }
        var dex = DexPath?.Invoke();
        if (string.IsNullOrWhiteSpace(dex) || !File.Exists(dex))
        {
            Inject?.Invoke("injectDown");
            Note("uhid-dex-missing");
            return;
        }
        var alive = await Call(adb, token, "-s", serial, "shell", "sh", "-c",
            "pid=$(cat /data/local/tmp/edgestpan-server.pid 2>/dev/null); [ -n \"$pid\" ] && kill -0 $pid 2>/dev/null && echo server-alive");
        if ((alive.Text ?? "").Contains("server-alive", StringComparison.Ordinal))
        {
            // Keep the running server so a connected phone keeps its injection socket.
            Inject?.Invoke("injectUhid");
            Note("uhid-kept");
            return;
        }
        await Call(adb, token, "-s", serial, "shell", "sh", "-c", "pid=$(cat /data/local/tmp/edgestpan-server.pid 2>/dev/null); [ -n \"$pid\" ] && kill $pid");
        var pushed = await Call(adb, token, "-s", serial, "push", dex, "/data/local/tmp/edgestpan-server.dex");
        if (pushed.Code != 0 || pushed.TimedOut)
        {
            Inject?.Invoke("injectDown");
            Note("uhid-push-failed " + First(pushed.Text));
            return;
        }
        var started = await Call(adb, token, "-s", serial, "shell", "sh", "-c",
            "rm -f /data/local/tmp/edgestpan-server.log; CLASSPATH=/data/local/tmp/edgestpan-server.dex setsid app_process / com.edgestpan.server.ServerMain >/data/local/tmp/edgestpan-server.log 2>&1 </dev/null & echo $! > /data/local/tmp/edgestpan-server.pid; echo uhid-started");
        if (started.TimedOut)
            Note("uhid-start-timeout");
        await Pause(700, token);
        var log = await Call(adb, token, "-s", serial, "shell", "cat", "/data/local/tmp/edgestpan-server.log");
        var body = log.Text ?? "";
        if (body.Contains("edgestpan-server", StringComparison.Ordinal) && !body.Contains("uhid-denied", StringComparison.Ordinal))
        {
            Inject?.Invoke("injectUhid");
            Note("uhid-ready");
        }
        else
        {
            Inject?.Invoke("injectDown");
            Note("uhid-denied " + First(body));
        }
    }

    private async Task EnableAccess(IAdbClient adb, string serial, CancellationToken token)
    {
        Phase("connecting", "usbStepAccess");
        var current = await Call(adb, token, "-s", serial, "shell", "settings", "get", "secure", "enabled_accessibility_services");
        var text = First(current.Text);
        if (text == "null")
            text = "";
        if (!text.Contains(AccessComponent, StringComparison.Ordinal))
        {
            var value = string.IsNullOrWhiteSpace(text) ? AccessComponent : text + ":" + AccessComponent;
            var put = await Call(adb, token, "-s", serial, "shell", "settings", "put", "secure", "enabled_accessibility_services", value);
            Note(put.Code == 0 && !put.TimedOut ? "access-enabled" : "access-enable-failed " + First(put.Text));
        }
        else
        {
            Note("access-already");
        }
        // Writing accessibility_enabled again rebinds the service (onUnbind clears its instance),
        // so only write it when it is actually off.
        var enabled = await Call(adb, token, "-s", serial, "shell", "settings", "get", "secure", "accessibility_enabled");
        if (First(enabled.Text) != "1")
            await Call(adb, token, "-s", serial, "shell", "settings", "put", "secure", "accessibility_enabled", "1");
        await Call(adb, token, "-s", serial, "shell", "dumpsys", "deviceidle", "whitelist", "+" + PackageName);
    }

    private async Task LaunchPhone(IAdbClient adb, string serial, CancellationToken token)
    {
        Phase("connecting", "usbStepLaunch");
        if (!PairingOpen())
            OpenPairing();
        var pin = Pin();
        if (string.IsNullOrWhiteSpace(pin))
        {
            Note("usb-launch-failed empty-pin");
            return;
        }
        var started = await Call(adb, token, "-s", serial, "shell", "am", "start", "-n",
            "com.edgestpan.app/.MainActivity", "--es", "code", pin, "--es", "method", "usb", "--ez", "auto", "true");
        Note(started.Code == 0 && !started.TimedOut ? "usb-launch " + pin : "usb-launch-failed " + First(started.Text));
        var pinFile = Environment.GetEnvironmentVariable("EDGESPAN_PIN_FILE");
        if (!string.IsNullOrWhiteSpace(pinFile))
        {
            try { File.WriteAllText(pinFile, pin); }
            catch (IOException) { }
        }
    }

    private async Task<bool> WaitForPhone(CancellationToken token)
    {
        Phase("connecting", "usbStepWait");
        var until = Environment.TickCount64 + JoinTimeoutMs;
        while (!token.IsCancellationRequested && Environment.TickCount64 < until)
        {
            if (IsConnected())
            {
                Phase("connected", "pairOkUsb");
                return true;
            }
            await Pause(400, token);
        }
        return IsConnected();
    }

    private string? Pick(IReadOnlyList<AdbDevice> devices, out string kind)
    {
        kind = "";
        var prefer = PreferSerial;
        var ready = devices.FirstOrDefault(d => d.State == "device" && (string.IsNullOrWhiteSpace(prefer) || d.Serial == prefer));
        if (ready == null && string.IsNullOrWhiteSpace(prefer))
            ready = devices.FirstOrDefault(d => d.State == "device");
        if (ready != null)
            return ready.Serial;
        if (devices.Any(d => d.State == "unauthorized"))
        {
            kind = "unauthorized";
            return null;
        }
        if (devices.Any(d => d.State == "offline"))
        {
            kind = "offline";
            return null;
        }
        kind = "none";
        return null;
    }

    private async Task<AdbCall> Devices(IAdbClient adb, CancellationToken token) =>
        await Call(adb, token, "devices");

    private async Task RemoveRules(IAdbClient adb, string serial, CancellationToken token)
    {
        var port = Product.ProductInfo.DefaultSessionPort.ToString();
        var inject = Product.ProductInfo.AdbServerPort.ToString();
        var video = Product.ProductInfo.HandoffPort.ToString();
        await Call(adb, token, "-s", serial, "reverse", "--remove", "tcp:" + port);
        await Call(adb, token, "-s", serial, "reverse", "--remove", "tcp:" + video);
        await Call(adb, token, "-s", serial, "forward", "--remove", "tcp:" + inject);
    }

    private async Task<AdbCall> Reverse(IAdbClient adb, string serial, CancellationToken token)
    {
        var port = Product.ProductInfo.DefaultSessionPort.ToString();
        return await Call(adb, token, "-s", serial, "reverse", "tcp:" + port, "tcp:" + port);
    }

    private async Task Restart(IAdbClient adb, CancellationToken token)
    {
        Note("adb-restart");
        await Call(adb, token, "kill-server");
        await Call(adb, token, "start-server");
    }

    private async Task<AdbCall> Call(IAdbClient adb, CancellationToken token, params string[] args)
    {
        var result = await adb.Call(StepTimeoutMs, token, args);
        if (result.NeedsRestart)
            Note("adb-broken " + args.FirstOrDefault() + " " + First(result.Text));
        return result;
    }

    private async Task Pause(int ms, CancellationToken token) =>
        await Task.Delay(ms, token);

    private void Note(string line) => Trace?.Invoke(line);

    private static string First(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return "";
        var line = text.Split('\n').Select(s => s.Trim()).FirstOrDefault(s => s.Length > 0);
        return line ?? "";
    }
}
