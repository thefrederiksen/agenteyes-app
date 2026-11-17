class B:
    count = 1
    @functools.cached_property
    def c(self):
        return 2
