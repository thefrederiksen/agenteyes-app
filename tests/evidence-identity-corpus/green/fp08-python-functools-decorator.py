import functools
@functools.lru_cache(maxsize=None)
def f():
    return 1
