using System.Text.Json.Serialization;
using CinemaBooking.Api.Auth;
using CinemaBooking.Api.Bookings;
using CinemaBooking.Api.Data;
using CinemaBooking.Api.Endpoints;
using Microsoft.EntityFrameworkCore;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddDbContext<AppDbContext>(o => o.UseNpgsql(builder.Configuration.GetConnectionString("Default")));
builder.Services.Configure<BookingOptions>(builder.Configuration.GetSection("Booking"));
builder.Services.AddSingleton(TimeProvider.System);
builder.Services.AddScoped<BookingService>();
builder.Services.AddKeycloakJwt(builder.Configuration);
builder.Services.AddProblemDetails();
builder.Services.AddOpenApi();
builder.Services.ConfigureHttpJsonOptions(o => o.SerializerOptions.Converters.Add(new JsonStringEnumConverter()));

var app = builder.Build();

// Некорректное тело запроса — ошибка клиента (400), а не сервера, в том числе в Development.
app.UseExceptionHandler(new ExceptionHandlerOptions
{
    StatusCodeSelector = ex => ex is BadHttpRequestException bad ? bad.StatusCode : StatusCodes.Status500InternalServerError,
});
app.UseStatusCodePages();
app.UseAuthentication();
app.UseAuthorization();

if (app.Environment.IsDevelopment())
    app.MapOpenApi().AllowAnonymous();

app.MapPublicEndpoints();
app.MapBookingEndpoints();
app.MapStaffEndpoints();

await DbInitializer.InitializeAsync(app.Services, seed: app.Configuration.GetValue("Database:Seed", true));

app.Run();

public partial class Program;
