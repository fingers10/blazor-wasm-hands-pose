using Microsoft.EntityFrameworkCore;
using Pgvector;
using Pgvector.EntityFrameworkCore;

var builder = WebApplication.CreateBuilder(args);

// Connection string injected by Aspire as ConnectionStrings__facemesh-db
var connectionString = builder.Configuration.GetConnectionString("facemesh-db")
    ?? throw new InvalidOperationException("Connection string 'facemesh-db' not found.");

builder.Services.AddDbContext<FaceMeshDbContext>(opts =>
    opts.UseNpgsql(connectionString, npgsql => npgsql.UseVector()));

builder.Services.AddCors(options =>
    options.AddDefaultPolicy(p => p.AllowAnyOrigin().AllowAnyMethod().AllowAnyHeader()));

var app = builder.Build();
app.UseCors();

using (var scope = app.Services.CreateScope())
{
    var db = scope.ServiceProvider.GetRequiredService<FaceMeshDbContext>();
    await db.Database.EnsureCreatedAsync();
}

// Recognition threshold: L2 distance in IOD-normalised landmark space.
// Within-person variation across frames is typically 0.3-0.8;
// between-person variation 1.5-4.0. Tune by testing with your subjects.
const double RecognitionThreshold = 1.2;

// ── Enroll a person ───────────────────────────────────────────────────────────
app.MapPost("/api/mesh/persons/enroll", async (MeshEnrollRequest req, FaceMeshDbContext db) =>
{
    if (string.IsNullOrWhiteSpace(req.Name) || req.Vector is not { Length: 956 })
        return Results.BadRequest("Name and a 956-element landmark vector are required.");

    var person = new MeshPersonEntity
    {
        Id          = Guid.NewGuid(),
        Name        = req.Name.Trim(),
        Descriptor  = new Vector(req.Vector),
        EnrolledAt  = DateTime.UtcNow
    };
    db.Persons.Add(person);
    await db.SaveChangesAsync();
    return Results.Created($"/api/mesh/persons/{person.Id}", new { person.Id, person.Name });
});

// ── List enrolled persons ─────────────────────────────────────────────────────
app.MapGet("/api/mesh/persons", async (FaceMeshDbContext db) =>
    await db.Persons
        .OrderBy(p => p.EnrolledAt)
        .Select(p => new { p.Id, p.Name, p.EnrolledAt })
        .ToListAsync());

// ── Check in via face mesh ────────────────────────────────────────────────────
app.MapPost("/api/mesh/attendance/checkin", async (MeshCheckInRequest req, FaceMeshDbContext db) =>
{
    if (req.Vector is not { Length: 956 })
        return Results.BadRequest("A 956-element landmark vector is required.");

    var vec = new Vector(req.Vector);

    // pgvector L2 nearest-neighbour pushed into Postgres via HNSW index
    var best = await db.Persons
        .Select(p => new { Person = p, Distance = p.Descriptor.L2Distance(vec) })
        .OrderBy(x => x.Distance)
        .FirstOrDefaultAsync();

    if (best == null || best.Distance > RecognitionThreshold)
        return Results.Ok(new MeshCheckInResponse(false, null, null,
            (float)(best?.Distance ?? double.MaxValue)));

    // Idempotent: one check-in per person per UTC day
    var dayStart = DateTime.UtcNow.Date;
    var dayEnd   = dayStart.AddDays(1);
    bool alreadyIn = await db.AttendanceRecords
        .AnyAsync(r => r.PersonId == best.Person.Id
                    && r.CheckedInAt >= dayStart
                    && r.CheckedInAt < dayEnd);

    if (!alreadyIn)
    {
        db.AttendanceRecords.Add(new MeshAttendanceRecordEntity
        {
            Id          = Guid.NewGuid(),
            PersonId    = best.Person.Id,
            PersonName  = best.Person.Name,
            CheckedInAt = DateTime.UtcNow
        });
        await db.SaveChangesAsync();
    }

    return Results.Ok(new MeshCheckInResponse(
        true, best.Person.Id, best.Person.Name, (float)best.Distance));
});

// ── Today's attendance log ────────────────────────────────────────────────────
app.MapGet("/api/mesh/attendance", async (FaceMeshDbContext db) =>
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
record MeshEnrollRequest(string Name, float[] Vector);
record MeshCheckInRequest(float[] Vector);
record MeshCheckInResponse(bool Recognized, Guid? PersonId, string? PersonName, float Distance);

// ── EF Core entities ──────────────────────────────────────────────────────────
public class MeshPersonEntity
{
    public Guid   Id          { get; set; }
    public string Name        { get; set; } = "";
    public Vector Descriptor  { get; set; } = null!;   // vector(956) in Postgres
    public DateTime EnrolledAt { get; set; }
    public ICollection<MeshAttendanceRecordEntity> AttendanceRecords { get; set; } = [];
}

public class MeshAttendanceRecordEntity
{
    public Guid   Id          { get; set; }
    public Guid   PersonId    { get; set; }
    public MeshPersonEntity Person { get; set; } = null!;
    public string PersonName  { get; set; } = "";
    public DateTime CheckedInAt { get; set; }
}

// ── DbContext ─────────────────────────────────────────────────────────────────
public class FaceMeshDbContext(DbContextOptions<FaceMeshDbContext> options) : DbContext(options)
{
    public DbSet<MeshPersonEntity> Persons => Set<MeshPersonEntity>();
    public DbSet<MeshAttendanceRecordEntity> AttendanceRecords => Set<MeshAttendanceRecordEntity>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.HasPostgresExtension("vector");

        modelBuilder.Entity<MeshPersonEntity>(e =>
        {
            e.ToTable("mesh_persons");
            e.Property(p => p.Descriptor).HasColumnType("vector(956)");
            // HNSW index for sub-millisecond nearest-neighbour search
            e.HasIndex(p => p.Descriptor)
             .HasMethod("hnsw")
             .HasOperators("vector_l2_ops");
        });

        modelBuilder.Entity<MeshAttendanceRecordEntity>(e =>
        {
            e.ToTable("mesh_attendance_records");
            e.HasOne(r => r.Person)
             .WithMany(p => p.AttendanceRecords)
             .HasForeignKey(r => r.PersonId);
        });
    }
}
