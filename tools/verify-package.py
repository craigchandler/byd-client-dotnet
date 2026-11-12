#!/usr/bin/env python3
"""Inspect a package and compile/run an isolated offline consumer. Never contacts BYD."""
import argparse
import json
from pathlib import Path
import re
import shutil
import subprocess
import tempfile
import xml.etree.ElementTree as ET
from xml.sax.saxutils import escape
import zipfile


def unexpected_package_entries(names, intended):
    # Newer SDKs use deterministic nuget.psmdcp; older NuGet uses a 32-digit GUID.
    metadata = r"package/services/metadata/core-properties/(?:[a-f0-9]{32}|nuget)\.psmdcp"
    return sorted(name for name in set(names) - set(intended) if not re.fullmatch(metadata, name))


def run(*args, cwd=None, env=None):
    subprocess.run(args, cwd=cwd, env=env, check=True)


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("package", type=Path)
    parser.add_argument("--require-source-link", action="store_true",
                        help="Release gate: require repository revision and Source Link mapping")
    args = parser.parse_args()
    package = args.package.resolve()
    repo = Path(__file__).resolve().parents[1]
    with zipfile.ZipFile(package) as archive:
        names = set(archive.namelist())
        intended = {"Byd.Client.nuspec", "_rels/.rels", "[Content_Types].xml",
                    "lib/net10.0/Byd.Client.dll", "lib/net10.0/Byd.Client.xml",
                    "README.md", "LICENSE", "CHANGELOG.md", "THIRD-PARTY-NOTICES.md",
                    "THIRD-PARTY-LICENSE.txt", "THIRD-PARTY-MARKETS-LICENSE.txt"}
        intended.update("docs/" + p.name for p in (repo / "docs").glob("*.md"))
        unexpected = unexpected_package_entries(names, intended)
        assert not unexpected, f"Unexpected package files: {unexpected}"
        assert intended <= names, "Missing package files"
        readme = archive.read("README.md").decode("utf-8-sig").replace("\r\n", "\n")
        quickstart = (repo / "README.md").read_text().split("```csharp\n", 1)[1].split("\n```", 1)[0]
        assert readme.split("```csharp\n", 1)[1].split("\n```", 1)[0] == quickstart, "Packaged quick start differs from validated source"
        ns = {"n": "http://schemas.microsoft.com/packaging/2013/05/nuspec.xsd"}
        manifest = ET.fromstring(archive.read("Byd.Client.nuspec"))
        metadata = manifest.find("n:metadata", ns)
        # NuGet may choose another schema namespace as metadata changes.
        if metadata is None:
            metadata = next(x for x in manifest if x.tag.endswith("metadata"))
        values = {x.tag.split("}")[-1]: x for x in metadata}
        assert values["id"].text == "Byd.Client"
        version = values["version"].text
        assert "-" in version, "This first release must remain a prerelease"
        assert values["license"].text == "MIT"
        dependencies = [x.attrib for x in metadata.iter() if x.tag.endswith("dependency")]
        assert len(dependencies) == 1 and dependencies[0]["id"] == "MQTTnet", "Unexpected runtime dependency"
        repository = values.get("repository")
        if args.require_source_link:
            assert repository is not None and re.fullmatch(r"[a-f0-9]{40}", repository.get("commit", "")), "Missing repository commit"
            assert re.fullmatch(r"https://github\.com/[^/]+/[^/]+", repository.get("url", "")), "Missing public repository URL"
            assert not re.search(r'\]\((?!https?://|#)[^)]+\)', readme), "Repository-relative links will break on NuGet"
        assert b"VehicleNotSelected" in archive.read("lib/net10.0/Byd.Client.xml"), "XML API documentation missing"
    symbols = package.with_suffix(".snupkg")
    with zipfile.ZipFile(symbols) as archive:
        pdbs = [n for n in archive.namelist() if n.endswith(".pdb")]
        assert pdbs == ["lib/net10.0/Byd.Client.pdb"], "Unexpected symbol files"
        pdb = archive.read(pdbs[0])
        assert pdb[:4] == b"BSJB", "Expected portable PDB"
        assert not any(value in pdb for value in [b"/home/", b"/Users/", b"C:\\Users\\"]), "Private build path in symbols"
        if args.require_source_link:
            # Source Link is an uncompressed UTF-8 JSON custom-debug-information blob in portable PDBs.
            match = re.search(rb'\{"documents":\{[^\x00]+?\}\}', pdb)
            assert match, "No Source Link document mapping"
            documents = json.loads(match[0])["documents"]
            assert documents, "Empty Source Link mapping"
            owner_repo = repository.get("url").removeprefix("https://github.com/")
            expected = "https://raw.githubusercontent.com/" + owner_repo + "/" + repository.get("commit") + "/"
            assert all(url.startswith(expected) for url in documents.values()), "Source Link must match package repository and commit"

    with tempfile.TemporaryDirectory(prefix="byd-package-check-") as directory:
        temp = Path(directory)
        feed = temp / "feed"; feed.mkdir()
        shutil.copyfile(package, feed / package.name)
        config = temp / "NuGet.Config"
        config.write_text(f'''<configuration>
  <packageSources><clear/><add key="local" value="{escape(str(feed))}"/><add key="nuget.org" value="https://api.nuget.org/v3/index.json"/></packageSources>
  <packageSourceMapping><packageSource key="local"><package pattern="Byd.Client"/></packageSource><packageSource key="nuget.org"><package pattern="*"/></packageSource></packageSourceMapping>
</configuration>''')
        project = temp / "Consumer.csproj"
        project.write_text(f'''<Project Sdk="Microsoft.NET.Sdk"><PropertyGroup><OutputType>Exe</OutputType><TargetFramework>net10.0</TargetFramework><Nullable>enable</Nullable><ImplicitUsings>enable</ImplicitUsings><TreatWarningsAsErrors>true</TreatWarningsAsErrors></PropertyGroup><ItemGroup><PackageReference Include="Byd.Client" Version="{version}"/></ItemGroup></Project>''')
        import os
        env = os.environ.copy()
        # A new package cache proves installation from this feed, not a previous local build.
        env["NUGET_PACKAGES"] = str(temp / "packages")
        (temp / "Program.cs").write_text(quickstart)
        run("dotnet", "restore", str(project), "--configfile", str(config), cwd=temp, env=env)
        run("dotnet", "build", str(project), "-c", "Release", "--no-restore", cwd=temp, env=env)
        assets = json.loads((temp / "obj/project.assets.json").read_text())
        assert assets["libraries"][f"Byd.Client/{version}"]["type"] == "package", "Consumer used a project reference"
        (temp / "Program.cs").write_text((repo / "samples/ReadStatus/Program.cs").read_text())
        run("dotnet", "build", str(project), "-c", "Release", "--no-restore", cwd=temp, env=env)
        # Only bundled metadata is accessed. The interactive live branch is never executed here.
        run("dotnet", "run", "--project", str(project), "-c", "Release", "--no-build", "--", "--offline", cwd=temp, env=env)
        # Exercise only the public local policy/state API. No sign-in, vehicle read or command is invoked.
        (temp / "Program.cs").write_text('''using Byd.Client;
using var client = new BydClient();
if (client.RemoteControlsEnabled) throw new Exception("Controls must default off");
await client.SetRemoteControlsEnabledAsync(true);
if (!client.RemoteControlsEnabled) throw new Exception("Local opt-in failed");
await client.SetRemoteControlsEnabledAsync(false);
if (client.RemoteControlsEnabled) throw new Exception("Local opt-out failed");
var now = DateTimeOffset.UtcNow;
var status = new VehicleStatus(100, 325, "Not charging", "Locked", now, now, true);
if (status.ChargingState != VehicleChargingState.NotCharging || status.LockState != VehicleLockState.Locked)
    throw new Exception("Typed status mismatch");
Console.WriteLine("Public local-permission and typed-state consumer passed; no BYD requests");
''')
        run("dotnet", "build", str(project), "-c", "Release", "--no-restore", cwd=temp, env=env)
        run("dotnet", "run", "--project", str(project), "-c", "Release", "--no-build", cwd=temp, env=env)
    print(f"Package, portable symbols, README quick start and fresh local-feed consumer verified: Byd.Client {version}")


if __name__ == "__main__":
    main()
