using CinemaBooking.Api.Bookings;
using CinemaBooking.Api.Data;
using Microsoft.EntityFrameworkCore;

namespace CinemaBooking.Api.Endpoints;

public static class PublicEndpoints
{
    // Единственные конечные точки с анонимным доступом; всё остальное закрыто fallback-политикой (D-01).
    public static void MapPublicEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapGet("/health", async (AppDbContext db, CancellationToken ct) =>
                await db.Database.CanConnectAsync(ct)
                    ? Results.Ok(new { status = "ok" })
                    : Results.Problem("База данных недоступна", statusCode: StatusCodes.Status503ServiceUnavailable))
            .AllowAnonymous();

        app.MapGet("/movies", async (AppDbContext db, CancellationToken ct) =>
                await db.Movies.OrderBy(m => m.Id)
                    .Select(m => new MovieDto(m.Id, m.Title, m.DurationMinutes))
                    .ToListAsync(ct))
            .AllowAnonymous();

        app.MapGet("/sessions", async (int? movieId, AppDbContext db, BookingService bookings, CancellationToken ct) =>
            {
                var query = db.Sessions.Include(s => s.Movie).Include(s => s.Hall).AsQueryable();
                if (movieId is not null) query = query.Where(s => s.MovieId == movieId);
                var sessions = await query.OrderBy(s => s.StartsAt).ToListAsync(ct);
                return sessions.Select(s => new SessionDto(s.Id, s.MovieId, s.Movie!.Title, s.HallId, s.Hall!.Name,
                    s.StartsAt, s.Status, bookings.IsOpenForBooking(s)));
            })
            .AllowAnonymous();

        app.MapGet("/sessions/{id:int}/seats", async (int id, AppDbContext db, CancellationToken ct) =>
            {
                var session = await db.Sessions.AsNoTracking().SingleOrDefaultAsync(s => s.Id == id, ct);
                if (session is null) return Results.NotFound();

                var booked = await db.BookingSeats.Where(b => b.SessionId == id && b.IsActive)
                    .Select(b => b.SeatId).ToListAsync(ct);
                var blocked = await db.BlockedSeats.Where(b => b.SessionId == id)
                    .Select(b => b.SeatId).ToListAsync(ct);
                var seats = await db.Seats.Where(s => s.HallId == session.HallId)
                    .OrderBy(s => s.Row).ThenBy(s => s.Number).ToListAsync(ct);

                return Results.Ok(seats.Select(s => new SeatStateDto(s.Id, s.Row, s.Number,
                    blocked.Contains(s.Id) ? "blocked" : booked.Contains(s.Id) ? "booked" : "free")));
            })
            .AllowAnonymous();
    }
}
