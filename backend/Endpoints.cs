using System.Globalization;
using System.Text;
using Microsoft.EntityFrameworkCore;
namespace Telecom.Api;
public static class Endpoints {
    static IResult Error(string message, int status=400) => Results.Problem(message,statusCode:status);
    static async Task<IResult> Save(NetworkDb db) {
        try { await db.SaveChangesAsync(); return Results.NoContent(); }
        catch(DbUpdateException) { return Error("Конфликт данных: код станции или дата уже существуют либо запись используется",409); }
    }
    static IQueryable<Observation> Filter(NetworkDb db, string? from, string? to, string? region, int? stationId) {
        var q=db.Observations.AsNoTracking().Include(x=>x.Station).AsQueryable();
        if(from!=null) { if(!DateOnly.TryParseExact(from,"yyyy-MM-dd",out var f)) throw new ArgumentException("Некорректная дата начала"); q=q.Where(x=>x.Date>=f); }
        if(to!=null) { if(!DateOnly.TryParseExact(to,"yyyy-MM-dd",out var t)) throw new ArgumentException("Некорректная дата конца"); q=q.Where(x=>x.Date<=t); }
        if(from!=null && to!=null && string.CompareOrdinal(from,to)>0) throw new ArgumentException("Начало периода позже конца");
        if(!string.IsNullOrEmpty(region)) q=q.Where(x=>x.Station.Region==region);
        if(stationId!=null) q=q.Where(x=>x.StationId==stationId);
        return q;
    }
    public static void MapNetworkApi(this WebApplication app) {
        var api=app.MapGroup("/api");
        api.AddEndpointFilter(async (context,next)=>{try{return await next(context);}catch(ArgumentException e){return Error(e.Message);}});
        api.MapGet("/stations", async (NetworkDb db)=>await db.Stations.AsNoTracking().OrderBy(x=>x.Code).ToListAsync());
        api.MapPost("/stations", async (StationInput x,NetworkDb db)=>{
            if(Rules.Validate(x) is {} e) return Error(e);
            var s=new Station{Code=x.Code.Trim().ToUpperInvariant(),Region=x.Region.Trim(),Technology=x.Technology};
            db.Stations.Add(s); var result=await Save(db); return result is Microsoft.AspNetCore.Http.HttpResults.NoContent ? Results.Created($"/api/stations/{s.Id}",s) : result;
        });
        api.MapPut("/stations/{id:int}", async (int id, StationInput x, NetworkDb db)=>{
            if(Rules.Validate(x) is {} e) return Error(e);
            var s=await db.Stations.FindAsync(id); if(s==null)return Results.NotFound();
            s.Code=x.Code.Trim().ToUpperInvariant();s.Region=x.Region.Trim();s.Technology=x.Technology;return await Save(db);
        });
        api.MapDelete("/stations/{id:int}",async(int id, NetworkDb db)=>{
            var s=await db.Stations.FindAsync(id);if(s==null)return Results.NotFound();
            if(await db.Observations.AnyAsync(x=>x.StationId==id))return Error("Сначала удалите показатели этой станции",409);
            db.Remove(s);return await Save(db);
        });
        api.MapGet("/observations",async(NetworkDb db,string? from,string? to,string? region,int? stationId,int page=1,int pageSize=50)=>{
            if(page<1 || pageSize<1 || pageSize>200)return Error("page >= 1, pageSize от 1 до 200");
            var q=Filter(db,from,to,region,stationId);
            return Results.Ok(new {total=await q.CountAsync(),page,pageSize,items=await q.OrderByDescending(x=>x.Date).ThenBy(x=>x.Station.Code).Skip((page-1)*pageSize).Take(pageSize).Select(x=>new{x.Id,x.StationId,stationCode=x.Station.Code,region=x.Station.Region,x.Date,x.TrafficGb,x.Calls,x.DroppedCalls,x.DowntimeMinutes}).ToListAsync()});
        });
        api.MapPost("/observations",async(ObservationInput x,NetworkDb db)=>{
            if(Rules.Validate(x) is {} e)return Error(e);
            if(!await db.Stations.AnyAsync(s=>s.Id==x.StationId))return Error("Станция не найдена");
            var row=Create(x);db.Add(row);var r=await Save(db);return r is Microsoft.AspNetCore.Http.HttpResults.NoContent ? Results.Created($"/api/observations/{row.Id}",new{row.Id}) : r;
        });
        api.MapPut("/observations/{id:int}",async(int id,ObservationInput x,NetworkDb db)=>{
            if(Rules.Validate(x) is {} e)return Error(e);
            if(!await db.Stations.AnyAsync(s=>s.Id==x.StationId))return Error("Станция не найдена");
            var row=await db.Observations.FindAsync(id);if(row==null)return Results.NotFound();
            row.StationId=x.StationId;row.Date=x.Date;row.TrafficGb=x.TrafficGb;row.Calls=x.Calls;row.DroppedCalls=x.DroppedCalls;row.DowntimeMinutes=x.DowntimeMinutes;return await Save(db);
        });
        api.MapDelete("/observations/{id:int}",async(int id,NetworkDb db)=>{var row=await db.Observations.FindAsync(id);if(row==null)return Results.NotFound();db.Remove(row);return await Save(db);});
        api.MapGet("/report",async(NetworkDb db,string? from,string? to,string? region,int? stationId)=>{
            var rows=await Filter(db,from,to,region,stationId).ToListAsync();
            return Results.Ok(new{records=rows.Count,stations=rows.Select(x=>x.StationId).Distinct().Count(),trafficGb=rows.Sum(x=>x.TrafficGb),calls=rows.Sum(x=>(long)x.Calls),dropRate=rows.Sum(x=>(long)x.Calls)==0 ? 0 : Math.Round(100d*rows.Sum(x=>(long)x.DroppedCalls)/rows.Sum(x=>(long)x.Calls),3),availability=Rules.Availability(rows),downtimeMinutes=rows.Sum(x=>(long)x.DowntimeMinutes),daily=rows.GroupBy(x=>x.Date).OrderBy(x=>x.Key).Select(g=>new{date=g.Key,trafficGb=g.Sum(x=>x.TrafficGb)}),regions=rows.GroupBy(x=>x.Station.Region).OrderBy(x=>x.Key).Select(g=>new{region=g.Key,records=g.Count(),trafficGb=g.Sum(x=>x.TrafficGb),availability=Rules.Availability(g)})});
        });
        api.MapGet("/export",async(NetworkDb db,string? from,string? to,string? region,int? stationId)=>{
            var rows=await Filter(db,from,to,region,stationId).OrderBy(x=>x.Date).ThenBy(x=>x.Station.Code).ToListAsync();
            var b=new StringBuilder(Csv.Header+"\n");foreach(var x in rows)b.AppendLine(FormattableString.Invariant($"{x.Station.Code},{x.Date:yyyy-MM-dd},{x.TrafficGb},{x.Calls},{x.DroppedCalls},{x.DowntimeMinutes}"));
            return Results.File(Encoding.UTF8.GetBytes(b.ToString()),"text/csv; charset=utf-8","telecom-report.csv");
        });
        api.MapPost("/import",async(HttpRequest request,NetworkDb db)=>{
            if(request.ContentLength>1024*1024)return Error("Файл больше 1 МБ",413);
            using var reader=new StreamReader(request.Body,Encoding.UTF8);
            var buffer=new char[1024*1024+1];var count=0;
            while(count<buffer.Length){var n=await reader.ReadAsync(buffer.AsMemory(count));if(n==0)break;count+=n;}
            if(count>1024*1024)return Error("Файл больше 1 МБ",413);
            var stations=await db.Stations.ToDictionaryAsync(x=>x.Code,x=>x.Id);
            var parsed=Csv.Parse(new string(buffer,0,count),stations);
            if(parsed.Errors.Count>0)return Results.BadRequest(new{detail="Импорт отменён. Исправьте строки",errors=parsed.Errors});
            var keys=(await db.Observations.Select(x=>new{x.StationId,x.Date}).ToListAsync()).Select(x=>(x.StationId,x.Date)).ToHashSet();
            if(parsed.Rows.Any(x=>keys.Contains((x.StationId,x.Date))))return Error("В базе уже есть станция и дата из файла. Импорт отменён",409);
            db.Observations.AddRange(parsed.Rows.Select(Create));var r=await Save(db);
            return r is Microsoft.AspNetCore.Http.HttpResults.NoContent ? Results.Ok(new{imported=parsed.Rows.Count}) : r;
        });
    }
    static Observation Create(ObservationInput x)=>new(){StationId=x.StationId,Date=x.Date,TrafficGb=x.TrafficGb,Calls=x.Calls,DroppedCalls=x.DroppedCalls,DowntimeMinutes=x.DowntimeMinutes};
}
public static class Csv {
    public const string Header="stationCode,date,trafficGb,calls,droppedCalls,downtimeMinutes";
    public record Parsed(List<ObservationInput> Rows,List<string> Errors);
    public static Parsed Parse(string text,Dictionary<string,int> stations) {
        var rows=new List<ObservationInput>();var errors=new List<string>();var keys=new HashSet<(int,DateOnly)>();
        var lines=text.TrimStart('\uFEFF').Replace("\r","").Split('\n');
        if(lines[0].Trim()!=Header)return new(rows,new(){"Неверный заголовок CSV"});
        for(var i=1;i<lines.Length;i++) {
            if(string.IsNullOrWhiteSpace(lines[i]))continue;
            if(rows.Count>=5000){errors.Add("Максимум 5000 строк");break;}
            var p=lines[i].Split(',');
            if(p.Length!=6 || !stations.TryGetValue(p[0].Trim().ToUpperInvariant(),out var id) || !DateOnly.TryParseExact(p[1],"yyyy-MM-dd",out var date) || !double.TryParse(p[2],NumberStyles.Float,CultureInfo.InvariantCulture,out var traffic) || !int.TryParse(p[3],out var calls) || !int.TryParse(p[4],out var dropped) || !int.TryParse(p[5],out var down)) {errors.Add($"Строка {i+1}: формат или код станции неверен");continue;}
            var x=new ObservationInput(id,date,traffic,calls,dropped,down);
            if(Rules.Validate(x) is {} e)errors.Add($"Строка {i+1}: {e}");
            else if(!keys.Add((id,date))) errors.Add($"Строка {i+1}: повтор станции и даты");
            else rows.Add(x);
        }
        if(rows.Count==0 && errors.Count==0)errors.Add("Нет строк для импорта");
        return new(rows,errors);
    }
}
