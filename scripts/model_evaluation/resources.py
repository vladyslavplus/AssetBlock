"""Resource measurement helpers: per-stage timing statistics and peak memory.

Stages are kept separate (preprocessing, window alignment, encoding, inference, ranking,
token scoring); transfer and queue time are not observable inside the harness and are
reported as unmeasured rather than as zero. Warmup samples are excluded per stage.
"""

import time
from contextlib import contextmanager

from . import metrics

UNMEASURED = {"transfer": "host-to-device copy time is not separable from encoding in the harness",
              "queue": "the harness has no request queue; queue time belongs to the service measurement"}


class StageTimer:
    def __init__(self, clock=time.perf_counter, warmup=0):
        if type(warmup) is not int or warmup < 0:
            raise ValueError("warmup must be a nonnegative integer")
        self.clock, self.warmup, self.samples = clock, warmup, {}

    def add(self, stage, seconds):
        if seconds < 0:
            raise ValueError("elapsed time cannot be negative")
        self.samples.setdefault(stage, []).append(seconds)

    @contextmanager
    def measure(self, stage):
        started = self.clock()
        try:
            yield
        finally:
            self.add(stage, self.clock() - started)

    def run(self, stage, call):
        started = self.clock()
        try:
            return call()
        finally:
            self.add(stage, self.clock() - started)

    def summary(self):
        result = {}
        for stage, values in sorted(self.samples.items()):
            kept = sorted(values[self.warmup:])
            entry = {"samples": len(values), "excludedWarmup": min(self.warmup, len(values)),
                     "belowWarmup": not kept}
            if kept:
                entry.update({"p50": metrics.percentile(kept, 0.5), "p95": metrics.percentile(kept, 0.95),
                              "max": kept[-1], "total": sum(kept)})
            else:
                entry.update({"p50": None, "p95": None, "max": None, "total": 0.0})
            result[stage] = entry
        return result


def peak_memory():
    """Peak resident memory of this process and peak CUDA allocation; ``None`` when unavailable."""
    ram = None
    try:
        from scripts.code_retrieval.training import process_working_set
        counters = process_working_set()
        ram = None if counters is None else counters["peakBytes"]
    except ImportError:
        ram = None
    vram = None
    try:
        import torch
        if torch.cuda.is_available():
            vram = int(torch.cuda.max_memory_allocated())
    except ImportError:
        vram = None
    return {"ramPeakBytes": ram, "vramPeakBytes": vram}


def resource_record(timer, protocol, memory=None):
    """The frozen protocol is enforced: too few timed queries after warmup refuses the measurement."""
    summary = timer.summary()
    end_to_end = summary.get("endToEndQuery")
    timed = 0 if end_to_end is None else end_to_end["samples"] - end_to_end["excludedWarmup"]
    if timed < protocol["timedQueries"]:
        raise ValueError("fewer timed queries (%d) than the frozen protocol requires (%d)"
                         % (timed, protocol["timedQueries"]))
    return {"warmup": protocol["warmupQueries"], "requiredTimedQueries": protocol["timedQueries"],
            "percentiles": list(protocol["percentiles"]), "stages": summary,
            "memory": memory if memory is not None else peak_memory(), "unmeasured": dict(UNMEASURED)}
