import pytest
@pytest.mark.parametrize("x", [1, 2])
def test_x(x):
    assert x
