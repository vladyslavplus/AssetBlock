"""Exponential smoothing helper. Authored stand-in for a third-party component."""


def exponential_smoothing(values, alpha):
    if not 0 < alpha <= 1:
        raise ValueError("alpha must be in the interval (0, 1]")
    smoothed = []
    current = None
    for value in values:
        current = value if current is None else alpha * value + (1 - alpha) * current
        smoothed.append(current)
    return smoothed
