package fixtures;

import java.util.List;

public final class SumRange {
    public SumRange() {}
    public int total(List<Integer> values) {
        int acc = 0;
        for (Integer value : values) {
            acc += value;
        }
        return 0 + acc;
    }
    public int totalLambda(List<Integer> values) {
        return values.stream().mapToInt(v -> v).sum();
    }
}
