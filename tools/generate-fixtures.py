"""Offline oracle. Requires pybyd==0.0.79. Uses only synthetic test identities."""
import json
from pathlib import Path
from unittest.mock import patch
from pybyd import BydConfig
from pybyd._api.login import build_login_request
from pybyd._api._envelope import build_token_outer_envelope
from pybyd._api._common import build_inner_base
from pybyd._crypto.bangcle import BangcleCodec
from pybyd._crypto.hashing import md5_hex, pwd_login_key, sha1_mixed
from pybyd._crypto.aes import aes_encrypt_hex
from pybyd.models import ClimateStartParams, minutes_to_time_span
from pybyd.models.control import ClimateScheduleParams, SeatClimateParams, BatteryHeatParams
from pybyd.models.hvac import HvacStatus
from pybyd.session import Session
from pybyd._mqtt import _build_mqtt_password

def generate():
    config = BydConfig(username="synthetic@example.invalid", password="Synthetic-Only-Password", country_code="AU", time_zone="Australia/Adelaide", mqtt_enabled=False)
    codec = BangcleCodec()
    texts = ["", "hello", "0123456789abcdef", '{"battery":0,"label":"Synthetic only"}', "Line one\nUnicode: é 🚗"]
    result = {"reference": "pybyd 0.0.79", "envelopes": [{"text": t, "encoded": codec.encode_envelope(t)} for t in texts]}
    result["hashes"] = [{"text": t, "md5": md5_hex(t), "loginKey": pwd_login_key(t), "sha1": sha1_mixed(t)} for t in texts]
    key = "0123456789ABCDEF0123456789ABCDEF"
    result["aes"] = [{"text": t, "key": key, "encrypted": aes_encrypt_hex(t, key)} for t in texts]
    with patch("time.time", return_value=1700000000), patch("secrets.token_hex", return_value="0123456789abcdef0123456789abcdef"):
        result["login"] = build_login_request(config, 1700000000000)
        session = Session(user_id="synthetic-user", sign_token="synthetic-sign", encry_token="synthetic-encryption")
        inner = build_inner_base(config, now_ms=1700000000000, vin="SYNTHETIC000000001")
        result["tokenInner"] = inner
        result["token"] = build_token_outer_envelope(config, session, inner, 1700000000000)[0]
        energy_inner = {**inner, "powerType": "0", "requestType": 0, "autoModelNameOut": "Synthetic Atto 3"}
        result["energyTokenInner"] = energy_inner
        result["energyToken"] = build_token_outer_envelope(config, session, energy_inner, 1700000000000)[0]
    result["climate"] = ClimateStartParams(temperature=21, time_span=minutes_to_time_span(10)).to_control_params_map()
    result["climateBooking"] = ClimateScheduleParams(temperature=22, time_span=minutes_to_time_span(20), remote_mode=2, booking_id=1234567890123456789, booking_time=1900000000).to_control_params_map()
    result["climateBookingRemove"] = ClimateScheduleParams(remote_mode=3, booking_id=1234567890123456789).to_control_params_map()
    state = HvacStatus.model_validate({"mainSeatHeatState": 1, "mainSeatVentilationState": 3, "copilotSeatHeatState": 2, "copilotSeatVentilationState": 1, "steeringWheelHeatState": -1})
    result["seatClimate"] = SeatClimateParams.from_current_state(state).with_change("main_heat", 1).to_control_params_map()
    result["batteryHeat"] = BatteryHeatParams(on=False).to_control_params_map()
    markets = json.loads((Path(__file__).resolve().parents[1] / "src/Byd.Client/markets.json").read_text())
    result["regions"] = []
    for code in ("GB", "SG", "AU", "BR", "JP", "UZ", "AE", "MX", "ID", "TR", "KR", "IN", "VN", "SA", "OM", "KZ"):
        market = next(m for m in markets if m["countryCode"] == code)
        regional = BydConfig(username=config.username, password=config.password, country_code=code, language=market["language"], base_url=market["apiBaseUrl"], time_zone="UTC", mqtt_enabled=False)
        with patch("time.time", return_value=1700000000), patch("secrets.token_hex", return_value="0123456789abcdef0123456789abcdef"):
            result["regions"].append({"countryCode": code, "login": build_login_request(regional, 1700000000000), "token": build_token_outer_envelope(regional, session, inner, 1700000000000)[0], "energyToken": build_token_outer_envelope(regional, session, energy_inner, 1700000000000)[0]})
    mqtt_client_id = "oversea_" + md5_hex(config.username)
    mqtt_event = {"event": "vehicleInfo", "vin": "SYNTHETIC000000001", "data": {"code": "0", "uuid": "status-1", "respondData": {
        "elecPercent": 78, "enduranceMileage": 325, "chargeState": 0, "chargingState": 1,
        "leftFrontDoorLock": 2, "rightFrontDoorLock": 2, "leftRearDoorLock": 2, "rightRearDoorLock": 2,
        "time": 1700000000000, "onlineState": 1}}}
    result["mqtt"] = {"clientId": mqtt_client_id, "password": _build_mqtt_password(session, mqtt_client_id, 1700000000),
        "key": session.content_key(), "encrypted": aes_encrypt_hex(json.dumps(mqtt_event), session.content_key()), "event": mqtt_event}
    return result

if __name__ == "__main__":
    destination = Path(__file__).resolve().parents[1] / "tests/Byd.Client.Tests/Fixtures/pybyd-0.0.79.json"
    destination.write_text(json.dumps(generate(), indent=2, ensure_ascii=False) + "\n")
    print("Generated synthetic protocol fixtures; no network requests made.")
