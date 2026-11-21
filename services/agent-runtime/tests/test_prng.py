import unittest

import _path  # noqa: F401
from finmind_runtime.prng import Xoshiro256StarStar


class PrngTests(unittest.TestCase):
    def test_matches_api_test_vector(self):
        # Same vector as ScenarioTests.Prng_matches_published_test_vector in the API (ADR-0010).
        random = Xoshiro256StarStar.from_label("finmind-scenario-v1|S02|1|42")
        self.assertEqual([random.next() for _ in range(3)],
                         [17067664432659434232, 18040955551344981867, 8957787423657356057])

    def test_same_label_same_sequence(self):
        a = Xoshiro256StarStar.from_label("finmind-mock-chaotic-v1|5")
        b = Xoshiro256StarStar.from_label("finmind-mock-chaotic-v1|5")
        self.assertEqual([a.next() for _ in range(5)], [b.next() for _ in range(5)])


if __name__ == "__main__":
    unittest.main()
