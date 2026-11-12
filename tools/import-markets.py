"""Import public country/server metadata without executing upstream Python. No network access.

Source: jkaberg/hass-byd-vehicle const.py @ 0dd8cbefa485e819e27c805129b4287928b7b18a (MIT).
Usage: python tools/import-markets.py /path/to/pinned/const.py
"""
import ast
import json
import sys
from pathlib import Path

tree = ast.parse(Path(sys.argv[1]).read_text())
wanted = {"NODE_METADATA", "COUNTRY_OPTIONS", "COUNTRY_TO_NODE"}
values = {node.target.id: ast.literal_eval(node.value) for node in tree.body
          if isinstance(node, ast.AnnAssign) and isinstance(node.target, ast.Name) and node.target.id in wanted}
nodes, countries, mapping = (values[name] for name in ("NODE_METADATA", "COUNTRY_OPTIONS", "COUNTRY_TO_NODE"))
rows = []
for name, (code, language) in countries.items():
    node = nodes[mapping[code]]
    assert node["api_base_url"].startswith("https://dilinkappoversea-") and node["api_base_url"].endswith(".byd.auto")
    rows.append({"name": name, "countryCode": code, "language": language, "serverRegion": node["region"], "apiBaseUrl": node["api_base_url"]})
assert len(rows) == len(mapping) and len({r["countryCode"] for r in rows}) == len(rows)
destination = Path(__file__).resolve().parents[1] / "src/Byd.Client/markets.json"
destination.write_text(json.dumps(rows, ensure_ascii=False, indent=2) + "\n")
print(f"Imported {len(rows)} countries across {len(nodes)} server regions; no network requests made.")
