var builder = DistributedApplication.CreateBuilder(args);

var attendanceDatabaseServer = builder.AddPostgres("postgres")
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
    .WithReference(attendanceDb, "ConnectionString")
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
#pragma warning restore ASPIREBROWSERLOGS001 // Type is for evaluation purposes only and is subject to change or removal in future updates. Suppress this diagnostic to proceed.

builder.Build().Run();
