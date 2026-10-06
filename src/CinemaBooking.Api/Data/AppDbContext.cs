using System.Text;
using Microsoft.EntityFrameworkCore;

namespace CinemaBooking.Api.Data;

public class AppDbContext(DbContextOptions<AppDbContext> options) : DbContext(options)
{
    public DbSet<Movie> Movies => Set<Movie>();
    public DbSet<Hall> Halls => Set<Hall>();
    public DbSet<Seat> Seats => Set<Seat>();
    public DbSet<Session> Sessions => Set<Session>();
    public DbSet<BlockedSeat> BlockedSeats => Set<BlockedSeat>();
    public DbSet<Booking> Bookings => Set<Booking>();
    public DbSet<BookingSeat> BookingSeats => Set<BookingSeat>();

    protected override void OnModelCreating(ModelBuilder b)
    {
        b.Entity<Seat>().HasIndex(s => new { s.HallId, s.Row, s.Number }).IsUnique();
        b.Entity<Seat>().HasOne<Hall>().WithMany(h => h.Seats).HasForeignKey(s => s.HallId);

        b.Entity<BlockedSeat>().HasKey(x => new { x.SessionId, x.SeatId });
        b.Entity<BlockedSeat>().HasOne<Session>().WithMany().HasForeignKey(x => x.SessionId);
        b.Entity<BlockedSeat>().HasOne<Seat>().WithMany().HasForeignKey(x => x.SeatId);

        b.Entity<Booking>().Property(x => x.UserId).HasMaxLength(64);
        b.Entity<Booking>().Property(x => x.CancelledBy).HasMaxLength(64);
        b.Entity<Booking>().Property(x => x.CancelReason).HasMaxLength(500);
        b.Entity<Booking>().HasIndex(x => new { x.SessionId, x.UserId });
        b.Entity<Booking>().HasOne<Session>().WithMany().HasForeignKey(x => x.SessionId);

        b.Entity<BookingSeat>().HasKey(x => new { x.BookingId, x.SeatId });
        b.Entity<BookingSeat>().HasOne<Booking>().WithMany(x => x.Seats).HasForeignKey(x => x.BookingId);
        b.Entity<BookingSeat>().HasOne<Seat>().WithMany().HasForeignKey(x => x.SeatId);
        // D-02: второй барьер — одно место на сеансе не может иметь двух активных бронирований.
        b.Entity<BookingSeat>()
            .HasIndex(x => new { x.SessionId, x.SeatId })
            .IsUnique()
            .HasFilter("is_active")
            .HasDatabaseName("ux_booking_seats_active_seat");

        foreach (var entity in b.Model.GetEntityTypes())
        {
            entity.SetTableName(ToSnakeCase(entity.GetTableName()!));
            foreach (var property in entity.GetProperties())
                property.SetColumnName(ToSnakeCase(property.Name));
        }
    }

    private static string ToSnakeCase(string name)
    {
        var sb = new StringBuilder();
        for (var i = 0; i < name.Length; i++)
        {
            if (char.IsUpper(name[i]) && i > 0) sb.Append('_');
            sb.Append(char.ToLowerInvariant(name[i]));
        }
        return sb.ToString();
    }
}
