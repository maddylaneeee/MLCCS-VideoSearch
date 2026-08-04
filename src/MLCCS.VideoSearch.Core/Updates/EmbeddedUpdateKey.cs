using System.Security.Cryptography;

namespace MLCCS.VideoSearch.Core.Updates;

public static class EmbeddedUpdateKey
{
    // Production P-256 manifest key. The private key is kept only in encrypted PKCS#8 form on the release Mac.
    private const string PublicPem = """
        -----BEGIN PUBLIC KEY-----
        MFkwEwYHKoZIzj0CAQYIKoZIzj0DAQcDQgAEsj6Tq3Dvm0jOeAKWUihiRVeUOBjm
        vJAryVn3kWM0thmccVb78XIXMNaj63nxtUzmq+lYyQUu1EgbQVW475dRHw==
        -----END PUBLIC KEY-----
        """;

    public static ECDsa Create() { var key = ECDsa.Create(); key.ImportFromPem(PublicPem); return key; }
}
