using System.Security.Cryptography;
using System.Text;

namespace EdgeSpan.Product;

public enum LicenseEdition
{
    Trial = 0,
    Pro = 1,
    Family = 2
}

public sealed record LicenseStatus(bool IsValid, LicenseEdition Edition, string Code, DateTimeOffset? ExpiresUtc, int DaysLeft);

/// <summary>
/// Development-only license check. The secret lives in the binary on purpose so a future
/// sales service can replace <see cref="ILicenseValidator"/> without changing the UI.
/// </summary>
public interface ILicenseValidator
{
    LicenseStatus Evaluate(string? key, DateTimeOffset firstRunUtc, DateTimeOffset now);
}

public sealed class DevLicenseValidator : ILicenseValidator
{
    public const string DevSecret = "edgestpan-dev-license-v1";
    private const string Alphabet = "ABCDEFGHJKLMNPQRSTUVWXYZ23456789";

    public LicenseStatus Evaluate(string? key, DateTimeOffset firstRunUtc, DateTimeOffset now)
    {
        if (!string.IsNullOrWhiteSpace(key))
        {
            if (TryParse(key.Trim(), out var edition))
                return new LicenseStatus(true, edition, "licensed", null, -1);
            return new LicenseStatus(false, LicenseEdition.Trial, "invalid", null, 0);
        }

        var expires = firstRunUtc.AddDays(14);
        if (now <= expires)
        {
            var days = Math.Max(0, (int)Math.Ceiling((expires - now).TotalDays));
            return new LicenseStatus(true, LicenseEdition.Trial, "trial", expires, days);
        }
        return new LicenseStatus(false, LicenseEdition.Trial, "expired", expires, 0);
    }

    public static string SignDevKey(string edition, string body)
    {
        var payload = $"ES-{edition}-{body}";
        var mac = HMACSHA256.HashData(Encoding.UTF8.GetBytes(DevSecret), Encoding.UTF8.GetBytes(payload));
        return payload + "-" + Convert.ToHexString(mac)[..4];
    }

    private static bool TryParse(string key, out LicenseEdition edition)
    {
        edition = LicenseEdition.Trial;
        var parts = key.ToUpperInvariant().Split('-');
        if (parts.Length != 4 || parts[0] != "ES")
            return false;
        edition = parts[1] switch
        {
            "PR" => LicenseEdition.Pro,
            "FM" => LicenseEdition.Family,
            _ => LicenseEdition.Trial
        };
        if (edition == LicenseEdition.Trial)
            return false;
        if (parts[2].Length != 8 || parts[2].Any(c => !Alphabet.Contains(c)))
            return false;
        if (parts[3].Length != 4)
            return false;
        var expect = SignDevKey(parts[1], parts[2]);
        var actualSig = parts[3];
        var expectSig = expect.Split('-')[^1];
        return CryptographicOperations.FixedTimeEquals(
            Encoding.ASCII.GetBytes(expectSig),
            Encoding.ASCII.GetBytes(actualSig));
    }
}
