"""xoshiro256** with the same seeding scheme as the API (ADR-0010 prng/xoshiro256starstar-v1)."""

from __future__ import annotations

import hashlib
import struct

_MASK = (1 << 64) - 1
ALGORITHM = "prng/xoshiro256starstar-v1"


def _rotl(value: int, shift: int) -> int:
    return ((value << shift) | (value >> (64 - shift))) & _MASK


class Xoshiro256StarStar:
    def __init__(self, state32: bytes) -> None:
        if len(state32) != 32:
            raise ValueError("state must be 32 bytes")
        self._s = list(struct.unpack("<4Q", state32))

    @classmethod
    def from_label(cls, label: str) -> "Xoshiro256StarStar":
        return cls(hashlib.sha256(label.encode("utf-8")).digest())

    def next(self) -> int:
        s0, s1, s2, s3 = self._s
        result = (_rotl((s1 * 5) & _MASK, 7) * 9) & _MASK
        t = (s1 << 17) & _MASK
        s2 ^= s0
        s3 ^= s1
        s1 ^= s2
        s0 ^= s3
        s2 ^= t
        s3 = _rotl(s3, 45)
        self._s = [s0, s1, s2, s3]
        return result

    def below(self, count: int) -> int:
        """Value in [0, count). Modulo bias is accepted, as in the API (ADR-0010)."""
        return self.next() % count

    def choice(self, items):
        return items[self.below(len(items))]
