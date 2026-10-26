using System.Diagnostics;
using System.Net;
using EdgeSpan.Net;

namespace EdgeSpan.Core.Tests;

public class RelayLiveTests
{
    [Fact]
    public async Task Relay_forwards_signal_and_ciphertext()
    {
        var root = RepoRoot();
        var binary = Path.Combine(Path.GetTempPath(), "edgestpan-relay-" + Environment.ProcessId);
        var build = Process.Start(new ProcessStartInfo("go", $"build -o \"{binary}\" ./cmd/relay")
        {
            WorkingDirectory = Path.Combine(root, "relay"),
            RedirectStandardError = true
        })!;
        await build.WaitForExitAsync();
        Assert.Equal(0, build.ExitCode);
        var proc = Process.Start(new ProcessStartInfo(binary, "-http 127.0.0.1:18787 -udp 127.0.0.1:18788")
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true
        })!;
        try
        {
            await Task.Delay(400);
            await using var host = new RelayClient();
            await using var phone = new RelayClient();
            await host.ConnectAsync("http://127.0.0.1:18787", "Office", "host", CancellationToken.None);
            await phone.ConnectAsync("http://127.0.0.1:18787", "Tablet", "client", CancellationToken.None);
            var signal = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
            phone.MessageReceived += message =>
            {
                if (message.Op == "signal")
                    signal.TrySetResult(message.Data ?? "");
            };
            await host.SendSignalAsync(phone.PeerId!, "{\"t\":\"announce\"}", CancellationToken.None);
            Assert.Equal("{\"t\":\"announce\"}", await signal.Task.WaitAsync(TimeSpan.FromSeconds(3)));

            var payload = new TaskCompletionSource<byte[]>(TaskCreationOptions.RunContinuationsAsynchronously);
            phone.PayloadReceived += (_, bytes) => payload.TrySetResult(bytes);
            var body = new byte[] { 9, 8, 7 };
            for (var i = 0; i < 5 && !payload.Task.IsCompleted; i++)
            {
                await host.SendPayloadAsync(phone.PeerId!, body, CancellationToken.None);
                await Task.Delay(50);
            }
            Assert.Equal(body, await payload.Task.WaitAsync(TimeSpan.FromSeconds(3)));
            var mapped = await StunQuery.BindingAsync("127.0.0.1", 18788, TimeSpan.FromSeconds(2), CancellationToken.None);
            Assert.NotNull(mapped);
            Assert.Equal(IPAddress.Loopback, mapped!.Address);
        }
        finally
        {
            try { proc.Kill(entireProcessTree: true); } catch (Exception) { }
            try { File.Delete(binary); } catch (Exception) { }
        }
    }

    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null)
        {
            if (File.Exists(Path.Combine(dir.FullName, "relay", "go.mod")))
                return dir.FullName;
            dir = dir.Parent;
        }
        throw new InvalidOperationException("Could not find the EdgeSpan repository root.");
    }
}
