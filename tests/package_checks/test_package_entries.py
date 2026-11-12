"""Offline package-boundary regressions across NuGet SDK versions."""
import importlib.util
from pathlib import Path
import unittest

spec = importlib.util.spec_from_file_location("verify_package", Path(__file__).resolve().parents[2] / "tools/verify-package.py")
verify_package = importlib.util.module_from_spec(spec)
spec.loader.exec_module(verify_package)


class PackageEntryTests(unittest.TestCase):
    def test_known_old_and_deterministic_sdk_metadata_names_are_accepted(self):
        intended = {"lib/net10.0/Byd.Client.dll", "README.md"}
        for basename in ["300b4e1be35646d8a1f0da1a3837653f.psmdcp", "nuget.psmdcp"]:
            with self.subTest(metadata=basename):
                names = intended | {"package/services/metadata/core-properties/" + basename}
                self.assertEqual([], verify_package.unexpected_package_entries(names, intended))

    def test_metadata_compatibility_does_not_allow_unintended_content(self):
        intended = {"lib/net10.0/Byd.Client.dll", "README.md"}
        for extra in ["credentials.json", "src/BydClient.cs", "lib/net10.0/Unexpected.dll",
                      "nuget.psmdcp", "package/services/metadata/core-properties/capture.json",
                      "package/services/metadata/core-properties/other.psmdcp"]:
            with self.subTest(extra=extra):
                names = intended | {"package/services/metadata/core-properties/nuget.psmdcp", extra}
                self.assertEqual([extra], verify_package.unexpected_package_entries(names, intended))


if __name__ == "__main__":
    unittest.main()
