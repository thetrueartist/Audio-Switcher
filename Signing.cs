using System;
using System.Security.Cryptography;

namespace AudioSwitcher
{
    // ====================================================================
    // RELEASE SIGNING - ECDSA P-256, pure BCL. Protects auto-update against a
    // compromised repo: only releases signed with the author's OFFLINE private key are
    // trusted. Keep the private key off CI/GitHub - anything a compromised repo can reach.
    //
    // In its own file (compiled into both the exe and the test project) so the crypto can be
    // unit-tested without pulling in the Windows/COM/WinForms code the rest of the app needs.
    // ====================================================================
    public static class Signing
    {
        // Empty until the author embeds their public key (base64 SubjectPublicKeyInfo).
        // Generate with --gen-signing-key. When empty, signature checks are skipped (the
        // update check falls back to the plain release tag).
        public const string PublicKeyB64 = "";

        public static bool Enabled => PublicKeyB64.Length > 0;

        public static (string priv, string pub) GenerateKeyPair()
        {
            using var ec = ECDsa.Create(ECCurve.NamedCurves.nistP256);
            return (Convert.ToBase64String(ec.ExportPkcs8PrivateKey()),
                    Convert.ToBase64String(ec.ExportSubjectPublicKeyInfo()));
        }

        public static string Sign(byte[] data, string privB64)
        {
            using var ec = ECDsa.Create();
            ec.ImportPkcs8PrivateKey(Convert.FromBase64String(privB64), out _);
            return Convert.ToBase64String(ec.SignData(data, HashAlgorithmName.SHA256));
        }

        // Verify against the embedded public key (the production update path).
        public static bool Verify(byte[] data, string sigB64)
        {
            if (!Enabled) return false;
            return Verify(data, sigB64, PublicKeyB64);
        }

        // Verify against an explicit public key. Separated out so tests can round-trip a freshly
        // generated keypair without needing one embedded in the build.
        public static bool Verify(byte[] data, string sigB64, string pubB64)
        {
            try
            {
                using var ec = ECDsa.Create();
                ec.ImportSubjectPublicKeyInfo(Convert.FromBase64String(pubB64), out _);
                return ec.VerifyData(data, Convert.FromBase64String(sigB64), HashAlgorithmName.SHA256);
            }
            catch { return false; }
        }

        public static string Sha256Hex(byte[] data)
        {
            using var sha = SHA256.Create();
            return Convert.ToHexString(sha.ComputeHash(data)).ToLowerInvariant();
        }
    }
}
