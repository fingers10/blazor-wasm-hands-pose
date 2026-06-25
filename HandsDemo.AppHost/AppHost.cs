var builder = DistributedApplication.CreateBuilder(args);

var attendanceDatabaseServer = builder.AddPostgres("postgres")
    .WithImage("pgvector/pgvector", "pg17")   // pre-built image with pgvector extension
    .WithPgAdmin(pgadmin =>
    {
        pgadmin.WithUrlForEndpoint("http", u => u.DisplayText = "pgAdmin Console");
    })
    .WithUrlForEndpoint("http", u => u.DisplayText = "Pg Admin");

var attendanceDb = attendanceDatabaseServer
    .WithUrlForEndpoint("tcp", u => u.DisplayLocation = UrlDisplayLocation.DetailsOnly)
    .AddDatabase("attendance-db", "Attendance");

var attendanceApi = builder.AddProject<Projects.AttendanceApi>("Attendance-API")
    .WithUrlForEndpoint("https", u => u.DisplayText = "Attendance API")
    .WithReference(attendanceDb)
    .WaitFor(attendanceDb);

#pragma warning disable ASPIREBROWSERLOGS001 // Type is for evaluation purposes only and is subject to change or removal in future updates. Suppress this diagnostic to proceed.
var attendanceWeb = builder.AddProject<Projects.AttendanceDemo>("Attendance-Web")
    .WithUrlForEndpoint("https", u => u.DisplayText = "Attendance Demo")
    .WithBrowserLogs()
    .WithReference(attendanceApi, "ApiBaseAddress");

var attendanceWebUrl = attendanceWeb.GetEndpoint("https");

attendanceWeb
    .WaitFor(attendanceApi);

var handsWeb = builder.AddProject<Projects.HandsDemo>("Hands-Web")
    .WithUrlForEndpoint("https", u => u.DisplayText = "Hands Demo")
    .WithBrowserLogs();

var faceMeshWeb = builder.AddProject<Projects.FaceMeshDemo>("FaceMesh-Web")
    .WithUrlForEndpoint("https", u => u.DisplayText = "Face Mesh Demo")
    .WithBrowserLogs();

// ── Face Mesh Attendance demo ─────────────────────────────────────────────────
var faceMeshAttendanceDb = attendanceDatabaseServer
    .AddDatabase("facemesh-db", "FaceMeshAttendance");

var faceMeshAttendanceApi = builder.AddProject<Projects.FaceMeshAttendanceApi>("FaceMeshAttendance-API")
    .WithUrlForEndpoint("https", u => u.DisplayText = "Face Mesh Attendance API")
    .WithReference(faceMeshAttendanceDb)
    .WaitFor(faceMeshAttendanceDb);

var faceMeshAttendanceWeb = builder.AddProject<Projects.FaceMeshAttendanceDemo>("FaceMeshAttendance-Web")
    .WithUrlForEndpoint("https", u => u.DisplayText = "Face Mesh Attendance Demo")
    .WithBrowserLogs()
    .WithReference(faceMeshAttendanceApi, "FaceMeshAttendanceApi")
    .WaitFor(faceMeshAttendanceApi);
#pragma warning restore ASPIREBROWSERLOGS001 // Type is for evaluation purposes only and is subject to change or removal in future updates. Suppress this diagnostic to proceed.

// ── Fingerprint / WebAuthn demo ───────────────────────────────────────────────
var fingerprintDb = attendanceDatabaseServer
    .AddDatabase("fingerprint-db", "Fingerprint");

// Declare the web project first so we can inject its HTTPS endpoint into the API.
// Aspire pre-assigns port numbers before any process starts, so there is no
// circular-dependency problem at runtime.
#pragma warning disable ASPIREBROWSERLOGS001
var fingerprintWeb = builder.AddProject<Projects.FingerprintDemo>("Fingerprint-Web")
    .WithUrlForEndpoint("https", u => u.DisplayText = "Fingerprint Demo")
    .WithBrowserLogs();
#pragma warning restore ASPIREBROWSERLOGS001

var fingerprintApi = builder.AddProject<Projects.FingerprintApi>("Fingerprint-API")
    .WithUrlForEndpoint("https", u => u.DisplayText = "Fingerprint API")
    .WithReference(fingerprintDb)
    .WaitFor(fingerprintDb)
    // Inject the frontend origin so Fido2NetLib can validate WebAuthn rpId/origin
    .WithEnvironment("WebAuthn__Origins", fingerprintWeb.GetEndpoint("https"));

fingerprintWeb
    .WithReference(fingerprintApi, "FingerprintApi")
    .WaitFor(fingerprintApi);

builder.Build().Run();
