using System.Security.Cryptography;
using ParentalControl.Domain.AdminAccess;

namespace ParentalControl.Core.Security;

/// <summary>
/// The QR code and the otpauth URI of the TOTP secret. The TOTP calculation and the check are in
/// <see cref="Totp"/> and <see cref="TotpSecret"/> (ParentalControl.Domain).
/// </summary>
public static class TotpService
{
    public static string GenerateSecret(int byteLength = TotpSecret.NewSecretBytes)
    {
        return TotpSecret.Encode(RandomNumberGenerator.GetBytes(byteLength));
    }

    public static string GenerateOtpauthUri(string issuer, string account, string secret)
    {
        var escapedIssuer = Uri.EscapeDataString(issuer);
        var escapedAccount = Uri.EscapeDataString(account);
        return $"otpauth://totp/{escapedIssuer}:{escapedAccount}?secret={secret}&issuer={escapedIssuer}&algorithm=SHA1&digits=6&period=30";
    }

    public static byte[] GenerateQrCodePng(string uri, int pixelsPerModule = 5)
    {
        using var qrGenerator = new QRCoder.QRCodeGenerator();
        using var qrCodeData = qrGenerator.CreateQrCode(uri, QRCoder.QRCodeGenerator.ECCLevel.M);
        var qrCode = new QRCoder.PngByteQRCode(qrCodeData);
        return qrCode.GetGraphic(pixelsPerModule);
    }

    /// <summary>Checks a code of the current time steps −1, 0 and +1, without the replay guard.</summary>
    public static bool VerifyCode(string secret, string code)
    {
        return Totp.TryVerify(secret, code, DateTimeOffset.UtcNow, Totp.NoStepUsed, out _);
    }
}
