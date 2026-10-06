using Microsoft.EntityFrameworkCore;

namespace CinemaBooking.Api.Data;

public static class DbInitializer
{
    /// <summary>
    /// Создаёт схему (для минимальной версии — без миграций) и при пустой БД добавляет демонстрационные данные.
    /// БД в docker compose может стартовать позже API, поэтому подключение повторяется.
    /// </summary>
    public static async Task InitializeAsync(IServiceProvider services, bool seed, CancellationToken ct = default)
    {
        using var scope = services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var logger = scope.ServiceProvider.GetRequiredService<ILoggerFactory>().CreateLogger(nameof(DbInitializer));

        for (var attempt = 1; ; attempt++)
        {
            try
            {
                await db.Database.EnsureCreatedAsync(ct);
                break;
            }
            catch (Exception ex) when (attempt < 10)
            {
                logger.LogWarning(ex, "БД недоступна, попытка {Attempt}", attempt);
                await Task.Delay(TimeSpan.FromSeconds(2), ct);
            }
        }

        if (seed && !await db.Movies.AnyAsync(ct))
            await SeedAsync(db, ct);
    }

    private static async Task SeedAsync(AppDbContext db, CancellationToken ct)
    {
        var hall1 = new Hall { Name = "Зал 1" };
        var hall2 = new Hall { Name = "Зал 2" };
        for (var row = 1; row <= 5; row++)
            for (var n = 1; n <= 10; n++)
                hall1.Seats.Add(new Seat { Row = row, Number = n });
        for (var row = 1; row <= 3; row++)
            for (var n = 1; n <= 8; n++)
                hall2.Seats.Add(new Seat { Row = row, Number = n });

        var movie1 = new Movie { Title = "Пример фильма 1", DurationMinutes = 120 };
        var movie2 = new Movie { Title = "Пример фильма 2", DurationMinutes = 95 };
        db.AddRange(hall1, hall2, movie1, movie2);
        await db.SaveChangesAsync(ct);

        var tomorrow = new DateTimeOffset(DateTime.UtcNow.Date.AddDays(1), TimeSpan.Zero);
        db.Sessions.AddRange(
            new Session { MovieId = movie1.Id, HallId = hall1.Id, StartsAt = tomorrow.AddHours(18) },
            new Session { MovieId = movie1.Id, HallId = hall1.Id, StartsAt = tomorrow.AddHours(21) },
            new Session { MovieId = movie2.Id, HallId = hall2.Id, StartsAt = tomorrow.AddDays(1).AddHours(19) });
        await db.SaveChangesAsync(ct);
    }
}
