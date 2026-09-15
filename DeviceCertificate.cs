using System;
using System.IO;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;

namespace WpfApp3
{
    public static class DeviceCertificate
    {
        private static readonly string CertificatePath =
            Path.Combine(
                Environment.GetFolderPath(
                    Environment.SpecialFolder.LocalApplicationData),
                "LANShare",
                "device.pfx");

        public static X509Certificate2 GetOrCreateCertificate()
        {
            string? folder =
                Path.GetDirectoryName(CertificatePath);

            if (!string.IsNullOrEmpty(folder))
            {
                Directory.CreateDirectory(folder);
            }

            // Load existing certificate
            if (File.Exists(CertificatePath))
            {
                return X509CertificateLoader.LoadPkcs12FromFile(
                    CertificatePath,
                    password: null,
                    keyStorageFlags:
                        X509KeyStorageFlags.Exportable |
                        X509KeyStorageFlags.PersistKeySet);
            }

            // Create a new device key
            using RSA rsa = RSA.Create(2048);

            CertificateRequest request =
                new CertificateRequest(
                    "CN=LANShare Device",
                    rsa,
                    HashAlgorithmName.SHA256,
                    RSASignaturePadding.Pkcs1);

            request.CertificateExtensions.Add(
                new X509BasicConstraintsExtension(
                    certificateAuthority: false,
                    hasPathLengthConstraint: false,
                    pathLengthConstraint: 0,
                    critical: false));

            request.CertificateExtensions.Add(
                new X509KeyUsageExtension(
                    X509KeyUsageFlags.DigitalSignature,
                    critical: false));

            using X509Certificate2 certificate =
                request.CreateSelfSigned(
                    DateTimeOffset.UtcNow.AddMinutes(-5),
                    DateTimeOffset.UtcNow.AddYears(5));

            // Export certificate + private key
            byte[] pfx =
                certificate.Export(
                    X509ContentType.Pfx);

            File.WriteAllBytes(
                CertificatePath,
                pfx);

            // Load the saved certificate
            return X509CertificateLoader.LoadPkcs12(
                pfx,
                password: null,
                keyStorageFlags:
                    X509KeyStorageFlags.Exportable |
                    X509KeyStorageFlags.PersistKeySet);
        }

        public static string GetFingerprint(
            X509Certificate2 certificate)
        {
            return certificate.GetCertHashString(
                HashAlgorithmName.SHA256);
        }
    }
}