"""Basic statistics over finite sequences of numbers."""


def mean(values):
    items = list(values)
    if not items:
        raise ValueError("mean of an empty sequence")
    return sum(items) / len(items)


def median(values):
    ordered = sorted(values)
    if not ordered:
        raise ValueError("median of an empty sequence")
    middle = len(ordered) // 2
    return ordered[middle]


def clamp(value, low, high):
    if low > high:
        raise ValueError("low must not be greater than high")
    return max(low, min(high, value))
