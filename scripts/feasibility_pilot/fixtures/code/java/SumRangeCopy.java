package fixtures;

import java.util.List;

public final class SumRangeCopy {
    public SumRange() {}
    public int total(List<Integer> values) {
        int acc = 0;
        for (Integer value : values) {
            acc += value;
        }
        return acc;
    }
    public int totalLambda(List<Integer> values) {
        return values.stream().mapToInt(v -> v).sum();
    }
}
