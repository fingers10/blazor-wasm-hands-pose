using Microsoft.AspNetCore.Components.Web;
using Microsoft.AspNetCore.Components.WebAssembly.Hosting;
using FaceMeshAttendanceDemo;

var builder = WebAssemblyHostBuilder.CreateDefault(args);
builder.RootComponents.Add<App>("#app");
builder.RootComponents.Add<HeadOutlet>("head::after");

builder.Services.AddScoped(sp => new HttpClient { BaseAddress = new Uri(builder.HostEnvironment.BaseAddress) });

var apiBaseUrl = builder.Configuration["FaceMeshAttendanceApi:BaseUrl"] ?? "http://localhost:5236";
builder.Services.AddHttpClient("facemesh", client =>
    client.BaseAddress = new Uri(apiBaseUrl));

await builder.Build().RunAsync();
