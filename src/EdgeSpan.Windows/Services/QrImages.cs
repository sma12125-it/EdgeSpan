using Avalonia.Media.Imaging;
using QRCoder;

namespace EdgeSpan.Windows.Services;

public static class QrImages
{
    public static Bitmap? Make(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return null;
        using var generator = new QRCodeGenerator();
        using var data = generator.CreateQrCode(text, QRCodeGenerator.ECCLevel.M);
        var png = new PngByteQRCode(data).GetGraphic(6);
        return new Bitmap(new MemoryStream(png));
    }
}
