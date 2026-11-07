"""Bounded moving average, index-based implementation."""


def moving_average(values, window):
    if window < 1:
        raise ValueError("window must be at least 1")
    averages = []
    total = 0.0
    for index, value in enumerate(values):
        total += value
        if index >= window:
            total -= values[index - window]
        if index >= window - 1:
            averages.append(total / window)
    return averages
