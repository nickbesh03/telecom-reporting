using Microsoft.EntityFrameworkCore;
namespace Telecom.Api;
public class Station {
    public int Id {get;set;}
    public string Code {get;set;} = "";
    public string Region {get;set;} = "";
    public string Technology {get;set;} = "4G";
}
public class Observation {
    public int Id {get;set;}
    public int StationId {get;set;}
    public Station Station {get;set;} = null!;
    public DateOnly Date {get;set;}
    public double TrafficGb {get;set;}
    public int Calls {get;set;}
    public int DroppedCalls {get;set;}
    public int DowntimeMinutes {get;set;}
}
public record StationInput(string Code, string Region, string Technology);
public record ObservationInput(int StationId, DateOnly Date, double TrafficGb, int Calls, int DroppedCalls, int DowntimeMinutes);
public class NetworkDb(DbContextOptions<NetworkDb> options) : DbContext(options) {
    public DbSet<Station> Stations => Set<Station>();
    public DbSet<Observation> Observations => Set<Observation>();
    protected override void OnModelCreating(ModelBuilder m) {
        m.Entity<Station>().Property(x=>x.Code).HasMaxLength(40);
        m.Entity<Station>().Property(x=>x.Region).HasMaxLength(80);
        m.Entity<Station>().Property(x=>x.Technology).HasMaxLength(8);
        m.Entity<Station>().HasIndex(x=>x.Code).IsUnique();
        m.Entity<Observation>().HasIndex(x=>new {x.StationId,x.Date}).IsUnique();
        m.Entity<Observation>().HasOne(x=>x.Station).WithMany().HasForeignKey(x=>x.StationId).OnDelete(DeleteBehavior.Restrict);
    }
}
public static class Rules {
    public static string? Validate(ObservationInput x) => x.StationId<=0 ? "Выберите станцию" : x.Date==default ? "Укажите дату" :
        !double.IsFinite(x.TrafficGb) || x.TrafficGb<0 || x.TrafficGb>1e9 ? "Трафик: от 0 до 1 млрд ГБ" :
        x.Calls<0 || x.DroppedCalls<0 || x.DroppedCalls>x.Calls ? "Обрывы должны быть от 0 до числа вызовов" :
        x.DowntimeMinutes<0 || x.DowntimeMinutes>1440 ? "Простой: от 0 до 1440 минут" : null;
    public static string? Validate(StationInput x) => string.IsNullOrWhiteSpace(x.Code) || x.Code.Trim().Length>40 || !System.Text.RegularExpressions.Regex.IsMatch(x.Code.Trim(), @"^[A-Za-z0-9_-]+$") ? "Код: латинские буквы, цифры, _ или -, до 40 символов" :
        string.IsNullOrWhiteSpace(x.Region) || x.Region.Trim().Length>80 ? "Регион: от 1 до 80 символов" :
        !new[]{"2G","3G","4G","5G"}.Contains(x.Technology) ? "Технология: 2G, 3G, 4G или 5G" : null;
    public static double Availability(IEnumerable<Observation> rows) {
        var list=rows.ToList();
        return list.Count==0 ? 0 : Math.Round(100*(1-list.Sum(x=>(double)x.DowntimeMinutes)/(list.Count*1440d)),3);
    }
}
public static class Demo {
    public static async Task Seed(NetworkDb db) {
        if (await db.Stations.AnyAsync()) return;
        var stations=new[]{new Station{Code="BS-001",Region="Центр",Technology="4G"},new Station{Code="BS-002",Region="Центр",Technology="5G"},new Station{Code="BS-003",Region="Север",Technology="4G"},new Station{Code="BS-004",Region="Юг",Technology="3G"},new Station{Code="BS-005",Region="Север",Technology="4G"},new Station{Code="BS-006",Region="Юг",Technology="4G"}};
        db.Stations.AddRange(stations);
        await db.SaveChangesAsync();
        for(var d=0;d<14;d++) for(var s=0;s<stations.Length;s++) db.Observations.Add(new Observation{StationId=stations[s].Id,Date=new DateOnly(2026,10,15).AddDays(d),TrafficGb=180+s*55+d*12,Calls=1000+s*200+d*20,DroppedCalls=8+s*3, DowntimeMinutes=s==2 && d>9 ? 120 : (s+d)%4*3});
        await db.SaveChangesAsync();
    }
}
