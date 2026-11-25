"""Kept so earlier instructions still work: the same as smoke_provider.py --provider anthropic [--model MODEL]."""

import sys
from pathlib import Path

sys.path.insert(0, str(Path(__file__).resolve().parent))
import smoke_provider  # noqa: E402

if __name__ == "__main__":
    sys.exit(smoke_provider.main(["--provider", "anthropic", *(["--model", sys.argv[1]] if len(sys.argv) > 1 else [])]))
