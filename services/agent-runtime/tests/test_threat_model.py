"""ADR-0017 step 1: every test the threat model names still exists, so the document cannot silently go stale."""

import re
import unittest
from pathlib import Path

ROOT = Path(__file__).resolve().parents[3]
DOCUMENT = ROOT / "docs" / "security" / "threat-model.md"
REFERENCE = re.compile(r"`([A-Za-z0-9_./-]+\.(?:cs|py|js))::([^`]+)`")


def references(text):
    return REFERENCE.findall(text)


def missing(text, root=ROOT):
    problems = []
    for path, name in references(text):
        file = root / path
        if not file.is_file():
            problems.append(f"{path}: file not found")
        elif name not in file.read_text(encoding="utf-8"):
            problems.append(f"{path}::{name}: test not found")
    return problems


class ThreatModelTests(unittest.TestCase):
    def test_every_named_test_exists(self):
        text = DOCUMENT.read_text(encoding="utf-8")
        self.assertGreater(len(references(text)), 40)
        self.assertEqual(missing(text), [])

    def test_every_threat_in_the_specification_has_a_section_and_evidence(self):
        text = DOCUMENT.read_text(encoding="utf-8")
        sections = re.split(r"\n### ", text)[1:]
        names = [s.split("\n", 1)[0] for s in sections]
        self.assertEqual([n.split(".")[0] for n in names if n.startswith("T")], [f"T{i}" for i in range(1, 11)])
        for section in sections:
            heading = section.split("\n", 1)[0]
            self.assertRegex(section, r"\nRemaining[ :(]", heading)
            self.assertTrue(references(section), heading)

    def test_the_check_notices_a_renamed_or_removed_test(self):
        absent = "test_" + "absent_" * 2 + "name"  # built here, so the name never appears in this file
        text = f"`services/agent-runtime/tests/test_threat_model.py::{absent}`\n`apps/api/NoSuchFile.cs::Anything`"
        self.assertEqual(len(missing(text)), 2)


if __name__ == "__main__":
    unittest.main()
