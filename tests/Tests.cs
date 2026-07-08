using System;
using System.Text;
using AudioSwitcher;

namespace AudioSwitcher.Tests
{
    // Dependency-free test runner (no xUnit/NUnit - keeps the project's zero-third-party-deps rule).
    // Each Check(...) asserts a condition; Main prints a summary and returns non-zero if anything
    // failed, so `dotnet run` / CI fails the build on a regression. Add more Suite() calls as more
    // pure logic is extracted (config validation, profile resolution, silence-decision, ...).
    public static class Program
    {
        static int _passed, _failed;

        static void Check(string name, bool ok)
        {
            if (ok) { _passed++; Console.WriteLine($"  PASS  {name}"); }
            else    { _failed++; Console.WriteLine($"  FAIL  {name}"); }
        }

        public static int Main()
        {
            Console.WriteLine("AudioSwitcher tests\n");
            SigningTests();
            SilencePolicyTests();
            Console.WriteLine($"\n{_passed} passed, {_failed} failed");
            return _failed == 0 ? 0 : 1;
        }

        static void SilencePolicyTests()
        {
            Console.WriteLine("\nSilencePolicy (learning: don't drag non-rate-silent games to the floor):");
            const int lowest = 5;

            Check("mid-ladder + silent -> keep dropping",
                  SilencePolicy.Decide(1, lowest, true) == SilenceAction.Bump);
            Check("just above floor -> still drops",
                  SilencePolicy.Decide(4, lowest, false) == SilenceAction.Bump);
            Check("floor + clean pre-set + silent -> give up (not rate-related)",
                  SilencePolicy.Decide(5, lowest, true) == SilenceAction.GiveUpNotRateRelated);
            Check("floor + NO clean pre-set -> stay pinned (can't conclude)",
                  SilencePolicy.Decide(5, lowest, false) == SilenceAction.StayPinned);
            Check("top tier is never immediately given up on",
                  SilencePolicy.Decide(0, lowest, true) == SilenceAction.Bump);
        }

        static void SigningTests()
        {
            Console.WriteLine("Signing (ECDSA P-256, update integrity):");
            var (priv, pub) = Signing.GenerateKeyPair();
            Check("keypair is non-empty base64", priv.Length > 0 && pub.Length > 0);

            byte[] data = Encoding.UTF8.GetBytes("manifest v1.4.0 sha256=abc123");
            string sig = Signing.Sign(data, priv);

            Check("a valid signature verifies", Signing.Verify(data, sig, pub));

            byte[] tampered = Encoding.UTF8.GetBytes("manifest v1.4.0 sha256=DEADBEEF");
            Check("tampered data is rejected", !Signing.Verify(tampered, sig, pub));

            var (_, otherPub) = Signing.GenerateKeyPair();
            Check("a signature from a different key is rejected", !Signing.Verify(data, sig, otherPub));

            Check("a garbage signature is rejected without throwing", !Signing.Verify(data, "not-base64!!", pub));

            // Production path: a public key is embedded, so signing is ENABLED and the updater fails
            // closed. The 2-arg Verify (embedded key) must reject a signature made with any other key.
            Check("signing is enabled (public key embedded)", Signing.Enabled);
            Check("production verify rejects a signature not from the embedded key", !Signing.Verify(data, sig));

            // SHA-256 known-answer vector: sha256("") = e3b0c442...b855
            Check("sha256 matches the empty-string known vector",
                  Signing.Sha256Hex(Array.Empty<byte>())
                  == "e3b0c44298fc1c149afbf4c8996fb92427ae41e4649b934ca495991b7852b855");
        }
    }
}
