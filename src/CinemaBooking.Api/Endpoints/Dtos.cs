using CinemaBooking.Api.Bookings;
using CinemaBooking.Api.Data;

namespace CinemaBooking.Api.Endpoints;

// Модели запросов не содержат владельца, роли и статуса (D-01): лишние поля тела игнорируются при привязке.
public sealed record CreateBookingRequest(int SessionId, int[] SeatIds);
public sealed record StaffCancelRequest(string? Reason);
public sealed record CreateSessionRequest(int MovieId, int HallId, DateTimeOffset StartsAt);
public sealed record UpdateSessionRequest(DateTimeOffset? StartsAt, bool? Cancelled);
public sealed record SeatAvailabilityRequest(bool Available);

public sealed record MovieDto(int Id, string Title, int DurationMinutes);
public sealed record SessionDto(int Id, int MovieId, string MovieTitle, int HallId, string HallName,
    DateTimeOffset StartsAt, SessionStatus Status, bool BookingOpen);

// Общедоступный ответ: только занятость места, без сведений о владельце (SR-01, T-10).
public sealed record SeatStateDto(int SeatId, int Row, int Number, string State);

public sealed record BookingDto(Guid Id, int SessionId, BookingStatus Status, DateTimeOffset CreatedAt,
    int[] SeatIds, DateTimeOffset? CancelledAt, bool CancelledByStaff, string? CancelReason)
{
    public static BookingDto From(Booking b) => new(b.Id, b.SessionId, b.Status, b.CreatedAt,
        b.Seats.Select(s => s.SeatId).Order().ToArray(), b.CancelledAt, b.CancelledByStaff, b.CancelReason);
}

// Представление для сотрудника: включает владельца и исполнителя отмены (SR-07).
public sealed record StaffBookingDto(Guid Id, string UserId, int SessionId, BookingStatus Status, DateTimeOffset CreatedAt,
    int[] SeatIds, DateTimeOffset? CancelledAt, string? CancelledBy, bool CancelledByStaff, string? CancelReason)
{
    public static StaffBookingDto From(Booking b) => new(b.Id, b.UserId, b.SessionId, b.Status, b.CreatedAt,
        b.Seats.Select(s => s.SeatId).Order().ToArray(), b.CancelledAt, b.CancelledBy, b.CancelledByStaff, b.CancelReason);
}

public static class ResultMapping
{
    public static IResult ToProblem(this BookingResult result) => result.Error switch
    {
        BookingError.InvalidRequest or BookingError.SeatNotInHall or BookingError.ReasonRequired =>
            Results.Problem(result.Message, statusCode: StatusCodes.Status400BadRequest),
        BookingError.NotFound =>
            Results.Problem(result.Message, statusCode: StatusCodes.Status404NotFound),
        BookingError.SessionClosed or BookingError.SeatUnavailable or BookingError.SeatAlreadyBooked or BookingError.AlreadyCancelled =>
            Results.Problem(result.Message, statusCode: StatusCodes.Status409Conflict),
        BookingError.LimitExceeded =>
            Results.Problem(result.Message, statusCode: StatusCodes.Status422UnprocessableEntity),
        _ => throw new InvalidOperationException($"Нет HTTP-ответа для {result.Error}"),
    };
}
