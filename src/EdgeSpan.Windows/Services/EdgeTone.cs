using System.Runtime.InteropServices;

namespace EdgeSpan.Windows.Services;

/// <summary>A short tone. The switch ships off, so this stays silent unless asked.</summary>
internal static class EdgeTone
{
    private static byte[]? _wav;

    public static void Play()
    {
        if (!OperatingSystem.IsWindows())
            return;
        _wav ??= Build();
        var copy = _wav;
        ThreadPool.QueueUserWorkItem(_ => PlaySound(copy, IntPtr.Zero, 0x0005));
    }

    private static byte[] Build()
    {
        const int rate = 22050;
        const int samples = 2205;
        var data = new byte[44 + samples * 2];
        void W(int at, string s)
        {
            for (var i = 0; i < s.Length; i++)
                data[at + i] = (byte)s[i];
        }
        W(0, "RIFF");
        BitConverter.GetBytes(data.Length - 8).CopyTo(data, 4);
        W(8, "WAVE");
        W(12, "fmt ");
        BitConverter.GetBytes(16).CopyTo(data, 16);
        BitConverter.GetBytes((short)1).CopyTo(data, 20);
        BitConverter.GetBytes((short)1).CopyTo(data, 22);
        BitConverter.GetBytes(rate).CopyTo(data, 24);
        BitConverter.GetBytes(rate * 2).CopyTo(data, 28);
        BitConverter.GetBytes((short)2).CopyTo(data, 32);
        BitConverter.GetBytes((short)16).CopyTo(data, 34);
        W(36, "data");
        BitConverter.GetBytes(samples * 2).CopyTo(data, 40);
        for (var i = 0; i < samples; i++)
        {
            var env = Math.Exp(-i / 700.0);
            var s = Math.Sin(2 * Math.PI * 880 * i / rate) * 0.18 * env;
            BitConverter.GetBytes((short)(s * short.MaxValue)).CopyTo(data, 44 + i * 2);
        }
        return data;
    }

    [DllImport("winmm.dll", CharSet = CharSet.Unicode)]
    private static extern bool PlaySound(byte[] sound, IntPtr module, uint flags);
}
