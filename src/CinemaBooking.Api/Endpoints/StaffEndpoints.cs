using System.Security.Claims;
using CinemaBooking.Api.Auth;
using CinemaBooking.Api.Bookings;
using CinemaBooking.Api.Data;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace CinemaBooking.Api.Endpoints;

public static class StaffEndpoints
{
    public const string Prefix = "/staff";

    public static void MapStaffEndpoints(this IEndpointRouteBuilder app)
    {
        // D-01: политика назначается на всю группу; новая административная операция не может остаться без проверки роли.
        var group = app.MapGroup(Prefix).RequireAuthorization(Policies.Staff);

        group.MapGet("/bookings", async (int? sessionId, AppDbContext db, CancellationToken ct) =>
        {
            var query = db.Bookings.Include(b => b.Seats).AsQueryable();
            if (sessionId is not null) query = query.Where(b => b.SessionId == sessionId);
            var list = await query.OrderByDescending(b => b.CreatedAt).ToListAsync(ct);
            return list.Select(StaffBookingDto.From);
        });

        group.MapDelete("/bookings/{id:guid}", async (Guid id, [FromBody] StaffCancelRequest request,
            ClaimsPrincipal user, BookingService bookings, CancellationToken ct) =>
        {
            var result = await bookings.CancelByStaffAsync(user.GetUserId(), id, request.Reason, ct);
            return result.Booking is { } booking ? Results.Ok(StaffBookingDto.From(booking)) : result.ToProblem();
        });

        group.MapPost("/sessions", async (CreateSessionRequest request, AppDbContext db, TimeProvider time, CancellationToken ct) =>
        {
            if (!await db.Movies.AnyAsync(m => m.Id == request.MovieId, ct) || !await db.Halls.AnyAsync(h => h.Id == request.HallId, ct))
                return Results.Problem("Фильм или зал не найден.", statusCode: StatusCodes.Status400BadRequest);
            if (request.StartsAt <= time.GetUtcNow())
                return Results.Problem("Сеанс должен начинаться в будущем.", statusCode: StatusCodes.Status400BadRequest);

            var session = new Session { MovieId = request.MovieId, HallId = request.HallId, StartsAt = request.StartsAt.ToUniversalTime() };
            db.Sessions.Add(session);
            await db.SaveChangesAsync(ct);
            return Results.Created($"/sessions/{session.Id}", new { session.Id });
        });

        group.MapPatch("/sessions/{id:int}", async (int id, UpdateSessionRequest request, AppDbContext db, TimeProvider time, CancellationToken ct) =>
        {
            await using var tx = await db.Database.BeginTransactionAsync(ct);
            var session = await SessionLock.LockAsync(db, id, ct);
            if (session is null) return Results.NotFound();
            if (session.Status == SessionStatus.Cancelled)
                return Results.Problem("Сеанс уже отменён.", statusCode: StatusCodes.Status409Conflict);

            if (request.StartsAt is { } startsAt)
            {
                if (startsAt <= time.GetUtcNow())
                    return Results.Problem("Сеанс должен начинаться в будущем.", statusCode: StatusCodes.Status400BadRequest);
                session.StartsAt = startsAt.ToUniversalTime();
            }
            if (request.Cancelled == true)
            {
                session.Status = SessionStatus.Cancelled;
                session.CancelledAt = time.GetUtcNow();
            }

            await db.SaveChangesAsync(ct);
            await tx.CommitAsync(ct);
            return Results.NoContent();
        });

        group.MapPatch("/sessions/{id:int}/seats/{seatId:int}", async (int id, int seatId, SeatAvailabilityRequest request,
            AppDbContext db, CancellationToken ct) =>
        {
            await using var tx = await db.Database.BeginTransactionAsync(ct);
            var session = await SessionLock.LockAsync(db, id, ct);
            if (session is null) return Results.NotFound();
            if (!await db.Seats.AnyAsync(s => s.Id == seatId && s.HallId == session.HallId, ct))
                return Results.Problem("Место не относится к залу сеанса.", statusCode: StatusCodes.Status400BadRequest);

            var block = await db.BlockedSeats.FindAsync([id, seatId], ct);
            if (!request.Available && block is null)
                db.BlockedSeats.Add(new BlockedSeat { SessionId = id, SeatId = seatId });
            else if (request.Available && block is not null)
                db.BlockedSeats.Remove(block);

            await db.SaveChangesAsync(ct);
            await tx.CommitAsync(ct);
            return Results.NoContent();
        });
    }
}
