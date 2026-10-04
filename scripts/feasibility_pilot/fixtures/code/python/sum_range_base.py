# naïve totaliser
from functools import wraps

def traced(fn):
    @wraps(fn)
    def inner(*args, **kwargs):
        return fn(*args, **kwargs)
    return inner

@traced
def sum_range(values):
    total = 0
    for value in values:
        total += int(value)
    return total

async def sum_range_async(values):
    return sum_range(values)

def nested_wrapper(values):
    def inner():
        return sum_range(values)
    return inner()
MODULE_FLAG = True
