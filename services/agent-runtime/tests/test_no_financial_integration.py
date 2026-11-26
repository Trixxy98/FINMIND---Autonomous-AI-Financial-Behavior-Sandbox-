"""ADR-0017 step 4: no real financial integration. Every dependency is on a reviewed list, the only network hosts in
source are local ones and the model provider, and no payment, banking, brokerage, or crypto SDK or host appears.

A change here is a review decision: add a dependency or host to the lists below only after checking what it does.
"""

import json
import re
import subprocess
import unittest
from pathlib import Path

ROOT = Path(__file__).resolve().parents[3]

NUGET = {("Npgsql", "10.0.3"), ("Microsoft.NET.Test.Sdk", "17.14.1"), ("xunit", "2.9.3"),
         ("xunit.runner.visualstudio", "3.1.5")}
PIP = {"anthropic==2.12.0"}
NPM_DIRECT = {"@vitejs/plugin-react": "6.1.2", "vite": "8.3.5", "react": "19.3.1", "react-dom": "19.3.1",
              "@types/react": "19.3.0", "@types/react-dom": "19.3.0", "typescript": "7.0.2"}
NPM_LOCKED = {"@oxc-project/types", "@rolldown/pluginutils", "@types/react", "@types/react-dom", "@vitejs/plugin-react",
              "csstype", "detect-libc", "fdir", "fsevents", "lightningcss", "nanoid", "picocolors", "picomatch", "postcss",
              "react", "react-dom", "rolldown", "scheduler", "source-map-js", "tinyglobby", "typescript", "vite"}
# Platform builds of the same tools: one package per operating system and processor.
NPM_PLATFORM = re.compile(r"^(@rolldown/binding-|@typescript/typescript-|lightningcss-)[a-z0-9-]+$")
IMAGES = {"postgres:18"}
# Model APIs the worker calls, and the documentation pages the price tables cite in comments (never requested).
HOSTS = {"127.0.0.1", "localhost", "api.anthropic.com", "platform.claude.com", "api.openai.com", "developers.openai.com",
         "api.groq.com", "console.groq.com"}

# Payment, banking, brokerage, wallet, and crypto SDKs and hosts. Matched as whole words, case-insensitively.
FORBIDDEN = ["stripe", "paypal", "braintree", "adyen", "plaid", "razorpay", "billplz", "toyyibpay", "ipay88", "senangpay",
             "molpay", "fiuu", "xendit", "midtrans", "squareup", "checkout\\.com", "wise\\.com", "transferwise", "revolut",
             "coinbase", "binance", "kraken", "ccxt", "web3", "ethers", "alpaca", "ib_insync", "ibapi", "dwolla", "moov",
             "open-banking", "openbanking", "duitnow", "paynet", "fpx", "swift\\.com", "visa\\.com", "mastercard\\.com"]
FORBIDDEN_PATTERN = re.compile(r"(?<![A-Za-z0-9_])(" + "|".join(FORBIDDEN) + r")(?![A-Za-z0-9_])", re.IGNORECASE)
HOST_PATTERN = re.compile(r"https?://([A-Za-z0-9.-]+)")


def tracked(*patterns):
    out = subprocess.run(["git", "ls-files", *patterns], cwd=ROOT, capture_output=True, text=True, check=True).stdout
    return [ROOT / line for line in out.splitlines() if line]


def source_files():
    """Code and configuration: everything tracked except documentation, tests, and the npm lockfile."""
    skip = re.compile(r"(^docs/|\.md$|(^|/)tests?/|Finance\.Tests/|package-lock\.json$|\.png$|\.ico$)")
    return [p for p in tracked() if not skip.search(str(p.relative_to(ROOT)))]


def scanned_files():
    """Source plus tests, minus this file (it holds the list of names it looks for)."""
    tests = re.compile(r"((^|/)tests?/|Finance\.Tests/).*\.(py|cs|js)$")
    own = Path(__file__).resolve()
    return source_files() + [p for p in tracked() if tests.search(str(p.relative_to(ROOT))) and p.resolve() != own] \
        + tracked("apps/web/package-lock.json")


def forbidden_hits(text):
    return sorted({m.group(1).lower() for m in FORBIDDEN_PATTERN.finditer(text)})


class DependencyAllowlistTests(unittest.TestCase):
    def test_nuget_references_are_the_reviewed_set(self):
        found = set()
        for project in tracked("*.csproj"):
            found |= set(re.findall(r'<PackageReference Include="([^"]+)" Version="([^"]+)"', project.read_text()))
        self.assertEqual(found, NUGET)

    def test_python_requirements_are_the_reviewed_set(self):
        found = set()
        for requirements in tracked("*requirements*.txt"):
            found |= {line.strip() for line in requirements.read_text().splitlines() if line.strip() and not line.startswith("#")}
        self.assertEqual(found, PIP)

    def test_npm_direct_and_locked_packages_are_the_reviewed_set(self):
        package = json.loads((ROOT / "apps/web/package.json").read_text())
        self.assertEqual({**package.get("dependencies", {}), **package.get("devDependencies", {})}, NPM_DIRECT)
        lock = json.loads((ROOT / "apps/web/package-lock.json").read_text())
        names = {key.split("node_modules/")[-1] for key in lock["packages"] if key}
        unknown = sorted(n for n in names if n not in NPM_LOCKED and not NPM_PLATFORM.match(n))
        self.assertEqual(unknown, [])
        for name, entry in lock["packages"].items():
            if name:
                self.assertTrue(entry.get("resolved", "https://registry.npmjs.org/").startswith("https://registry.npmjs.org/"), name)

    def test_container_images_are_the_reviewed_set(self):
        images = set()
        for compose in tracked("*compose*.yaml", "*compose*.yml", "*Dockerfile*"):
            images |= set(re.findall(r"^\s*(?:image:|FROM)\s*(\S+)", compose.read_text(), re.MULTILINE))
        self.assertEqual(images, IMAGES)


class IntegrationScanTests(unittest.TestCase):
    def test_source_names_only_local_hosts_and_the_model_provider(self):
        hosts = {}
        for path in source_files():
            for host in HOST_PATTERN.findall(path.read_text(errors="replace")):
                hosts.setdefault(host.lower(), str(path.relative_to(ROOT)))
        self.assertEqual({h: p for h, p in hosts.items() if h not in HOSTS}, {})

    def test_no_payment_banking_brokerage_or_crypto_sdk_or_host(self):
        hits = {}
        for path in scanned_files():
            found = forbidden_hits(path.read_text(errors="replace"))
            if found:
                hits[str(path.relative_to(ROOT))] = found
        self.assertEqual(hits, {})

    def test_the_scan_notices_an_integration(self):
        self.assertEqual(forbidden_hits('import stripe\nfetch("https://api.paypal.com/v1")\npip install ccxt'),
                         ["ccxt", "paypal", "stripe"])
        self.assertEqual(forbidden_hits("A stripey cat; paypalish; sweb3x"), [])
        self.assertEqual(HOST_PATTERN.findall("see https://api.stripe.com/v1/charges"), ["api.stripe.com"])


if __name__ == "__main__":
    unittest.main()
