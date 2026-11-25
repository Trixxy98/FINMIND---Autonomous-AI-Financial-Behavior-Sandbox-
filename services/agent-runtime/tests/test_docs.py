"""ADR-0017 step 5: every repository path the current documentation names exists, so it cannot silently go stale.

Decision records (docs/decisions) are history: they keep the paths they were written with, and later changes are
recorded in their implementation notes. Tests named as `file::test` are checked by test_threat_model.py.
"""

import re
import unittest
from pathlib import Path

ROOT = Path(__file__).resolve().parents[3]
PATH = re.compile(r"`((?:apps|services|docs|infra|scenarios|prompts|\.github)/[^`\s*{}<>:]+)(?:::[^`]*)?`")
REQUIRED = ["README.md", "AGENTS.md", "docs/ARCHITECTURE.md", "docs/RESEARCHER_GUIDE.md", "docs/reports/TEMPLATE.md",
            "docs/security/threat-model.md"]


def documents():
    return [ROOT / "README.md", ROOT / "AGENTS.md"] + sorted(
        p for p in (ROOT / "docs").rglob("*.md") if "decisions" not in p.relative_to(ROOT).parts)


def missing_paths(text, root=ROOT):
    return sorted({p.rstrip("/.,") for p in PATH.findall(text) if not (root / p.rstrip("/.,")).exists()})


class DocumentationTests(unittest.TestCase):
    def test_the_documentation_set_exists(self):
        self.assertEqual([p for p in REQUIRED if not (ROOT / p).is_file()], [])

    def test_every_named_path_exists(self):
        problems = {str(d.relative_to(ROOT)): missing_paths(d.read_text(encoding="utf-8")) for d in documents()}
        self.assertEqual({d: p for d, p in problems.items() if p}, {})

    def test_the_check_notices_a_missing_path(self):
        self.assertEqual(missing_paths("see `docs/NO_SUCH_FILE.md` and `apps/web/src/lib/cues.ts`"), ["docs/NO_SUCH_FILE.md"])
        self.assertEqual(missing_paths("`apps/api/Finance.Tests/LedgerTests.cs::Anything`"), [])

    def test_the_readme_links_every_guide(self):
        readme = (ROOT / "README.md").read_text(encoding="utf-8")
        for guide in REQUIRED[2:]:
            self.assertIn(f"`{guide}`", readme)


if __name__ == "__main__":
    unittest.main()
