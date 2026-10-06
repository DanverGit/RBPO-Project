using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using CinemaBooking.Api.Auth;
using CinemaBooking.Api.Data;
using CinemaBooking.Api.Endpoints;
using CinemaBooking.Tests.Infrastructure;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace CinemaBooking.Tests;

/// <summary>Будущая проверка D-01 (SR-01, SR-02, SR-03, SR-09; T-01, T-02, T-03, T-09, T-10).</summary>
[Collection(ApiCollection.Name)]
public class AuthorizationTests(ApiFactory api)
{
    [Fact]
    public async Task Viewer_cannot_read_or_cancel_foreign_booking()
    {
        var session = await api.CreateSessionAsync();
        var a = api.Viewer("viewer-a-" + Guid.NewGuid());
        var b = api.Viewer("viewer-b-" + Guid.NewGuid());
        var bookingId = await a.BookOkAsync(session.Id, session.SeatIds[0]);

        Assert.Equal(HttpStatusCode.NotFound, (await b.GetAsync($"/bookings/{bookingId}")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await b.DeleteAsync($"/bookings/{bookingId}")).StatusCode);

        var stored = await api.WithDbAsync(db => db.Bookings.Include(x => x.Seats).SingleAsync(x => x.Id == bookingId));
        Assert.Equal(BookingStatus.Active, stored.Status);
        Assert.All(stored.Seats, s => Assert.True(s.IsActive));

        // Позитивный случай: владелец видит и отменяет своё бронирование.
        Assert.Equal(HttpStatusCode.OK, (await a.GetAsync($"/bookings/{bookingId}")).StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, (await a.DeleteAsync($"/bookings/{bookingId}")).StatusCode);
        Assert.Equal("free", (await a.SeatStatesAsync(session.Id))[session.SeatIds[0]]);
    }

    [Fact]
    public async Task Owner_from_request_body_is_ignored()
    {
        var session = await api.CreateSessionAsync();
        var victim = "viewer-a-" + Guid.NewGuid();
        var attacker = "viewer-b-" + Guid.NewGuid();

        var response = await api.Viewer(attacker).PostAsJsonAsync("/bookings", new
        {
            sessionId = session.Id,
            seatIds = new[] { session.SeatIds[0] },
            userId = victim,
            ownerId = victim,
        });

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var id = (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetGuid();
        var owner = await api.WithDbAsync(db => db.Bookings.Where(x => x.Id == id).Select(x => x.UserId).SingleAsync());
        Assert.Equal(attacker, owner);
    }

    [Fact]
    public async Task Viewer_gets_403_on_every_staff_endpoint()
    {
        var viewer = api.Viewer("viewer-" + Guid.NewGuid());
        var endpoints = api.Services.GetRequiredService<EndpointDataSource>().Endpoints
            .OfType<RouteEndpoint>()
            .Where(e => e.RoutePattern.RawText!.StartsWith(StaffEndpoints.Prefix))
            .ToList();
        Assert.NotEmpty(endpoints);

        foreach (var endpoint in endpoints)
        {
            var url = endpoint.RoutePattern.RawText!
                .Replace("{id:int}", "1")
                .Replace("{id:guid}", Guid.NewGuid().ToString())
                .Replace("{seatId:int}", "1");
            foreach (var method in endpoint.Metadata.GetMetadata<HttpMethodMetadata>()!.HttpMethods)
            {
                var request = new HttpRequestMessage(new HttpMethod(method), url)
                {
                    Content = method is "GET" ? null : new StringContent("{}", Encoding.UTF8, "application/json"),
                };
                var response = await viewer.SendAsync(request);
                Assert.True(response.StatusCode == HttpStatusCode.Forbidden, $"{method} {url} вернул {(int)response.StatusCode}");
            }
        }
    }

    [Fact]
    public async Task Viewer_cannot_change_session_or_seat_and_state_is_kept()
    {
        var session = await api.CreateSessionAsync();
        var viewer = api.Viewer("viewer-" + Guid.NewGuid());

        var cancel = await viewer.PatchAsJsonAsync($"/staff/sessions/{session.Id}", new { cancelled = true });
        var block = await viewer.PatchAsJsonAsync($"/staff/sessions/{session.Id}/seats/{session.SeatIds[0]}", new { available = false });

        Assert.Equal(HttpStatusCode.Forbidden, cancel.StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, block.StatusCode);
        var status = await api.WithDbAsync(db => db.Sessions.Where(s => s.Id == session.Id).Select(s => s.Status).SingleAsync());
        Assert.Equal(SessionStatus.Scheduled, status);
        Assert.False(await api.WithDbAsync(db => db.BlockedSeats.AnyAsync(b => b.SessionId == session.Id)));

        // Позитивный случай: сотрудник выполняет то же действие.
        var staff = api.Staff("staff-" + Guid.NewGuid());
        Assert.Equal(HttpStatusCode.NoContent,
            (await staff.PatchAsJsonAsync($"/staff/sessions/{session.Id}/seats/{session.SeatIds[0]}", new { available = false })).StatusCode);
        Assert.Equal("blocked", (await viewer.SeatStatesAsync(session.Id))[session.SeatIds[0]]);
    }

    [Fact]
    public async Task Staff_cannot_use_viewer_booking_endpoints()
    {
        var session = await api.CreateSessionAsync();
        var response = await api.Staff("staff-" + Guid.NewGuid()).BookAsync(session.Id, session.SeatIds[0]);
        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    public static TheoryData<string> InvalidTokens()
    {
        var valid = TestAuth.CreateToken("viewer-x", [Roles.Viewer]);
        var parts = valid.Split('.');
        // Роль заменена на staff в payload, подпись осталась от исходного токена.
        var payload = Encoding.UTF8.GetString(Base64UrlDecode(parts[1])).Replace("\"viewer\"", "\"staff\"");
        var tampered = $"{parts[0]}.{Base64UrlEncode(Encoding.UTF8.GetBytes(payload))}.{parts[2]}";

        return new TheoryData<string>
        {
            tampered,
            TestAuth.CreateToken("viewer-x", [Roles.Staff], key: TestAuth.ForeignKey),
            TestAuth.CreateToken("viewer-x", [Roles.Viewer], expires: DateTime.UtcNow.AddMinutes(-5)),
            TestAuth.CreateToken("viewer-x", [Roles.Viewer], audience: "other-client"),
        };
    }

    [Theory]
    [MemberData(nameof(InvalidTokens))]
    public async Task Invalid_tokens_are_rejected_without_state_change(string token)
    {
        var session = await api.CreateSessionAsync();
        var client = api.CreateClient().WithToken(token);

        Assert.Equal(HttpStatusCode.Unauthorized, (await client.BookAsync(session.Id, session.SeatIds[0])).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized,
            (await client.PatchAsJsonAsync($"/staff/sessions/{session.Id}", new { cancelled = true })).StatusCode);
        Assert.False(await api.WithDbAsync(db => db.Bookings.AnyAsync(b => b.SessionId == session.Id)));
    }

    [Fact]
    public async Task Requests_without_token_are_rejected_except_public_ones()
    {
        var anonymous = api.Anonymous();
        Assert.Equal(HttpStatusCode.Unauthorized, (await anonymous.GetAsync("/bookings/my")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await anonymous.GetAsync("/staff/bookings")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await anonymous.GetAsync("/health")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await anonymous.GetAsync("/movies")).StatusCode);
    }

    [Fact]
    public async Task Seat_list_does_not_expose_booking_owner()
    {
        var session = await api.CreateSessionAsync();
        var userId = "viewer-secret-" + Guid.NewGuid();
        await api.Viewer(userId).BookOkAsync(session.Id, session.SeatIds[0]);

        var raw = await api.Anonymous().GetStringAsync($"/sessions/{session.Id}/seats");

        Assert.DoesNotContain(userId, raw);
        Assert.DoesNotContain("userId", raw, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("booked", raw);
    }

    private static byte[] Base64UrlDecode(string s)
    {
        s = s.Replace('-', '+').Replace('_', '/');
        return Convert.FromBase64String(s.PadRight(s.Length + (4 - s.Length % 4) % 4, '='));
    }

    private static string Base64UrlEncode(byte[] bytes) =>
        Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');
}
