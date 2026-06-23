using System.Text.Json;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddSingleton<AttendanceStore>();
builder.Services.AddCors(options =>
    options.AddDefaultPolicy(p => p.AllowAnyOrigin().AllowAnyMethod().AllowAnyHeader()));

var app = builder.Build();

app.UseCors();

// ── Enroll a person ──────────────────────────────────────────────────────────
app.MapPost("/api/persons/enroll", (EnrollRequest req, AttendanceStore store) =>
{
    if (string.IsNullOrWhiteSpace(req.Name) || req.Descriptor is not { Length: 128 })
        return Results.BadRequest("Name and a 128-element descriptor are required.");

    var person = new Person(Guid.NewGuid(), req.Name.Trim(), req.Descriptor, DateTime.UtcNow);
    store.AddPerson(person);
    return Results.Created($"/api/persons/{person.Id}", new { person.Id, person.Name });
});

// ── List enrolled persons ────────────────────────────────────────────────────
app.MapGet("/api/persons", (AttendanceStore store) =>
    store.GetAllPersons().Select(p => new { p.Id, p.Name, p.EnrolledAt }));

// ── Check in (face recognition) ──────────────────────────────────────────────
app.MapPost("/api/attendance/checkin", (CheckInRequest req, AttendanceStore store) =>
{
    if (req.Descriptor is not { Length: 128 })
        return Results.BadRequest("A 128-element descriptor is required.");

    const float Threshold = 0.6f;

    Person? best = null;
    float bestDist = float.MaxValue;

    foreach (var person in store.GetAllPersons())
    {
        float dist = EuclideanDistance(req.Descriptor, person.Descriptor);
        if (dist < bestDist)
        {
            bestDist = dist;
            best = person;
        }
    }

    if (best is null || bestDist > Threshold)
        return Results.Ok(new CheckInResponse(false, null, null, bestDist));

    // Idempotent: only mark once per person per calendar day (UTC)
    var today = DateOnly.FromDateTime(DateTime.UtcNow);
    bool alreadyCheckedIn = store.HasCheckedInToday(best.Id, today);

    if (!alreadyCheckedIn)
    {
        var record = new AttendanceRecord(Guid.NewGuid(), best.Id, best.Name, DateTime.UtcNow);
        store.AddAttendance(record);
    }

    return Results.Ok(new CheckInResponse(true, best.Id, best.Name, bestDist));
});

// ── Today's attendance log ───────────────────────────────────────────────────
app.MapGet("/api/attendance", (AttendanceStore store) =>
{
    var today = DateOnly.FromDateTime(DateTime.UtcNow);
    return store.GetAttendanceForDay(today)
                .OrderBy(r => r.CheckedInAt)
                .Select(r => new { r.Id, r.PersonId, r.PersonName, r.CheckedInAt });
});

app.Run();

// ── Helpers ──────────────────────────────────────────────────────────────────
static float EuclideanDistance(float[] a, float[] b)
{
    float sum = 0f;
    for (int i = 0; i < a.Length; i++)
    {
        float d = a[i] - b[i];
        sum += d * d;
    }
    return MathF.Sqrt(sum);
}

// ── Models ───────────────────────────────────────────────────────────────────
record Person(Guid Id, string Name, float[] Descriptor, DateTime EnrolledAt);
record AttendanceRecord(Guid Id, Guid PersonId, string PersonName, DateTime CheckedInAt);
record EnrollRequest(string Name, float[] Descriptor);
record CheckInRequest(float[] Descriptor);
record CheckInResponse(bool Recognized, Guid? PersonId, string? PersonName, float Distance);

// ── In-memory store ──────────────────────────────────────────────────────────
class AttendanceStore
{
    private readonly List<Person> _persons = [];
    private readonly List<AttendanceRecord> _attendance = [];
    private readonly Lock _lock = new();

    public void AddPerson(Person person)
    {
        lock (_lock) _persons.Add(person);
    }

    public IReadOnlyList<Person> GetAllPersons()
    {
        lock (_lock) return [.. _persons];
    }

    public void AddAttendance(AttendanceRecord record)
    {
        lock (_lock) _attendance.Add(record);
    }

    public bool HasCheckedInToday(Guid personId, DateOnly date)
    {
        lock (_lock)
            return _attendance.Any(r =>
                r.PersonId == personId &&
                DateOnly.FromDateTime(r.CheckedInAt) == date);
    }

    public IReadOnlyList<AttendanceRecord> GetAttendanceForDay(DateOnly date)
    {
        lock (_lock)
            return [.. _attendance.Where(r => DateOnly.FromDateTime(r.CheckedInAt) == date)];
    }
}
