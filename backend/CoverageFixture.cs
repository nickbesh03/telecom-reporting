using Microsoft.EntityFrameworkCore;

namespace Telecom.Api;

public static class CoverageFixture
{
    public static async Task Seed(NetworkDb db)
    {
        // Reserved prefix keeps this fixture separate from the original MVP stations.
        if (await db.Stations.AnyAsync(s => s.Code.StartsWith("MAP-DEMO-"))) return;

        var regions = new[]
        {
            (Code: "SVE", Name: "Свердловская область", Latitude: 56.84, Longitude: 60.60),
            (Code: "KGN", Name: "Курганская область", Latitude: 55.44, Longitude: 65.34),
            (Code: "KHM", Name: "ХМАО — Югра", Latitude: 61.00, Longitude: 69.00),
            (Code: "YNA", Name: "ЯНАО", Latitude: 66.53, Longitude: 66.64)
        };
        var start = new DateTime(2026, 10, 29, 0, 0, 0, DateTimeKind.Utc);
        await using var transaction = await db.Database.BeginTransactionAsync();
        foreach (var region in regions)
        {
            var stations = Enumerable.Range(1, 3).Select(n => new Station
            {
                Code = $"MAP-DEMO-{region.Code}-{n:00}",
                Region = region.Name,
                Technology = "4G"
            }).ToArray();
            db.Stations.AddRange(stations);
            await db.SaveChangesAsync();

            for (var index = 0; index < stations.Length; index++)
            {
                var station = stations[index];
                for (var sector = 1; sector <= 3; sector++)
                {
                    db.Add(new MapCell
                    {
                        StationId = station.Id, Sector = sector,
                        Latitude = region.Latitude + index * 0.018,
                        Longitude = region.Longitude + index * 0.027,
                        Azimuth = (sector - 1) * 120
                    });
                    for (var hour = 0; hour < 168; hour++)
                    {
                        // Omit two samples entirely to exercise completeness checks.
                        if (region.Code == "KHM" && index == 0 && sector == 2 && hour is 40 or 41)
                            continue;

                        var localHour = (hour + 5) % 24;
                        var attempts = 220 + localHour * 39 + index * 70 + sector * 31;
                        var connected = attempts - 8 - index;
                        var dropped = 2 + (hour + sector) % 7;
                        var bytes = (long)connected * (7_000_000 + localHour * 350_000);

                        // A burst of call drops on one sector. Other sectors stay normal.
                        if (region.Code == "SVE" && index == 0 && sector == 1 && hour is >= 61 and <= 63)
                            dropped = connected / 8;

                        // Recorded zeros for all sectors of one station during one hour.
                        if (region.Code == "YNA" && index == 2 && hour == 79)
                        {
                            attempts = connected = dropped = 0;
                            bytes = 0;
                        }

                        db.Add(new CellHour
                        {
                            StationId = station.Id, Sector = sector, HourUtc = start.AddHours(hour),
                            Attempts = attempts, Connected = connected, Dropped = dropped, TrafficBytes = bytes
                        });
                    }
                }
            }
            await db.SaveChangesAsync();
        }
        await transaction.CommitAsync();
    }
}
