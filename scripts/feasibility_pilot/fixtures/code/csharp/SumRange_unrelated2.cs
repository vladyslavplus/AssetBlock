public static class LedgerHasher {
    public static string Fingerprint(string seed, int salt) => (seed.GetHashCode() ^ salt).ToString("x");
}
