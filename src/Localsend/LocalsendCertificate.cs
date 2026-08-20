using System;
using System.IO;
using System.Security.Cryptography.X509Certificates;
using System.Security.Cryptography;

namespace FileShare.Localsend
{
    /// <summary>
    /// Small helper to load certificate and compute fingerprint.
    /// </summary>
    public static class LocalsendCertificate
    {
        public static X509Certificate2? LoadPfx(string pfxPath, string? password)
        {
            if (!File.Exists(pfxPath)) return null;
            return string.IsNullOrEmpty(password) ? new X509Certificate2(pfxPath) : new X509Certificate2(pfxPath, password);
        }

        public static string ComputeSha256Hex(X509Certificate2 cert)
        {
            using var sha = SHA256.Create();
            var h = sha.ComputeHash(cert.RawData);
            return BitConverter.ToString(h).Replace("-", "").ToLowerInvariant();
        }
    }
}
