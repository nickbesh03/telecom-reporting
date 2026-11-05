import json
import sys
from urllib.parse import urlencode
from urllib.request import urlopen


def get(path, **params):
    with urlopen(f"{sys.argv[1]}/api/coverage/{path}?{urlencode(params)}", timeout=10) as response:
        return json.load(response)


cells = {c["stationCode"]: c["stationId"] for c in get("cells")
         if c["stationCode"].startswith("MAP-DEMO-")}
assert len(cells) == 12, cells
week = get("metrics", level="region", **{
    "from": "2026-10-29T00:00:00Z", "to": "2026-11-05T00:00:00Z"
})["items"]
regions = {"Свердловская область", "Курганская область", "ХМАО — Югра", "ЯНАО"}
fixture = {r["key"]: r for r in week if r["key"] in regions}
assert set(fixture) == regions, fixture
assert sum(r["expectedSamples"] for r in fixture.values()) == 6048
assert sum(r["receivedSamples"] for r in fixture.values()) == 6046
assert fixture["ХМАО — Югра"]["status"] == "partial"


def point(code, sector, start, end):
    return get("metrics", level="hour", stationId=cells[code], sector=sector,
               **{"from": start, "to": end})["items"][0]


incident = point("MAP-DEMO-SVE-01", 1, "2026-10-31T13:00:00Z", "2026-10-31T14:00:00Z")
assert incident["dropRate"] > 12, incident
missing = point("MAP-DEMO-KHM-01", 2, "2026-10-30T16:00:00Z", "2026-10-30T17:00:00Z")
assert missing["status"] == "missing" and missing["dropRate"] is None, missing
zero = point("MAP-DEMO-YNA-03", 1, "2026-11-01T07:00:00Z", "2026-11-01T08:00:00Z")
assert zero["status"] == "complete" and zero["trafficBytes"] == 0, zero
assert zero["dropRate"] is None, zero
print("Coverage fixture verified: 4 regions, 12 stations, 6046 samples, incident, missing and zero.")
