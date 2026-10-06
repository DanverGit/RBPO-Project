using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace CinemaBooking.Tests.Infrastructure;

public static class ApiClientExtensions
{
    public static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter() },
    };

    public static Task<HttpResponseMessage> BookAsync(this HttpClient client, int sessionId, params int[] seatIds) =>
        client.PostAsJsonAsync("/bookings", new { sessionId, seatIds });

    public static async Task<Guid> BookOkAsync(this HttpClient client, int sessionId, params int[] seatIds)
    {
        var response = await client.BookAsync(sessionId, seatIds);
        Assert.Equal(System.Net.HttpStatusCode.Created, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>(Json);
        return body.GetProperty("id").GetGuid();
    }

    public static Task<HttpResponseMessage> DeleteWithBodyAsync(this HttpClient client, string url, object body) =>
        client.SendAsync(new HttpRequestMessage(HttpMethod.Delete, url) { Content = JsonContent.Create(body) });

    public static async Task<Dictionary<int, string>> SeatStatesAsync(this HttpClient client, int sessionId)
    {
        var seats = await client.GetFromJsonAsync<JsonElement>($"/sessions/{sessionId}/seats", Json);
        return seats.EnumerateArray().ToDictionary(
            s => s.GetProperty("seatId").GetInt32(),
            s => s.GetProperty("state").GetString()!);
    }
}
