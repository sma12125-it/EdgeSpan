using System.Diagnostics;
using System.Net.Sockets;
using System.Text;
using EdgeSpan.Layout;
using EdgeSpan.Media;
using EdgeSpan.Session;

namespace EdgeSpan.Windows.Services;

/// <summary>
/// Settings → "Test handoff": walks every stage of the PC→tablet path locally (setting, link, listeners,
/// capture, encode, decode) and names the first stage that fails. Everything also goes to the diagnostics log.
/// </summary>
internal static class HandoffSelfTest
{
    public static string Run(HostController host)
    {
        var sb = new StringBuilder();
        var firstFail = "";
        void Line(bool ok, string stage, string detail)
        {
            var text = $"{(ok ? "✔" : "✘")} {stage}: {detail}";
            sb.AppendLine(text);
            DiagLog.Write("handoff selftest " + text);
            if (!ok && firstFail.Length == 0)
                firstFail = stage;
        }

        var s = host.Settings;
        Line(HandoffPolicy.AllowsPcToPhone(s.Handoff), "setting", $"Handoff={s.Handoff} (PC→tablet {(HandoffPolicy.AllowsPcToPhone(s.Handoff) ? "on" : "off")}, tablet→PC {(HandoffPolicy.AllowsPhoneToPc(s.Handoff) ? "on" : "off")})");

        var links = host.HandoffLinkReport().ToList();
        if (links.Count == 0)
            Line(false, "link", "no tablet connected");
        foreach (var l in links)
            sb.AppendLine("   " + l);
        var device = host.DeviceOnEdge(ScreenEdge.Right);
        if (device != null)
        {
            var blocker = host.HandoffBlocker(device);
            Line(blocker == null, "tablet", blocker == null ? $"{device} ready" : $"{device} blocked: {blocker}");
        }
        else if (links.Count > 0)
            Line(false, "tablet", "no established link");

        Line(Listening(27184), "listener 27184", Listening(27184) ? "video port open" : "nothing listening (handoff off or port busy)");
        Line(Listening(27186), "listener 27186", Listening(27186) ? "app-stream port open" : "nothing listening (tablet→PC off or port busy)");

        var target = HandoffHost.ListWindows().Select(w => w.Hwnd).FirstOrDefault();
        if (target == 0)
            target = Process.GetCurrentProcess().MainWindowHandle;
        if (target == 0)
        {
            Line(false, "capture", "no window to test with");
            return Finish(sb, firstFail);
        }

        var frames = 0;
        var h264 = 0;
        var jpeg = 0;
        byte[]? firstH264 = null;
        var reports = new List<string>();
        var until = Environment.TickCount64 + 2500;
        var t = new Thread(() =>
        {
            try
            {
                WindowEncoder.Run(target, "balanced", () => Environment.TickCount64 > until, () => frames == 0,
                    (data, _, flag) =>
                    {
                        frames++;
                        if (flag == JpegStill.Flag) jpeg++;
                        else
                        {
                            h264++;
                            firstH264 ??= data;
                        }
                    },
                    key => { lock (reports) reports.Add(key); });
            }
            catch (Exception ex)
            {
                lock (reports) reports.Add("exception " + ex.GetType().Name + ": " + ex.Message);
            }
        }) { IsBackground = true, Name = "edgestpan-selftest" };
        t.Start();
        t.Join(6000);
        var notes = reports.Count > 0 ? " [" + string.Join(", ", reports.Distinct()) + "]" : "";
        Line(frames > 0, "capture+encode", $"{frames} frames in 2.5 s (H.264 {h264}, JPEG fallback {jpeg}){notes}");
        if (firstH264 != null)
        {
            try
            {
                using var dec = H264Decoder.Create();
                if (dec == null)
                    Line(false, "decode", "no H.264 decoder on this PC (tablet decodes anyway)");
                else
                {
                    var img = dec.Decode(firstH264);
                    Line(img != null, "decode", img != null ? $"{dec.Width}x{dec.Height} luma {dec.Mean:F0}" : "first frame did not decode");
                }
            }
            catch (Exception ex)
            {
                Line(false, "decode", ex.Message);
            }
        }
        return Finish(sb, firstFail);
    }

    private static string Finish(StringBuilder sb, string firstFail)
    {
        var head = firstFail.Length == 0 ? "All stages passed." : $"First failing stage: {firstFail}";
        DiagLog.Write("handoff selftest result " + head);
        return head + Environment.NewLine + sb.ToString().TrimEnd();
    }

    private static bool Listening(int port)
    {
        try
        {
            using var c = new TcpClient();
            return c.ConnectAsync("127.0.0.1", port).Wait(500) && c.Connected;
        }
        catch
        {
            return false;
        }
    }
}
