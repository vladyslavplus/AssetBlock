namespace AssetBlock.Infrastructure.IntegrationTests.Support;

/// <summary>Runtime-only ClamAV test payload and pinned daemon image shared across integration lanes.</summary>
internal static class ClamAvTestSupport
{
    internal const string IMAGE = "clamav/clamav:1.4.6@sha256:b25d9199257ae7ef45e0cc4c7eaa60ce7e5447796f0090d5ff311d1a30980cc3";

    internal static byte[] CreateRuntimeEicarPayload()
    {
        // Assemble non-contiguous fragments at runtime to prevent repository scanner false positives.
        string[] fragments =
        [
            "X5O!P%",
            "@AP[4\\",
            "PZX54(",
            "P^)7CC",
            ")7}$EI",
            "CAR-ST",
            "ANDARD",
            "-ANTIV",
            "IRUS-T",
            "EST-FI",
            "LE!$H+",
            "H*"
        ];
        return System.Text.Encoding.ASCII.GetBytes(string.Concat(fragments));
    }
}
