using Npgsql;
using HomeChef.Api.Data;
using Microsoft.EntityFrameworkCore;

var builder = WebApplication.CreateBuilder(args);

var connectionString = builder.Configuration
    .GetConnectionString("HomeChefDb")
    ?? throw new InvalidOperationException(
        "Missing ConnectionStrings:HomeChefDb"
    );

builder.Services.AddSingleton<NpgsqlDataSource>(
    _ => NpgsqlDataSource.Create(connectionString)
);

builder.Services.AddDbContext<HomeChefDbContext>(
    (services, options) =>
        options.UseNpgsql(
            services.GetRequiredService<Npgsql.NpgsqlDataSource>()
        )
);

// Add services to the container.
builder.Services.AddControllers();
// Learn more about configuring OpenAPI at https://aka.ms/aspnet/openapi
builder.Services.AddOpenApi();

var app = builder.Build();

// Configure the HTTP request pipeline.
if (app.Environment.IsDevelopment())
{
    app.MapOpenApi();
    app.UseSwaggerUI(options =>
        options.SwaggerEndpoint("/openapi/v1.json", "HomeChef API v1"));
}

app.UseHttpsRedirection();

app.UseAuthorization();

app.MapControllers();

app.Run();
