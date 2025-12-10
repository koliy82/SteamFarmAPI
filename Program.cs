using Microsoft.Extensions.Options;
using MongoDB.Driver;
using SteamAPI.Models.Mongo;
using SteamAPI.Services;
using SteamFarmApi.Configurations;

var builder = WebApplication.CreateBuilder(args);

builder.Services.Configure<MongoSettings>(builder.Configuration.GetSection("Mongo"));
builder.Services.AddSingleton(s => s.GetRequiredService<IOptions<MongoSettings>>().Value);
builder.Services.AddSingleton(s =>
{
    var settings = s.GetRequiredService<MongoSettings>();
    return new MongoClient(settings.ConnectionString);
});
builder.Services.AddSingleton(s =>
{
    var settings = s.GetRequiredService<MongoSettings>();
    var client = s.GetRequiredService<MongoClient>();
    return client.GetDatabase(settings.DatabaseName);
});
builder.Services.AddSingleton<AccountRepo>();
builder.Services.AddSingleton<QrRepo>();

builder.Services.AddSingleton<SteamService>();

builder.Services.AddHostedService<InitialSessionsBackgroundService>();

builder.Services.AddControllers();
builder.Services.AddEndpointsApiExplorer();

var app = builder.Build();

if (app.Environment.IsDevelopment())
{
    app.MapOpenApi();
}

app.UseSwaggerUI();

app.UseHttpsRedirection();

app.UseAuthorization();

app.MapControllers();

app.Run();
