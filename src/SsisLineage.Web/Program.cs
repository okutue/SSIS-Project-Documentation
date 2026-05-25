using MudBlazor.Services;
using SsisLineage.Web.Components;
using SsisLineage.UI.Services;

var builder = WebApplication.CreateBuilder(args);

// Add services to the container.
builder.Services.AddRazorComponents()
    .AddInteractiveServerComponents();

// MudBlazor
builder.Services.AddMudServices();

// Add application services (shared UI library)
builder.Services.AddSingleton<LineageReportStore>();
builder.Services.AddSingleton<RecentProjectsService>();
builder.Services.AddSingleton<IFolderPicker, NoopFolderPicker>();
builder.Services.AddScoped<LineageUserSession>();
builder.Services.AddScoped<LineageService>();

var app = builder.Build();

// Configure the HTTP request pipeline.
if (!app.Environment.IsDevelopment())
{
    app.UseExceptionHandler("/Error", createScopeForErrors: true);
    // The default HSTS value is 30 days. You may want to change this for production scenarios, see https://aka.ms/aspnetcore-hsts.
    app.UseHsts();
}
app.UseStatusCodePagesWithReExecute("/not-found");
app.UseHttpsRedirection();

app.UseStaticFiles();
app.UseAntiforgery();

app.MapRazorComponents<App>()
    .AddInteractiveServerRenderMode()
    .AddAdditionalAssemblies(typeof(SsisLineage.UI.Components.Pages.Lineage).Assembly);

app.Run();
