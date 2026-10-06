using System.Net;
using CinemaBooking.Tests.Infrastructure;
using Microsoft.EntityFrameworkCore;

namespace CinemaBooking.Tests;

/// <summary>Будущая проверка D-03 (SR-08; T-06). Лимит в тестах — 6 мест.</summary>
[Collection(ApiCollection.Name)]
public class BookingLimitTests(ApiFactory api)
{
    [Fact]
    public async Task Single_booking_over_limit_is_rejected()
    {
        var session = await api.CreateSessionAsync();
        var viewer = api.Viewer("viewer-" + Guid.NewGuid());

        var response = await viewer.BookAsync(session.Id, session.SeatIds.Take(7).ToArray());

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        Assert.All(await viewer.SeatStatesAsync(session.Id), s => Assert.Equal("free", s.Value));
    }

    [Fact]
    public async Task Parallel_bookings_do_not_exceed_limit()
    {
        // Сценарий из D-03: два параллельных запроса по 4 места.
        var session = await api.CreateSessionAsync();
        var userId = "viewer-" + Guid.NewGuid();
        var responses = await ParallelAsync(api.Viewer(userId), session.Id, session.SeatIds[..4], session.SeatIds[4..8]);

        Assert.Equal(1, responses.Count(r => r.StatusCode == HttpStatusCode.Created));
        Assert.Equal(1, responses.Count(r => r.StatusCode == HttpStatusCode.UnprocessableEntity));
        Assert.Equal(4, await ActiveSeatsAsync(session.Id, userId));
    }

    [Fact]
    public async Task Many_parallel_single_seat_bookings_stop_exactly_at_limit()
    {
        // Более широкое окно гонки: без блокировки сеанса проходит больше 6 запросов.
        var session = await api.CreateSessionAsync(seats: 12);
        var userId = "viewer-" + Guid.NewGuid();
        var responses = await ParallelAsync(api.Viewer(userId), session.Id, session.SeatIds.Select(s => new[] { s }).ToArray());

        Assert.Equal(6, responses.Count(r => r.StatusCode == HttpStatusCode.Created));
        Assert.Equal(6, responses.Count(r => r.StatusCode == HttpStatusCode.UnprocessableEntity));
        Assert.Equal(6, await ActiveSeatsAsync(session.Id, userId));
    }

    private static async Task<HttpResponseMessage[]> ParallelAsync(HttpClient client, int sessionId, params int[][] seatGroups)
    {
        var start = new TaskCompletionSource();
        var tasks = seatGroups.Select(seats => Task.Run(async () =>
        {
            await start.Task;
            return await client.BookAsync(sessionId, seats);
        })).ToList();
        start.SetResult();
        return await Task.WhenAll(tasks);
    }

    private Task<int> ActiveSeatsAsync(int sessionId, string userId) =>
        api.WithDbAsync(db => db.BookingSeats.CountAsync(s =>
            s.SessionId == sessionId && s.IsActive && db.Bookings.Any(b => b.Id == s.BookingId && b.UserId == userId)));

    [Fact]
    public async Task Cancelled_bookings_do_not_count_towards_limit()
    {
        var session = await api.CreateSessionAsync();
        var viewer = api.Viewer("viewer-" + Guid.NewGuid());

        var first = await viewer.BookOkAsync(session.Id, session.SeatIds[..3]);
        await viewer.BookOkAsync(session.Id, session.SeatIds[3..6]);
        Assert.Equal(HttpStatusCode.UnprocessableEntity, (await viewer.BookAsync(session.Id, session.SeatIds[6])).StatusCode);

        await viewer.DeleteAsync($"/bookings/{first}");
        await viewer.BookOkAsync(session.Id, session.SeatIds[6..9]);
    }

    [Fact]
    public async Task Limit_is_per_session()
    {
        var s1 = await api.CreateSessionAsync();
        var s2 = await api.CreateSessionAsync();
        var viewer = api.Viewer("viewer-" + Guid.NewGuid());

        await viewer.BookOkAsync(s1.Id, s1.SeatIds[..6]);
        await viewer.BookOkAsync(s2.Id, s2.SeatIds[..6]);
    }
}
