using CinemaBooking.Api.Auth;
using CinemaBooking.Api.Data;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.IdentityModel.Protocols.OpenIdConnect;
using Npgsql;
using Testcontainers.PostgreSql;

namespace CinemaBooking.Tests.Infrastructure;

[CollectionDefinition(Name)]
public sealed class ApiCollection : ICollectionFixture<ApiFactory>
{
    public const string Name = "api";
}

/// <summary>
/// Поднимает API на настоящей PostgreSQL: блокировки строк и частичный уникальный индекс
/// не проверяются на in-memory провайдере (D-02).
/// Если задана переменная TEST_DB_CONNECTION, используется этот сервер, иначе запускается контейнер Testcontainers.
/// </summary>
public sealed class ApiFactory : WebApplicationFactory<Program>, IAsyncLifetime
{
    private PostgreSqlContainer? _container;
    private string _connectionString = "";
    private string _baseConnection = "";
    private string _dbName = "";

    public async Task InitializeAsync()
    {
        var external = Environment.GetEnvironmentVariable("TEST_DB_CONNECTION");
        string baseConnection;
        if (!string.IsNullOrEmpty(external))
        {
            baseConnection = external;
        }
        else
        {
            _container = new PostgreSqlBuilder("postgres:17").Build();
            await _container.StartAsync();
            baseConnection = _container.GetConnectionString();
        }

        _baseConnection = baseConnection;
        _dbName = $"cinema_test_{Guid.NewGuid():N}";
        await ExecuteOnServerAsync($"CREATE DATABASE \"{_dbName}\"");
        _connectionString = new NpgsqlConnectionStringBuilder(baseConnection) { Database = _dbName, MaxPoolSize = 50 }.ConnectionString;

        // Запуск хоста создаёт схему через DbInitializer.
        _ = Server;
    }

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Testing");
        builder.UseSetting("ConnectionStrings:Default", _connectionString);
        builder.UseSetting("Database:Seed", "false");
        builder.UseSetting("Auth:Authority", TestAuth.Issuer);
        builder.UseSetting("Auth:Audience", TestAuth.Audience);
        builder.UseSetting("Booking:MaxSeatsPerBooking", "6");
        builder.UseSetting("Booking:MaxSeatsPerUserPerSession", "6");

        builder.ConfigureTestServices(services =>
        {
            // Вместо загрузки метаданных Keycloak — тестовый издатель и ключ. Параметры проверки не меняются.
            // Configure (а не PostConfigure): штатная пост-настройка JwtBearer увидит готовую конфигурацию
            // и не будет обращаться к Authority.
            services.Configure<JwtBearerOptions>(JwtBearerDefaults.AuthenticationScheme, o =>
            {
                var config = new OpenIdConnectConfiguration { Issuer = TestAuth.Issuer };
                config.SigningKeys.Add(TestAuth.SigningKey);
                o.Configuration = config;
            });
        });
    }

    public HttpClient Anonymous() => CreateClient();

    public HttpClient Viewer(string userId) =>
        CreateClient().WithToken(TestAuth.CreateToken(userId, [Roles.Viewer]));

    public HttpClient Staff(string userId) =>
        CreateClient().WithToken(TestAuth.CreateToken(userId, [Roles.Staff]));

    public async Task<T> WithDbAsync<T>(Func<AppDbContext, Task<T>> action)
    {
        using var scope = Services.CreateScope();
        return await action(scope.ServiceProvider.GetRequiredService<AppDbContext>());
    }

    public Task WithDbAsync(Func<AppDbContext, Task> action) =>
        WithDbAsync(async db => { await action(db); return 0; });

    /// <summary>Создаёт отдельный зал и сеанс, чтобы тесты не влияли друг на друга.</summary>
    public Task<TestSession> CreateSessionAsync(int seats = 10, DateTimeOffset? startsAt = null) =>
        WithDbAsync(async db =>
        {
            var hall = new Hall { Name = $"Зал {Guid.NewGuid():N}" };
            for (var n = 1; n <= seats; n++) hall.Seats.Add(new Seat { Row = 1, Number = n });
            var movie = new Movie { Title = "Тестовый фильм", DurationMinutes = 90 };
            db.AddRange(hall, movie);
            await db.SaveChangesAsync();

            var session = new Session
            {
                MovieId = movie.Id,
                HallId = hall.Id,
                StartsAt = startsAt ?? DateTimeOffset.UtcNow.AddDays(1),
            };
            db.Sessions.Add(session);
            await db.SaveChangesAsync();
            return new TestSession(session.Id, hall.Seats.OrderBy(s => s.Number).Select(s => s.Id).ToArray());
        });

    public new async Task DisposeAsync()
    {
        await base.DisposeAsync();
        if (_container is not null)
        {
            await _container.DisposeAsync();
            return;
        }
        // Внешний сервер (TEST_DB_CONNECTION) общий — тестовая база удаляется.
        NpgsqlConnection.ClearAllPools();
        await ExecuteOnServerAsync($"DROP DATABASE IF EXISTS \"{_dbName}\" WITH (FORCE)");
    }

    private async Task ExecuteOnServerAsync(string sql)
    {
        await using var conn = new NpgsqlConnection(_baseConnection);
        await conn.OpenAsync();
        await using var cmd = new NpgsqlCommand(sql, conn);
        await cmd.ExecuteNonQueryAsync();
    }
}

public sealed record TestSession(int Id, int[] SeatIds);
