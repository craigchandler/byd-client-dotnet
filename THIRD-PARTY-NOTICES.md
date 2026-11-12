# Protocol reference and attribution

The international country/language/server catalog in `markets.json` is derived from [hass-byd-vehicle const.py](https://github.com/jkaberg/hass-byd-vehicle/blob/0dd8cbefa485e819e27c805129b4287928b7b18a/custom_components/byd_vehicle/const.py), MIT, copyright (c) 2026 jkaberg. The exact licence is included in `THIRD-PARTY-MARKETS-LICENSE.txt`. `tools/import-markets.py` imports literal metadata without executing upstream code. The catalog contains 101 account countries across 16 international server regions; mainland China is not part of this catalog.

The Bangcle cipher port, protocol field layout, hashing/signing rules, API paths, MQTT authentication/event layout, GPS response layout and climate mappings derive from [jkaberg/pyBYD](https://github.com/jkaberg/pyBYD), **0.0.79**, MIT, copyright 2026 Joel Kåberg. The full upstream licence is included in `THIRD-PARTY-LICENSE.txt` and in the generated NuGet package.

Reference wheel: `pybyd-0.0.79-py3-none-any.whl`, SHA-256 `80ab9ab3c3d0bd9d05bdb71f41018a308271725e4ab771e7592918a9b9465cbd`. PyPI identifies publishing commit `995ef70a2b6bd287d58c1f8564e6b50eabc7f868`.

`src/Byd.Client/Crypto/bangcle_tables.bin` is copied unchanged from that public wheel. SHA-256: `5d7577f6298441a393898a6283b043495f3918f80d86c41191d9cf48a1c99a1f`. It is a protocol lookup table, not a user account key. The cipher source in the reference describes table extraction from `libencrypt.so`.

`tools/generate-fixtures.py` uses the reference library offline to generate synthetic vectors. The committed vectors contain no real account, token, VIN or vehicle response. There is no code copied from Moreno-Borsalino/BydClient.

The retained `pyBYD+0.0.79` wire identifier is a compatibility field, not project branding or an affiliation claim. Future protocol upgrades must preserve attribution and regenerate/review the synthetic fixtures.

The native receiver depends on [MQTTnet 5.2.0.1603](https://www.nuget.org/packages/MQTTnet/5.2.0.1603), MIT, maintained by the .NET Foundation and contributors. Its own licence is distributed with that NuGet package. No Python is needed at runtime.
