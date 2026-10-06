using System.Net;
using System.Net.Http.Json;
using CinemaBooking.Api.Data;
using CinemaBooking.Tests.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace CinemaBooking.Tests;

/// <summary>Будущая проверка D-02 (SR-04, SR-05, SR-06; T-04, T-05, T-07).</summary>
[Collection(ApiCollection.Name)]
public class BookingConsistencyTests(ApiFactory api)
{
    [Fact]
    public async Task Parallel_requests_for_same_seat_create_exactly_one_booking()
    {
        var session = await api.CreateSessionAsync();
        var seat = session.SeatIds[4];
        var a = api.Viewer("viewer-a-" + Guid.NewGuid());
        var b = api.Viewer("viewer-b-" + Guid.NewGuid());

        var start = new TaskCompletionSource();
        var requests = Enumerable.Range(0, 20).Select(async i =>
        {
            await start.Task;
            return await (i % 2 == 0 ? a : b).BookAsync(session.Id, seat);
        }).ToList();
        start.SetResult();
        var responses = await Task.WhenAll(requests);

        Assert.Equal(1, responses.Count(r => r.StatusCode == HttpStatusCode.Created));
        Assert.Equal(19, responses.Count(r => r.StatusCode == HttpStatusCode.Conflict));
        var active = await api.WithDbAsync(db => db.BookingSeats.CountAsync(s => s.SessionId == session.Id && s.SeatId == seat && s.IsActive));
        Assert.Equal(1, active);
    }

    [Fact]
    public async Task Request_with_seat_from_another_hall_is_rejected_entirely()
    {
        var session = await api.CreateSessionAsync();
        var otherHall = await api.CreateSessionAsync();
        var viewer = api.Viewer("viewer-" + Guid.NewGuid());

        var response = await viewer.BookAsync(session.Id, session.SeatIds[2], session.SeatIds[3], otherHall.SeatIds[0]);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var states = await viewer.SeatStatesAsync(session.Id);
        Assert.Equal("free", states[session.SeatIds[2]]);
        Assert.Equal("free", states[session.SeatIds[3]]);
        Assert.False(await api.WithDbAsync(db => db.Bookings.AnyAsync(x => x.SessionId == session.Id)));
    }

    [Fact]
    public async Task Request_with_one_booked_seat_is_rejected_entirely()
    {
        var session = await api.CreateSessionAsync();
        await api.Viewer("viewer-a-" + Guid.NewGuid()).BookOkAsync(session.Id, session.SeatIds[0]);
        var b = api.Viewer("viewer-b-" + Guid.NewGuid());

        var response = await b.BookAsync(session.Id, session.SeatIds[0], session.SeatIds[1]);

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        Assert.Equal("free", (await b.SeatStatesAsync(session.Id))[session.SeatIds[1]]);
    }

    [Fact]
    public async Task Blocked_seat_cannot_be_booked()
    {
        var session = await api.CreateSessionAsync();
        var staff = api.Staff("staff-" + Guid.NewGuid());
        await staff.PatchAsJsonAsync($"/staff/sessions/{session.Id}/seats/{session.SeatIds[0]}", new { available = false });

        var response = await api.Viewer("viewer-" + Guid.NewGuid()).BookAsync(session.Id, session.SeatIds[0]);

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        Assert.False(await api.WithDbAsync(db => db.Bookings.AnyAsync(x => x.SessionId == session.Id)));
    }

    [Fact]
    public async Task Cancelled_or_started_session_cannot_be_booked()
    {
        var cancelled = await api.CreateSessionAsync();
        await api.Staff("staff-" + Guid.NewGuid()).PatchAsJsonAsync($"/staff/sessions/{cancelled.Id}", new { cancelled = true });
        var started = await api.CreateSessionAsync(startsAt: DateTimeOffset.UtcNow.AddMinutes(-1));
        var viewer = api.Viewer("viewer-" + Guid.NewGuid());

        Assert.Equal(HttpStatusCode.Conflict, (await viewer.BookAsync(cancelled.Id, cancelled.SeatIds[0])).StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, (await viewer.BookAsync(started.Id, started.SeatIds[0])).StatusCode);
    }

    [Fact]
    public async Task Booking_racing_with_session_cancellation_is_not_created_after_cancellation()
    {
        var session = await api.CreateSessionAsync(seats: 30);
        var staff = api.Staff("staff-" + Guid.NewGuid());

        var start = new TaskCompletionSource();
        var bookings = session.SeatIds.Take(25).Select(async (seat, i) =>
        {
            await start.Task;
            return await api.Viewer($"viewer-{i}-{Guid.NewGuid()}").BookAsync(session.Id, seat);
        }).ToList();
        var cancel = Task.Run(async () =>
        {
            await start.Task;
            return await staff.PatchAsJsonAsync($"/staff/sessions/{session.Id}", new { cancelled = true });
        });
        start.SetResult();
        await Task.WhenAll(bookings);
        Assert.Equal(HttpStatusCode.NoContent, (await cancel).StatusCode);

        var cancelledAt = await api.WithDbAsync(db => db.Sessions.Where(s => s.Id == session.Id).Select(s => s.CancelledAt).SingleAsync());
        var createdAfter = await api.WithDbAsync(db =>
            db.Bookings.CountAsync(b => b.SessionId == session.Id && b.Status == BookingStatus.Active && b.CreatedAt > cancelledAt));
        Assert.Equal(0, createdAfter);
    }

    [Fact]
    public async Task Database_rejects_second_active_booking_of_same_seat()
    {
        var session = await api.CreateSessionAsync();
        await api.Viewer("viewer-a-" + Guid.NewGuid()).BookOkAsync(session.Id, session.SeatIds[0]);

        // Вставка в обход API и блокировки сеанса — срабатывает частичный уникальный индекс.
        var ex = await Assert.ThrowsAsync<DbUpdateException>(() => api.WithDbAsync(async db =>
        {
            db.Bookings.Add(new Booking
            {
                Id = Guid.NewGuid(),
                UserId = "direct-insert",
                SessionId = session.Id,
                CreatedAt = DateTimeOffset.UtcNow,
                Seats = [new BookingSeat { SessionId = session.Id, SeatId = session.SeatIds[0], IsActive = true }],
            });
            await db.SaveChangesAsync();
        }));
        Assert.Equal(PostgresErrorCodes.UniqueViolation, Assert.IsType<PostgresException>(ex.InnerException).SqlState);
    }

    [Fact]
    public async Task Cancelled_booking_frees_seat_for_new_booking()
    {
        var session = await api.CreateSessionAsync();
        var a = api.Viewer("viewer-a-" + Guid.NewGuid());
        var id = await a.BookOkAsync(session.Id, session.SeatIds[0]);
        await a.DeleteAsync($"/bookings/{id}");

        await api.Viewer("viewer-b-" + Guid.NewGuid()).BookOkAsync(session.Id, session.SeatIds[0]);
    }

    [Fact]
    public async Task Malformed_body_is_client_error()
    {
        var viewer = api.Viewer("viewer-" + Guid.NewGuid());
        var content = new ByteArrayContent([(byte)'{', (byte)'"', 0xCF, 0xF0, (byte)'"', (byte)':', (byte)'1', (byte)'}']);
        content.Headers.ContentType = new("application/json");

        Assert.Equal(HttpStatusCode.BadRequest, (await viewer.PostAsync("/bookings", content)).StatusCode);
    }

    [Fact]
    public async Task Duplicate_or_empty_seat_list_is_rejected()
    {
        var session = await api.CreateSessionAsync();
        var viewer = api.Viewer("viewer-" + Guid.NewGuid());

        Assert.Equal(HttpStatusCode.BadRequest, (await viewer.BookAsync(session.Id)).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await viewer.BookAsync(session.Id, session.SeatIds[0], session.SeatIds[0])).StatusCode);
    }
}
