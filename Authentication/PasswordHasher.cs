using System;
using System.Security.Cryptography;
using System.Text;

namespace WpfApp3.Authentication
{
    /// <summary>
    /// PBKDF2-HMAC-SHA256 password hashing.
    /// Passwords are NEVER stored in plaintext.
    /// </summary>
    public static class PasswordHasher
    {
        private const int SaltBytes       = 32;
        private const int HashBytes        = 32;
        private const int Iterations       = 600_000;
        private static readonly HashAlgorithmName Algorithm = HashAlgorithmName.SHA256;

        /// <summary>Returns (hash, salt) both as Base64 strings.</summary>
        public static (string Hash, string Salt) HashPassword(string password)
        {
            if (string.IsNullOrEmpty(password))
                throw new ArgumentException("Password must not be empty.", nameof(password));

            byte[] saltBytes = RandomNumberGenerator.GetBytes(SaltBytes);

            byte[] hashBytes = Rfc2898DeriveBytes.Pbkdf2(
                Encoding.UTF8.GetBytes(password),
                saltBytes,
                Iterations,
                Algorithm,
                HashBytes);

            return (
                Convert.ToBase64String(hashBytes),
                Convert.ToBase64String(saltBytes)
            );
        }

        /// <summary>Returns true if the password matches the stored hash+salt.</summary>
        public static bool VerifyPassword(
            string password,
            string storedHashBase64,
            string storedSaltBase64)
        {
            if (string.IsNullOrEmpty(password))           return false;
            if (string.IsNullOrEmpty(storedHashBase64))   return false;
            if (string.IsNullOrEmpty(storedSaltBase64))   return false;

            try
            {
                byte[] saltBytes   = Convert.FromBase64String(storedSaltBase64);
                byte[] storedHash  = Convert.FromBase64String(storedHashBase64);

                byte[] computedHash = Rfc2898DeriveBytes.Pbkdf2(
                    Encoding.UTF8.GetBytes(password),
                    saltBytes,
                    Iterations,
                    Algorithm,
                    HashBytes);

                // Constant-time comparison to prevent timing attacks
                return CryptographicOperations.FixedTimeEquals(computedHash, storedHash);
            }
            catch
            {
                return false;
            }
        }
    }
}
