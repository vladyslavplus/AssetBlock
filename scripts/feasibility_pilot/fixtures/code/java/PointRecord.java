package fixtures;

public record PointRecord(int x, int y) {
    public int mag() { return Math.abs(x) + Math.abs(y); }
}
