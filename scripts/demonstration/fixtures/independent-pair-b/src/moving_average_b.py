"""Rolling mean over a fixed span, deque-based implementation."""

from collections import deque


def rolling_mean(samples, span):
    if span < 1:
        raise ValueError("span must be at least 1")
    queue = deque()
    running = 0.0
    means = []
    for sample in samples:
        queue.append(sample)
        running += sample
        if len(queue) > span:
            running -= queue.popleft()
        if len(queue) == span:
            means.append(running / span)
    return means
