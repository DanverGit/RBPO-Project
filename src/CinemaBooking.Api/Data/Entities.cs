namespace CinemaBooking.Api.Data;

public class Movie
{
    public int Id { get; set; }
    public required string Title { get; set; }
    public int DurationMinutes { get; set; }
}

public class Hall
{
    public int Id { get; set; }
    public required string Name { get; set; }
    public List<Seat> Seats { get; set; } = [];
}

public class Seat
{
    public int Id { get; set; }
    public int HallId { get; set; }
    public int Row { get; set; }
    public int Number { get; set; }
}

public enum SessionStatus
{
    Scheduled = 0,
    Cancelled = 1,
}

public class Session
{
    public int Id { get; set; }
    public int MovieId { get; set; }
    public Movie? Movie { get; set; }
    public int HallId { get; set; }
    public Hall? Hall { get; set; }
    public DateTimeOffset StartsAt { get; set; }
    public SessionStatus Status { get; set; }
    public DateTimeOffset? CancelledAt { get; set; }
}

/// <summary>Место, сделанное сотрудником недоступным для новых бронирований на конкретном сеансе.</summary>
public class BlockedSeat
{
    public int SessionId { get; set; }
    public int SeatId { get; set; }
}

public enum BookingStatus
{
    Active = 0,
    Cancelled = 1,
}

public class Booking
{
    public Guid Id { get; set; }

    /// <summary>Владелец — claim <c>sub</c> из проверенного токена (D-01), никогда не из тела запроса.</summary>
    public required string UserId { get; set; }

    public int SessionId { get; set; }
    public BookingStatus Status { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public List<BookingSeat> Seats { get; set; } = [];

    // Сведения об отмене (SR-07). Для привилегированной отмены заполняются все поля.
    public DateTimeOffset? CancelledAt { get; set; }
    public string? CancelledBy { get; set; }
    public bool CancelledByStaff { get; set; }
    public string? CancelReason { get; set; }
}

/// <summary>
/// Место в бронировании. <see cref="SessionId"/> и <see cref="IsActive"/> денормализованы,
/// чтобы частичный уникальный индекс (session_id, seat_id) WHERE is_active запрещал
/// два активных бронирования одного места (D-02).
/// </summary>
public class BookingSeat
{
    public Guid BookingId { get; set; }
    public int SessionId { get; set; }
    public int SeatId { get; set; }
    public bool IsActive { get; set; }
}
