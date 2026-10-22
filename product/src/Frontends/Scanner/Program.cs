using EventTicketing.Scanner;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.AspNetCore.Components.WebAssembly.Hosting;

var builder = WebAssemblyHostBuilder.CreateDefault(args);
builder.RootComponents.Add<App>("#app");
builder.RootComponents.Add<HeadOutlet>("head::after");

// CheckInApi serves the scanner at /scanner/, so its API is the same origin, one level up.
builder.Services.AddScoped(_ => new HttpClient { BaseAddress = new Uri(new Uri(builder.HostEnvironment.BaseAddress), "/") });
builder.Services.AddScoped<DoorBrowser>();
builder.Services.AddScoped<DoorSync>();

await builder.Build().RunAsync();
