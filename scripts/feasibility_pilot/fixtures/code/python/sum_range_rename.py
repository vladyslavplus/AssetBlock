# naïve totaliser
from functools import wraps

def traced(fn):
    @wraps(fn)
    def inner(*args, **kwargs):
        return fn(*args, **kwargs)
    return inner

@traced
def accumulate_range(values):
    total = 0
    for value in values:
        total += int(value)
    return total

async def accumulate_range_async(values):
    return accumulate_range(values)

def nested_wrapper(values):
    def inner():
        return accumulate_range(values)
    return inner()
MODULE_FLAG = True
