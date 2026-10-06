using System.Security.Claims;
using CinemaBooking.Api.Auth;
using CinemaBooking.Api.Bookings;
using CinemaBooking.Api.Data;
using Microsoft.EntityFrameworkCore;

namespace CinemaBooking.Api.Endpoints;

public static class BookingEndpoints
{
    public static void MapBookingEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/bookings").RequireAuthorization(Policies.Viewer);

        // Владелец — всегда текущий пользователь из токена (SR-02).
        group.MapPost("/", async (CreateBookingRequest request, ClaimsPrincipal user, BookingService bookings, CancellationToken ct) =>
        {
            var result = await bookings.CreateAsync(user.GetUserId(), request.SessionId, request.SeatIds ?? [], ct);
            return result.Booking is { } booking
                ? Results.Created($"/bookings/{booking.Id}", BookingDto.From(booking))
                : result.ToProblem();
        });

        group.MapGet("/my", async (ClaimsPrincipal user, AppDbContext db, CancellationToken ct) =>
        {
            var userId = user.GetUserId();
            var list = await db.Bookings.Include(b => b.Seats)
                .Where(b => b.UserId == userId)
                .OrderByDescending(b => b.CreatedAt)
                .ToListAsync(ct);
            return list.Select(BookingDto.From);
        });

        group.MapGet("/{id:guid}", async (Guid id, ClaimsPrincipal user, BookingService bookings, CancellationToken ct) =>
            await bookings.FindOwnAsync(user.GetUserId(), id, ct) is { } booking
                ? Results.Ok(BookingDto.From(booking))
                : Results.NotFound());

        group.MapDelete("/{id:guid}", async (Guid id, ClaimsPrincipal user, BookingService bookings, CancellationToken ct) =>
        {
            var result = await bookings.CancelOwnAsync(user.GetUserId(), id, ct);
            return result.Booking is not null ? Results.NoContent() : result.ToProblem();
        });
    }
}
