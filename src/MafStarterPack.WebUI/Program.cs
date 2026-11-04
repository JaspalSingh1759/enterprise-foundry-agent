using MafStarterPack.WebUI.Components;
using MafStarterPack.WebUI.Disputes;

using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Authentication.OpenIdConnect;

using Microsoft.Agents.AI.AGUI;
using Microsoft.Extensions.AI;
using Microsoft.Identity.Web;

var builder = WebApplication.CreateBuilder(args);

builder.AddServiceDefaults();

builder.Services.AddRazorComponents()
                .AddInteractiveServerComponents();

builder.Services.AddHttpClient("agent", client =>
{
    client.BaseAddress = new Uri("https+http://agent");
    client.Timeout = TimeSpan.FromMinutes(5);
});

builder.Services.AddChatClient(sp => new AGUIChatClient(
    httpClient: sp.GetRequiredService<IHttpClientFactory>().CreateClient("agent"),
    endpoint: "ag-ui")
);

// Maple Bank dispute console (Day 9). Analysts sign in with Entra ID, and the console calls disputes-api with
// a token for the signed-in analyst. Without AzureAd:ClientId (local development) there's no sign-in, and calls
// use the developer's Azure CLI sign-in instead.
var signInEnabled = string.IsNullOrWhiteSpace(builder.Configuration["AzureAd:ClientId"]) == false;
if (signInEnabled)
{
    builder.Services.AddAuthentication(OpenIdConnectDefaults.AuthenticationScheme)
                    .AddMicrosoftIdentityWebApp(builder.Configuration.GetSection("AzureAd"))
                    .EnableTokenAcquisitionToCallDownstreamApi([builder.Configuration["DisputesApi:Scope"]!])
                    .AddInMemoryTokenCaches();
}
builder.Services.AddCascadingAuthenticationState();
builder.Services.AddAuthorization();
builder.Services.AddScoped<DisputesTokenSource>();
builder.Services.AddHttpClient<DisputesClient>(client =>
{
    client.BaseAddress = new Uri(builder.Configuration["DisputesApi:BaseUrl"] ?? "https+http://disputes-api");
    client.Timeout = TimeSpan.FromMinutes(5); // an investigation runs three agents
});

var app = builder.Build();

app.MapDefaultEndpoints();

// Configure the HTTP request pipeline.
if (app.Environment.IsDevelopment() == false)
{
    app.UseExceptionHandler("/Error", createScopeForErrors: true);
    app.UseHsts();
    app.UseHttpsRedirection();
}

if (signInEnabled)
{
    app.UseAuthentication();
    app.UseAuthorization();

    app.MapGet("/signin", (string? returnUrl) => Results.Challenge(
        new AuthenticationProperties { RedirectUri = returnUrl is { Length: > 0 } && returnUrl.StartsWith('/') ? returnUrl : "/disputes" },
        [OpenIdConnectDefaults.AuthenticationScheme]));
    app.MapGet("/signout", () => Results.SignOut(
        new AuthenticationProperties { RedirectUri = "/disputes" },
        [CookieAuthenticationDefaults.AuthenticationScheme, OpenIdConnectDefaults.AuthenticationScheme]));
}

app.UseAntiforgery();

app.UseStaticFiles();

app.MapRazorComponents<App>()
   .AddInteractiveServerRenderMode();

app.Run();
