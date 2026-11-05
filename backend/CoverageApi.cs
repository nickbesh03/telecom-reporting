using Microsoft.EntityFrameworkCore;

namespace Telecom.Api;

public sealed class MapCell
{
    public int StationId { get; set; }
    public Station Station { get; set; } = null!;
    public int Sector { get; set; }
    public double Latitude { get; set; }
    public double Longitude { get; set; }
    public int Azimuth { get; set; }
}

public sealed class CellHour
{
    public int StationId { get; set; }
    public int Sector { get; set; }
    public DateTime HourUtc { get; set; }
    public int Attempts { get; set; }
    public int Connected { get; set; }
    public int Dropped { get; set; }
    public long TrafficBytes { get; set; }
}

public static class CoverageApi
{
    // Additive schema upgrade for demo databases already initialized by EnsureCreated.
    public static async Task EnsureSchema(NetworkDb db)
    {
        await db.Database.ExecuteSqlRawAsync("""
            CREATE TABLE IF NOT EXISTS MapCells (
                StationId INT NOT NULL, Sector INT NOT NULL,
                Latitude DOUBLE NOT NULL, Longitude DOUBLE NOT NULL, Azimuth INT NOT NULL,
                PRIMARY KEY (StationId, Sector),
                FOREIGN KEY (StationId) REFERENCES Stations(Id) ON DELETE CASCADE
            )
            """);
        await db.Database.ExecuteSqlRawAsync("""
            CREATE TABLE IF NOT EXISTS CellHours (
                StationId INT NOT NULL, Sector INT NOT NULL, HourUtc DATETIME NOT NULL,
                Attempts INT NOT NULL, Connected INT NOT NULL, Dropped INT NOT NULL, TrafficBytes BIGINT NOT NULL,
                PRIMARY KEY (StationId, Sector, HourUtc),
                FOREIGN KEY (StationId, Sector) REFERENCES MapCells(StationId, Sector) ON DELETE CASCADE,
                CHECK (Attempts >= Connected AND Connected >= Dropped AND Dropped >= 0 AND TrafficBytes >= 0)
            )
            """);
    }

    public static async Task Seed(NetworkDb db)
    {
        if (await db.Set<MapCell>().AnyAsync()) return;
        var stations = await db.Stations.Where(s => s.Code == "BS-001" || s.Code == "BS-002" || s.Code == "BS-003")
            .OrderBy(s => s.Code).ToListAsync();
        var day = new DateTime(2026, 10, 28, 0, 0, 0, DateTimeKind.Utc);
        for (var i = 0; i < stations.Count; i++)
        {
            for (var sector = 1; sector <= 3; sector++)
            {
                var id = stations[i].Id;
                db.Add(new MapCell { StationId = id, Sector = sector, Latitude = 56.83 + i * 0.025,
                    Longitude = 60.60 + i * 0.04, Azimuth = (sector - 1) * 120 });
                for (var h = 0; h < 24; h++)
                    db.Add(new CellHour { StationId = id, Sector = sector, HourUtc = day.AddHours(h),
                        Attempts = 1250, Connected = 1200, Dropped = i == 0 && sector == 1 && h == 13 ? 54 : 4,
                        TrafficBytes = (4L + h + sector + i) * 1_000_000_000 });
            }
        }
        await db.SaveChangesAsync();
    }

    public static void MapCoverageApi(this WebApplication app)
    {
        var map = app.MapGroup("/api/coverage");
        map.MapGet("/cells", async (NetworkDb db, string? region, int? stationId, CancellationToken ct) =>
            await Cells(db, region, stationId).OrderBy(c => c.StationId).ThenBy(c => c.Sector)
                .Select(c => new { c.StationId, c.Sector, c.Latitude, c.Longitude, c.Azimuth,
                    stationCode = c.Station.Code, c.Station.Region, c.Station.Technology }).ToListAsync(ct));

        map.MapGet("/metrics", async (NetworkDb db, DateTimeOffset from, DateTimeOffset to,
            string? level, string? region, int? stationId, int? sector, CancellationToken ct) =>
        {
            level ??= "region";
            if (level is not ("region" or "station" or "cell" or "hour"))
                return Results.BadRequest(new { detail = "level: region, station, cell или hour" });
            var start = from.UtcDateTime;
            var end = to.UtcDateTime;
            if (end <= start || end - start > TimeSpan.FromDays(31) ||
                start.Ticks % TimeSpan.TicksPerHour != 0 || end.Ticks % TimeSpan.TicksPerHour != 0)
                return Results.BadRequest(new { detail = "Выберите границы по целым часам и период до 31 дня" });

            var cells = Cells(db, region, stationId);
            if (sector.HasValue) cells = cells.Where(c => c.Sector == sector.Value);
            var topology = await cells.Select(c => new { c.StationId, c.Sector, c.Station.Region }).ToListAsync(ct);
            var hours = await db.Set<CellHour>().AsNoTracking()
                .Where(h => h.HourUtc >= start && h.HourUtc < end)
                .Join(cells, h => new { h.StationId, h.Sector }, c => new { c.StationId, c.Sector },
                    (h, c) => new { Sample = h, c.Station.Region }).ToListAsync(ct);

            string Key(string name, int id, int n) => level switch
            {
                "region" => name,
                "station" => id.ToString(),
                _ => $"{id}/{n}"
            };
            var samples = hours.ToLookup(h => level == "hour"
                ? DateTime.SpecifyKind(h.Sample.HourUtc, DateTimeKind.Utc).ToString("O")
                : Key(h.Region, h.Sample.StationId, h.Sample.Sector), h => h.Sample);
            var buckets = level == "hour"
                ? Enumerable.Range(0, (int)(end - start).TotalHours)
                    .Select(i => (Key: start.AddHours(i).ToString("O"), Expected: (long)topology.Count))
                : topology.GroupBy(c => Key(c.Region, c.StationId, c.Sector))
                    .Select(g => (Key: g.Key, Expected: g.LongCount() * (long)(end - start).TotalHours));
            return Results.Ok(new { from = start, to = end, level, items = buckets.OrderBy(b => b.Key)
                .Select(b => Summarize(b.Key, b.Expected, samples[b.Key])) });
        });
    }

    private static IQueryable<MapCell> Cells(NetworkDb db, string? region, int? stationId)
    {
        var query = db.Set<MapCell>().AsNoTracking();
        if (!string.IsNullOrWhiteSpace(region)) query = query.Where(c => c.Station.Region == region);
        if (stationId.HasValue) query = query.Where(c => c.StationId == stationId.Value);
        return query;
    }

    private static object Summarize(string key, long expected, IEnumerable<CellHour> samples)
    {
        var rows = samples.ToList();
        long attempts = rows.Sum(r => (long)r.Attempts);
        long connected = rows.Sum(r => (long)r.Connected);
        long dropped = rows.Sum(r => (long)r.Dropped);
        return new { key, expectedSamples = expected, receivedSamples = rows.Count,
            status = rows.Count == 0 ? "missing" : rows.Count < expected ? "partial" : "complete",
            attempts, connected, dropped, trafficBytes = rows.Sum(r => r.TrafficBytes),
            successRate = attempts == 0 ? (double?)null : 100d * connected / attempts,
            dropRate = connected == 0 ? (double?)null : 100d * dropped / connected };
    }
}
