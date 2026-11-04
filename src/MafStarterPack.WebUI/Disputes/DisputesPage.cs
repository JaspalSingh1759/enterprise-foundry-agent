using System.Security.Claims;

using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Authorization;

namespace MafStarterPack.WebUI.Disputes;

/// <summary>
/// Base for the dispute console pages: sends anyone not signed in to /signin, and back to it when their token
/// can't be refreshed. What a user may do is decided by disputes-api; the console shows the API's answer.
/// </summary>
public abstract class DisputesPage : ComponentBase
{
    [Inject] protected DisputesClient Api { get; set; } = default!;
    [Inject] protected NavigationManager Nav { get; set; } = default!;
    [Inject] protected AuthenticationStateProvider Auth { get; set; } = default!;

    protected ClaimsPrincipal User { get; private set; } = new();
    protected bool Ready { get; private set; }

    protected override async Task OnInitializedAsync()
    {
        User = (await Auth.GetAuthenticationStateAsync()).User;
        if (Api.SignInEnabled && User.Identity?.IsAuthenticated != true)
        {
            SignIn();
            return;
        }
        Ready = true;
        await RunAsync(LoadAsync);
    }

    protected abstract Task LoadAsync();

    /// <summary>Runs an API action, sending the user to sign in again if their session has lapsed.</summary>
    protected async Task RunAsync(Func<Task> action)
    {
        try
        {
            await action();
        }
        catch (SignInRequiredException)
        {
            SignIn();
        }
    }

    private void SignIn() =>
        Nav.NavigateTo($"/signin?returnUrl={Uri.EscapeDataString("/" + Nav.ToBaseRelativePath(Nav.Uri))}", forceLoad: true);
}
