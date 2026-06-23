using Microsoft.AspNetCore.Components.Web;
using Microsoft.AspNetCore.Components.WebAssembly.Hosting;
using AttendanceDemo;

var builder = WebAssemblyHostBuilder.CreateDefault(args);
builder.RootComponents.Add<App>("#app");
builder.RootComponents.Add<HeadOutlet>("head::after");

builder.Services.AddScoped(sp => new HttpClient { BaseAddress = new Uri(builder.HostEnvironment.BaseAddress) });

var apiBaseUrl = builder.Configuration["AttendanceApi:BaseUrl"] ?? "http://localhost:5235";
builder.Services.AddHttpClient("attendance", client =>
    client.BaseAddress = new Uri(apiBaseUrl));

await builder.Build().RunAsync();
