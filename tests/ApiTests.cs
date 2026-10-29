using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Telecom.Api;

public class ApiTests {
    sealed class Factory : WebApplicationFactory<Program> {
        readonly string path=Path.Combine(Path.GetTempPath(),Guid.NewGuid()+".db");
        protected override void ConfigureWebHost(IWebHostBuilder builder) => builder.ConfigureAppConfiguration((_,c)=>c.AddInMemoryCollection(new Dictionary<string,string?>{{"Database:Provider",Environment.GetEnvironmentVariable("TEST_MARIADB")==null?"Sqlite":"MariaDb"},{"ConnectionStrings:Network",Environment.GetEnvironmentVariable("TEST_MARIADB")??$"Data Source={path};Pooling=False"},{"SeedDemo","true"}}));
        protected override void Dispose(bool disposing){base.Dispose(disposing);if(File.Exists(path))File.Delete(path);}
    }
    [Fact] public async Task Full_workflow_preserves_data_and_rejects_bad_import_atomically() {
        using var factory=new Factory();using var client=factory.CreateClient();
        Assert.Equal(HttpStatusCode.OK,(await client.GetAsync("/api/health")).StatusCode);
        var stations=await client.GetFromJsonAsync<Station[]>("/api/stations");Assert.Equal(6,stations!.Length);
        var report=await client.GetFromJsonAsync<JsonElement>("/api/report");Assert.Equal(84,report.GetProperty("records").GetInt32());Assert.Equal(33222,report.GetProperty("trafficGb").GetDouble());
        var id=stations[0].Id;
        var input=new ObservationInput(id,new(2026,10,29),123.5,100,2,60);
        var created=await client.PostAsJsonAsync("/api/observations",input);Assert.Equal(HttpStatusCode.Created,created.StatusCode);
        var rowId=(await created.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetInt32();
        Assert.Equal(HttpStatusCode.Conflict,(await client.PostAsJsonAsync("/api/observations",input)).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest,(await client.PostAsJsonAsync("/api/observations",input with {DroppedCalls=101})).StatusCode);
        var only=await client.GetFromJsonAsync<JsonElement>("/api/report?from=2026-10-29&to=2026-10-29");Assert.Equal(123.5,only.GetProperty("trafficGb").GetDouble());Assert.Equal(95.833,only.GetProperty("availability").GetDouble());Assert.Equal(2,only.GetProperty("dropRate").GetDouble());
        Assert.Equal(HttpStatusCode.NoContent,(await client.PutAsJsonAsync($"/api/observations/{rowId}",input with {TrafficGb=222})).StatusCode);
        var csv=Csv.Header+"\nBS-001,2026-10-30,45,100,2,0\nUNKNOWN,2026-10-31,2,100,0,0\n";
        Assert.Equal(HttpStatusCode.BadRequest,(await client.PostAsync("/api/import",new StringContent(csv,Encoding.UTF8,"text/csv"))).StatusCode);
        var after=await client.GetFromJsonAsync<JsonElement>("/api/report?from=2026-10-30");Assert.Equal(0,after.GetProperty("records").GetInt32());
        var valid=Csv.Header+"\nBS-001,2026-10-30,45.5,100,2,0\n";
        Assert.Equal(HttpStatusCode.OK,(await client.PostAsync("/api/import",new StringContent(valid,Encoding.UTF8,"text/csv"))).StatusCode);
        Assert.Equal(HttpStatusCode.Conflict,(await client.PostAsync("/api/import",new StringContent(valid,Encoding.UTF8,"text/csv"))).StatusCode);
        var export=await client.GetStringAsync("/api/export?from=2026-10-30");Assert.Contains("BS-001,2026-10-30,45.5,100,2,0",export);
        Assert.Equal(HttpStatusCode.BadRequest,(await client.GetAsync("/api/report?from=oops")).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest,(await client.GetAsync("/api/observations?pageSize=201")).StatusCode);
        Assert.Equal(HttpStatusCode.Conflict,(await client.DeleteAsync($"/api/stations/{id}")).StatusCode);
        Assert.Equal(HttpStatusCode.NoContent,(await client.DeleteAsync($"/api/observations/{rowId}")).StatusCode);
        var s=await client.PostAsJsonAsync("/api/stations",new StationInput("BS-NEW","Восток","5G"));Assert.Equal(HttpStatusCode.Created,s.StatusCode);
        var newStation=await s.Content.ReadFromJsonAsync<Station>();
        Assert.Equal(HttpStatusCode.NoContent,(await client.PutAsJsonAsync($"/api/stations/{newStation!.Id}",new StationInput("BS-NEW","Запад","4G"))).StatusCode);
        Assert.Equal(HttpStatusCode.NoContent,(await client.DeleteAsync($"/api/stations/{newStation.Id}")).StatusCode);
    }
    [Theory]
    [InlineData(-1,0,0,0)] [InlineData(1,2,3,0)] [InlineData(1,3,2,1441)]
    public void Invalid_metrics_are_rejected(double traffic,int calls,int dropped,int down)=>Assert.NotNull(Rules.Validate(new ObservationInput(1,new(2026,1,1),traffic,calls,dropped,down)));
    [Fact] public void Duplicate_csv_rows_are_rejected()=>Assert.NotEmpty(Csv.Parse(Csv.Header+"\nBS-1,2026-01-01,1,1,0,0\nBS-1,2026-01-01,2,1,0,0",new(){{"BS-1",1}}).Errors);
    [Fact] public void Empty_report_is_defined()=>Assert.Equal(0,Rules.Availability([]));
}