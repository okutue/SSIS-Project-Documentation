using Microsoft.AspNetCore.Components.Web;
using Microsoft.Extensions.DependencyInjection;
using MudBlazor.Services;
using Photino.Blazor;
using SsisLineage.UI.Services;

namespace SsisLineage.Desktop;

internal static class Program
{
    [STAThread]
    private static void Main(string[] args)
    {
        var builder = PhotinoBlazorAppBuilder.CreateDefault(args);

        builder.Services.AddLogging();

        // MudBlazor
        builder.Services.AddMudServices();

        // Shared UI library services
        builder.Services.AddSingleton<LineageReportStore>();
        builder.Services.AddSingleton<RecentProjectsService>();
        builder.Services.AddSingleton<IFolderPicker, PhotinoFolderPicker>();
        builder.Services.AddScoped<LineageUserSession>();
        builder.Services.AddScoped<LineageService>();

        // Root components
        builder.RootComponents.Add<Routes>("#app");
        builder.RootComponents.Add<HeadOutlet>("head::after");

        var app = builder.Build();

        app.MainWindow
            .SetTitle("SSIS Lineage")
            .SetUseOsDefaultSize(false)
            .SetSize(1600, 1000)
            .SetDevToolsEnabled(true);

        // Expose the window to the native folder picker
        DesktopWindowHolder.Window = app.MainWindow;

        AppDomain.CurrentDomain.UnhandledException += (_, error) =>
        {
            app.MainWindow.ShowMessage("Fatal exception", error.ExceptionObject?.ToString() ?? "Unknown error");
        };

        app.Run();
    }
}
