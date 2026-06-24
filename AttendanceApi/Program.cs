using Microsoft.EntityFrameworkCore;
using Pgvector;
using Pgvector.EntityFrameworkCore;

var builder = WebApplication.CreateBuilder(args);

// Connection string is injected by Aspire as ConnectionStrings__attendance-db
var connectionString = builder.Configuration.GetConnectionString("attendance-db")
    ?? throw new InvalidOperationException("Connection string 'attendance-db' not found.");

builder.Services.AddDbContext<AttendanceDbContext>(opts =>
    opts.UseNpgsql(connectionString, npgsql => npgsql.UseVector()));

builder.Services.AddCors(options =>
    options.AddDefaultPolicy(p => p.AllowAnyOrigin().AllowAnyMethod().AllowAnyHeader()));

var app = builder.Build();

app.UseCors();

// Ensure the database schema and pgvector extension exist on startup
using (var scope = app.Services.CreateScope())
{
    var db = scope.ServiceProvider.GetRequiredService<AttendanceDbContext>();
    await db.Database.EnsureCreatedAsync();
}

// ── Enroll a person ──────────────────────────────────────────────────────────
app.MapPost("/api/persons/enroll", async (EnrollRequest req, AttendanceDbContext db) =>
{
    if (string.IsNullOrWhiteSpace(req.Name) || req.Descriptor is not { Length: 128 })
        return Results.BadRequest("Name and a 128-element descriptor are required.");

    var person = new PersonEntity
    {
        Id = Guid.NewGuid(),
        Name = req.Name.Trim(),
        Descriptor = new Vector(req.Descriptor),
        EnrolledAt = DateTime.UtcNow
    };
    db.Persons.Add(person);
    await db.SaveChangesAsync();
    return Results.Created($"/api/persons/{person.Id}", new { person.Id, person.Name });
});

// ── List enrolled persons ────────────────────────────────────────────────────
app.MapGet("/api/persons", async (AttendanceDbContext db) =>
    await db.Persons
        .OrderBy(p => p.EnrolledAt)
        .Select(p => new { p.Id, p.Name, p.EnrolledAt })
        .ToListAsync());

// ── Check in (face recognition) ──────────────────────────────────────────────
app.MapPost("/api/attendance/checkin", async (CheckInRequest req, AttendanceDbContext db) =>
{
    if (req.Descriptor is not { Length: 128 })
        return Results.BadRequest("A 128-element descriptor is required.");

    const double Threshold = 0.6;
    var vec = new Vector(req.Descriptor);

    // pgvector L2 distance (<->) — nearest-neighbour search pushed into Postgres
    var best = await db.Persons
        .Select(p => new { Person = p, Distance = p.Descriptor.L2Distance(vec) })
        .OrderBy(x => x.Distance)
        .FirstOrDefaultAsync();

    if (best == null || best.Distance > Threshold)
        return Results.Ok(new CheckInResponse(false, null, null, (float)(best?.Distance ?? double.MaxValue)));

    // Idempotent: only mark once per person per calendar day (UTC)
    var dayStart = DateTime.UtcNow.Date;
    var dayEnd   = dayStart.AddDays(1);
    bool alreadyCheckedIn = await db.AttendanceRecords
        .AnyAsync(r => r.PersonId == best.Person.Id
                    && r.CheckedInAt >= dayStart
                    && r.CheckedInAt < dayEnd);

    if (!alreadyCheckedIn)
    {
        db.AttendanceRecords.Add(new AttendanceRecordEntity
        {
            Id = Guid.NewGuid(),
            PersonId = best.Person.Id,
            PersonName = best.Person.Name,
            CheckedInAt = DateTime.UtcNow
        });
        await db.SaveChangesAsync();
    }

    return Results.Ok(new CheckInResponse(true, best.Person.Id, best.Person.Name, (float)best.Distance));
});

// ── Today's attendance log ───────────────────────────────────────────────────
app.MapGet("/api/attendance", async (AttendanceDbContext db) =>
{
    var dayStart = DateTime.UtcNow.Date;
    var dayEnd   = dayStart.AddDays(1);
    return await db.AttendanceRecords
        .Where(r => r.CheckedInAt >= dayStart && r.CheckedInAt < dayEnd)
        .OrderBy(r => r.CheckedInAt)
        .Select(r => new { r.Id, r.PersonId, r.PersonName, r.CheckedInAt })
        .ToListAsync();
});

app.Run();

// ── Request / response records ────────────────────────────────────────────────
record EnrollRequest(string Name, float[] Descriptor);
record CheckInRequest(float[] Descriptor);
record CheckInResponse(bool Recognized, Guid? PersonId, string? PersonName, float Distance);

// ── EF Core entities ──────────────────────────────────────────────────────────
public class PersonEntity
{
    public Guid Id { get; set; }
    public string Name { get; set; } = "";
    public Vector Descriptor { get; set; } = null!;   // vector(128) in Postgres
    public DateTime EnrolledAt { get; set; }
    public ICollection<AttendanceRecordEntity> AttendanceRecords { get; set; } = [];
}

public class AttendanceRecordEntity
{
    public Guid Id { get; set; }
    public Guid PersonId { get; set; }
    public PersonEntity Person { get; set; } = null!;
    public string PersonName { get; set; } = "";      // denormalised for fast reads
    public DateTime CheckedInAt { get; set; }
}

// ── DbContext ─────────────────────────────────────────────────────────────────
public class AttendanceDbContext(DbContextOptions<AttendanceDbContext> options) : DbContext(options)
{
    public DbSet<PersonEntity> Persons => Set<PersonEntity>();
    public DbSet<AttendanceRecordEntity> AttendanceRecords => Set<AttendanceRecordEntity>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        // Enable the pgvector extension (idempotent)
        modelBuilder.HasPostgresExtension("vector");

        modelBuilder.Entity<PersonEntity>(e =>
        {
            e.ToTable("persons");
            e.Property(p => p.Descriptor).HasColumnType("vector(128)");
            // HNSW index for sub-millisecond nearest-neighbour at scale
            e.HasIndex(p => p.Descriptor)
             .HasMethod("hnsw")
             .HasOperators("vector_l2_ops");
        });

        modelBuilder.Entity<AttendanceRecordEntity>(e =>
        {
            e.ToTable("attendance_records");
            e.HasOne(r => r.Person)
             .WithMany(p => p.AttendanceRecords)
             .HasForeignKey(r => r.PersonId);
        });
    }
}

