"""Deterministic pair order, step accounting and reload comparison helpers."""

import math
import random


def epoch_order(rows, *, seed, epoch):
    rng = random.Random(seed + epoch)
    indices = list(range(len(rows)))
    rng.shuffle(indices)
    return [{"index": index, "swapped": bool(rng.getrandbits(1))} for index in indices]


def require_step_ceiling(*, skipped_steps, attempted_steps, epoch_losses, maximum_fraction):
    if type(attempted_steps) is not int or attempted_steps <= 0:
        raise ValueError("optimizer step attempts are missing")
    if type(skipped_steps) is not int or not 0 <= skipped_steps <= attempted_steps:
        raise ValueError("skipped optimizer step count is invalid")
    if any(not isinstance(loss, (int, float)) or not math.isfinite(loss) for loss in epoch_losses):
        raise ValueError("non-finite epoch loss")
    if skipped_steps / attempted_steps > maximum_fraction:
        raise ValueError("skipped optimizer step fraction exceeds the frozen ceiling")


def require_reload_match(expected, actual, *, tolerance):
    if not isinstance(expected, list) or not expected or len(expected) != len(actual):
        raise ValueError("reload output shape differs")
    differences = []
    for expected_row, actual_row in zip(expected, actual):
        if not isinstance(expected_row, list) or len(expected_row) != len(actual_row):
            raise ValueError("reload output shape differs")
        for left, right in zip(expected_row, actual_row):
            if not math.isfinite(float(left)) or not math.isfinite(float(right)):
                raise ValueError("reload output is non-finite")
            differences.append(abs(float(left) - float(right)))
    maximum = max(differences)
    if maximum > tolerance:
        raise ValueError("reload output differs beyond the declared tolerance")
    return {"values": len(differences), "maximumAbsoluteDifference": maximum, "tolerance": tolerance}
