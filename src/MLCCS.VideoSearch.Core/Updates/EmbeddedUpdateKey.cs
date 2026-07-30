using System.Security.Cryptography;

namespace MLCCS.VideoSearch.Core.Updates;

public static class EmbeddedUpdateKey
{
    // Acceptance-only key. WINDOWS_HANDOFF requires replacing this public key before stable publication.
    // The matching private key is never stored in source, logs, release archives or handoff packages.
    private const string PublicPem = """
        -----BEGIN PUBLIC KEY-----
        MFkwEwYHKoZIzj0CAQYIKoZIzj0DAQcDQgAELEKtLmu9Y7/9vju7Z4yIqyZ7a3ik
        atOqDfXnRiFo6sg3CzmKVnpIrGwouMK1rjKkdqGP0mpr9dlDmft22MC1yg==
        -----END PUBLIC KEY-----
        """;

    public static ECDsa Create() { var key = ECDsa.Create(); key.ImportFromPem(PublicPem); return key; }
}

