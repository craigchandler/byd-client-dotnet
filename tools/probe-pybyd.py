"""Manual read-only comparison probe. Never pass credentials as command arguments."""
import asyncio
import getpass
import json
import logging
import sys
import warnings

import aiohttp
from pybyd import BydClient, BydConfig

ALLOWED = frozenset({"/app/account/login", "/app/account/getAllListByUserId", "/vehicleInfo/vehicle/vehicleRealTimeRequest", "/vehicleInfo/vehicle/vehicleRealTimeResult"})

async def guard(session, context, params):
    if params.method != "POST" or str(params.url.origin()) != "https://dilinkappoversea-au.byd.auto" or params.url.path not in ALLOWED:
        raise RuntimeError("Read-only endpoint boundary rejected a request")

async def no_redirect(session, context, params):
    raise RuntimeError("Redirect blocked")

async def probe(username, password):
    trace = aiohttp.TraceConfig()
    trace.on_request_start.append(guard)
    trace.on_request_redirect.append(no_redirect)
    config = BydConfig(username=username, password=password, base_url="https://dilinkappoversea-au.byd.auto", country_code="AU", time_zone="Australia/Adelaide", mqtt_enabled=False, control_pin=None)
    async with aiohttp.ClientSession(timeout=aiohttp.ClientTimeout(total=30), trace_configs=[trace]) as session:
        async with BydClient(config, session=session) as client:
            await client.login()
            vehicles = await client.get_vehicles()
            print(json.dumps({"authentication": "ok", "vehicle_count": len(vehicles)}))
            if not vehicles:
                return
            for index, vehicle in enumerate(vehicles, 1):
                print(f"{index}: vehicle VIN ending {vehicle.vin[-4:]}")
            index = int(input("Vehicle number (Enter for 1): ") or "1") - 1
            if not 0 <= index < len(vehicles):
                print("Invalid vehicle selection.")
                return
            data = await client.get_vehicle_realtime(vehicles[index].vin, poll_attempts=8, signal_retries=0)
            print(json.dumps({"battery_percent": data.elec_percent, "range_km": data.endurance_mileage, "charging_state": data.effective_charging_state.name}))

if __name__ == "__main__":
    logging.disable(logging.CRITICAL)
    if not sys.stdin.isatty():
        sys.exit("Run in your own terminal. Do not pipe credentials or enter them in chat.")
    warnings.simplefilter("error", getpass.GetPassWarning)
    print("One Australian BYD sign-in and read-only status check. May wake the car. No PIN or vehicle commands.")
    try:
        username = getpass.getpass("BYD account email (hidden): ")
        password = getpass.getpass("BYD password (hidden): ")
        asyncio.run(probe(username, password))
    except (KeyboardInterrupt, EOFError):
        print("Cancelled.")
    except Exception:
        print("Probe failed. Check the official app; raw responses and exception details were suppressed.")
        sys.exit(1)
