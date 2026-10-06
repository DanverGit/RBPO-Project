using CinemaBooking.Api.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Npgsql;

namespace CinemaBooking.Api.Bookings;

public sealed class BookingOptions
{
    public int MaxSeatsPerBooking { get; set; } = 6;
    public int MaxSeatsPerUserPerSession { get; set; } = 6;
}

public enum BookingError
{
    None,
    InvalidRequest,
    NotFound,
    SessionClosed,
    SeatNotInHall,
    SeatUnavailable,
    SeatAlreadyBooked,
    LimitExceeded,
    AlreadyCancelled,
    ReasonRequired,
}

public sealed record BookingResult(Booking? Booking, BookingError Error, string? Message)
{
    public static BookingResult Ok(Booking booking) => new(booking, BookingError.None, null);
    public static BookingResult Fail(BookingError error, string message) => new(null, error, message);
}

public static class SessionLock
{
    /// <summary>
    /// D-02: блокирует строку сеанса до конца текущей транзакции. Все операции, меняющие сеанс,
    /// его места и бронирования, берут эту блокировку первой, поэтому выполняются по очереди.
    /// </summary>
    public static async Task<Session?> LockAsync(AppDbContext db, int sessionId, CancellationToken ct)
    {
        if (db.Database.CurrentTransaction is null)
            throw new InvalidOperationException("Блокировка сеанса имеет смысл только внутри транзакции");

        var rows = await db.Sessions
            .FromSql($"SELECT * FROM sessions WHERE id = {sessionId} FOR UPDATE")
            .ToListAsync(ct);
        return rows.SingleOrDefault();
    }
}

public sealed class BookingService(AppDbContext db, IOptions<BookingOptions> options, TimeProvider time)
{
    private readonly BookingOptions _options = options.Value;

    /// <summary>D-02, D-03: проверки сеанса, мест и лимита выполняются в одной транзакции под блокировкой сеанса.</summary>
    public async Task<BookingResult> CreateAsync(string userId, int sessionId, IReadOnlyCollection<int> seatIds, CancellationToken ct)
    {
        if (seatIds.Count == 0 || seatIds.Distinct().Count() != seatIds.Count)
            return BookingResult.Fail(BookingError.InvalidRequest, "Список мест пуст или содержит повторы.");
        if (seatIds.Count > _options.MaxSeatsPerBooking)
            return BookingResult.Fail(BookingError.LimitExceeded,
                $"В одном бронировании не больше {_options.MaxSeatsPerBooking} мест.");

        await using var tx = await db.Database.BeginTransactionAsync(ct);

        var session = await SessionLock.LockAsync(db, sessionId, ct);
        if (session is null)
            return BookingResult.Fail(BookingError.NotFound, "Сеанс не найден.");
        if (!IsOpenForBooking(session))
            return BookingResult.Fail(BookingError.SessionClosed, "Бронирование на сеанс закрыто.");

        var seatsInHall = await db.Seats.CountAsync(s => s.HallId == session.HallId && seatIds.Contains(s.Id), ct);
        if (seatsInHall != seatIds.Count)
            return BookingResult.Fail(BookingError.SeatNotInHall, "Часть мест не относится к залу сеанса.");

        if (await db.BlockedSeats.AnyAsync(b => b.SessionId == sessionId && seatIds.Contains(b.SeatId), ct))
            return BookingResult.Fail(BookingError.SeatUnavailable, "Часть мест недоступна для бронирования.");

        if (await db.BookingSeats.AnyAsync(b => b.SessionId == sessionId && b.IsActive && seatIds.Contains(b.SeatId), ct))
            return BookingResult.Fail(BookingError.SeatAlreadyBooked, "Часть мест уже забронирована.");

        // D-03: подсчёт под блокировкой сеанса видит все завершённые бронирования этого сеанса.
        var userActiveSeats = await db.BookingSeats.CountAsync(bs =>
            bs.SessionId == sessionId && bs.IsActive &&
            db.Bookings.Any(b => b.Id == bs.BookingId && b.UserId == userId), ct);
        if (userActiveSeats + seatIds.Count > _options.MaxSeatsPerUserPerSession)
            return BookingResult.Fail(BookingError.LimitExceeded,
                $"На один сеанс можно забронировать не больше {_options.MaxSeatsPerUserPerSession} мест.");

        var booking = new Booking
        {
            Id = Guid.CreateVersion7(),
            UserId = userId,
            SessionId = sessionId,
            Status = BookingStatus.Active,
            CreatedAt = time.GetUtcNow(),
            Seats = seatIds.Select(id => new BookingSeat { SessionId = sessionId, SeatId = id, IsActive = true }).ToList(),
        };
        db.Bookings.Add(booking);

        try
        {
            await db.SaveChangesAsync(ct);
            await tx.CommitAsync(ct);
        }
        catch (DbUpdateException ex) when (ex.InnerException is PostgresException { SqlState: PostgresErrorCodes.UniqueViolation })
        {
            // Уникальный индекс сработал: какая-то операция обошла блокировку сеанса.
            return BookingResult.Fail(BookingError.SeatAlreadyBooked, "Часть мест уже забронирована.");
        }

        return BookingResult.Ok(booking);
    }

    /// <summary>D-01: бронирование выбирается сразу по id и владельцу; чужое неотличимо от несуществующего.</summary>
    public Task<Booking?> FindOwnAsync(string userId, Guid bookingId, CancellationToken ct) =>
        db.Bookings.Include(b => b.Seats)
            .SingleOrDefaultAsync(b => b.Id == bookingId && b.UserId == userId, ct);

    public Task<BookingResult> CancelOwnAsync(string userId, Guid bookingId, CancellationToken ct) =>
        CancelAsync(b => b.Id == bookingId && b.UserId == userId, cancelledBy: userId, byStaff: false, reason: null, ct);

    /// <summary>SR-03, SR-07: отмена сотрудником требует причины; исполнитель и время берутся на сервере.</summary>
    public Task<BookingResult> CancelByStaffAsync(string staffId, Guid bookingId, string? reason, CancellationToken ct)
    {
        reason = reason?.Trim();
        if (string.IsNullOrEmpty(reason) || reason.Length > 500)
            return Task.FromResult(BookingResult.Fail(BookingError.ReasonRequired, "Укажите причину отмены (до 500 символов)."));

        return CancelAsync(b => b.Id == bookingId, cancelledBy: staffId, byStaff: true, reason, ct);
    }

    /// <summary>
    /// D-02: отмена тоже берёт блокировку сеанса и проверяет статус уже под ней. Иначе одновременные
    /// отмены владельцем и сотрудником обе проходят, и вторая перезаписывает сведения об отмене (SR-07).
    /// </summary>
    private async Task<BookingResult> CancelAsync(System.Linq.Expressions.Expression<Func<Booking, bool>> filter,
        string cancelledBy, bool byStaff, string? reason, CancellationToken ct)
    {
        await using var tx = await db.Database.BeginTransactionAsync(ct);

        var sessionId = await db.Bookings.Where(filter).Select(b => (int?)b.SessionId).SingleOrDefaultAsync(ct);
        if (sessionId is null)
            return BookingResult.Fail(BookingError.NotFound, "Бронирование не найдено.");
        await SessionLock.LockAsync(db, sessionId.Value, ct);

        var booking = await db.Bookings.Include(b => b.Seats).SingleAsync(filter, ct);
        if (booking.Status != BookingStatus.Active)
            return BookingResult.Fail(BookingError.AlreadyCancelled, "Бронирование уже отменено.");

        booking.Status = BookingStatus.Cancelled;
        booking.CancelledAt = time.GetUtcNow();
        booking.CancelledBy = cancelledBy;
        booking.CancelledByStaff = byStaff;
        booking.CancelReason = reason;
        foreach (var seat in booking.Seats)
            seat.IsActive = false;

        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);
        return BookingResult.Ok(booking);
    }

    public bool IsOpenForBooking(Session session) =>
        session.Status == SessionStatus.Scheduled && session.StartsAt > time.GetUtcNow();
}
