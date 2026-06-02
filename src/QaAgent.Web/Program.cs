using QaAgent.App;
using QaAgent.Web.Components;
using QaAgent.Web.Services;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddRazorComponents()
    .AddInteractiveServerComponents();

builder.Services.AddSingleton(new WorkspacePaths());
builder.Services.AddSingleton<AgentSettings>();
builder.Services.AddSingleton<SchemaMonitor>();
builder.Services.AddHostedService(sp => sp.GetRequiredService<SchemaMonitor>());

var app = builder.Build();

if (!app.Environment.IsDevelopment())
{
    app.UseExceptionHandler("/Error", createScopeForErrors: true);
    app.UseHsts();
}
app.UseStatusCodePagesWithReExecute("/not-found", createScopeForStatusCodePages: true);

app.UseAntiforgery();

app.MapStaticAssets();
app.MapRazorComponents<App>()
    .AddInteractiveServerRenderMode();

app.Run();
