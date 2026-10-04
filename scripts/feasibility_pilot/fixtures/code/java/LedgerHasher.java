package fixtures;

public final class LedgerHasher {
    public String fingerprint(String seed, int salt) {
        return Integer.toHexString(seed.hashCode() ^ salt);
    }
}
