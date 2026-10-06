using System.Net;
using CinemaBooking.Api.Data;
using CinemaBooking.Tests.Infrastructure;
using Microsoft.EntityFrameworkCore;

namespace CinemaBooking.Tests;

/// <summary>Привилегированная отмена: SR-03, SR-07; T-08.</summary>
[Collection(ApiCollection.Name)]
public class StaffCancellationTests(ApiFactory api)
{
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public async Task Cancellation_without_reason_is_rejected(string? reason)
    {
        var session = await api.CreateSessionAsync();
        var id = await api.Viewer("viewer-" + Guid.NewGuid()).BookOkAsync(session.Id, session.SeatIds[0]);

        var response = await api.Staff("staff-" + Guid.NewGuid()).DeleteWithBodyAsync($"/staff/bookings/{id}", new { reason });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var status = await api.WithDbAsync(db => db.Bookings.Where(b => b.Id == id).Select(b => b.Status).SingleAsync());
        Assert.Equal(BookingStatus.Active, status);
    }

    [Fact]
    public async Task Concurrent_owner_and_staff_cancellation_succeeds_once_and_keeps_consistent_record()
    {
        for (var attempt = 0; attempt < 10; attempt++)
        {
            var session = await api.CreateSessionAsync();
            var ownerId = "viewer-" + Guid.NewGuid();
            var owner = api.Viewer(ownerId);
            var staffId = "staff-" + Guid.NewGuid();
            var id = await owner.BookOkAsync(session.Id, session.SeatIds[0]);

            var start = new TaskCompletionSource();
            var byOwner = Task.Run(async () => { await start.Task; return await owner.DeleteAsync($"/bookings/{id}"); });
            var byStaff = Task.Run(async () =>
            {
                await start.Task;
                return await api.Staff(staffId).DeleteWithBodyAsync($"/staff/bookings/{id}", new { reason = "Отмена сеанса" });
            });
            start.SetResult();
            var responses = await Task.WhenAll(byOwner, byStaff);

            // Ровно одна отмена; запись об отмене соответствует той операции, которая прошла.
            Assert.Equal(1, responses.Count(r => r.IsSuccessStatusCode));
            Assert.Equal(1, responses.Count(r => r.StatusCode == HttpStatusCode.Conflict));
            var booking = await api.WithDbAsync(db => db.Bookings.SingleAsync(b => b.Id == id));
            var staffWon = responses[1].IsSuccessStatusCode;
            Assert.Equal(staffWon ? staffId : ownerId, booking.CancelledBy);
            Assert.Equal(staffWon, booking.CancelledByStaff);
            Assert.Equal(staffWon ? "Отмена сеанса" : null, booking.CancelReason);
        }
    }

    [Fact]
    public async Task Cancellation_records_actor_time_and_reason_and_frees_seats()
    {
        var session = await api.CreateSessionAsync();
        var viewer = api.Viewer("viewer-" + Guid.NewGuid());
        var id = await viewer.BookOkAsync(session.Id, session.SeatIds[0], session.SeatIds[1]);
        var staffId = "staff-" + Guid.NewGuid();
        var before = DateTimeOffset.UtcNow;

        var response = await api.Staff(staffId).DeleteWithBodyAsync($"/staff/bookings/{id}", new { reason = "Техническая неисправность зала" });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var booking = await api.WithDbAsync(db => db.Bookings.SingleAsync(b => b.Id == id));
        Assert.Equal(BookingStatus.Cancelled, booking.Status);
        Assert.Equal(staffId, booking.CancelledBy);
        Assert.True(booking.CancelledByStaff);
        Assert.Equal("Техническая неисправность зала", booking.CancelReason);
        Assert.True(booking.CancelledAt >= before.AddSeconds(-1));

        var states = await viewer.SeatStatesAsync(session.Id);
        Assert.Equal("free", states[session.SeatIds[0]]);
        Assert.Equal("free", states[session.SeatIds[1]]);
    }
}
