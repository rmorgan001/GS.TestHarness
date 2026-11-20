using Microsoft.AspNetCore.DataProtection;
/* Copyright(C) 2026  Rob Morgan (robert.morgan.e@gmail.com)

    This program is free software: you can redistribute it and/or modify
    it under the terms of the GNU General Public License as published
    by the Free Software Foundation, either version 3 of the License, or
    (at your option) any later version.

    This program is distributed in the hope that it will be useful,
    but WITHOUT ANY WARRANTY; without even the implied warranty of
    MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE.  See the
    GNU General Public License for more details.

    You should have received a copy of the GNU General Public License
    along with this program.  If not, see <https://www.gnu.org/licenses/>.
 */

using GS.TestHarness;
using GS.TestHarness.Components;
using GS.TestHarness.Engine;
using GS.TestHarness.Services;

var builder = WebApplication.CreateBuilder(new WebApplicationOptions
{
    Args = args,
    // Started from a script, a shortcut or another folder, the content root would be the current folder: use the
    // app's own folder, where appsettings.json is. Development (dotnet run, Visual Studio) keeps the project folder.
    ContentRootPath = IsDevelopment() ? null : AppContext.BaseDirectory
});

static bool IsDevelopment()
    => string.Equals(Environment.GetEnvironmentVariable("ASPNETCORE_ENVIRONMENT") ?? Environment.GetEnvironmentVariable("DOTNET_ENVIRONMENT"),
        "Development", StringComparison.OrdinalIgnoreCase);

// Serve wwwroot from the project when run from bin (the CLI, a script); a published copy has its own wwwroot and
// no manifest, so this does nothing there.
builder.WebHost.UseStaticWebAssets();

builder.Services.AddRazorComponents()
    .AddInteractiveServerComponents();

// Where the GSServer and GreenSwampAlpaca source is, so findings can point at the code to change.
builder.Services.AddSingleton(_ =>
{
    var section = builder.Configuration.GetSection("Codebases");
    var codebases = new Dictionary<Codebase, CodebaseOptions>();
    foreach (var codebase in Enum.GetValues<Codebase>())
        if (section.GetSection(codebase.ToString()).Get<CodebaseOptions>() is { } options)
            codebases[codebase] = options;
    return new SourceLocator(codebases, builder.Environment.ContentRootPath);
});
builder.Services.AddSingleton<TestRunner>();
builder.Services.AddSingleton(new ReportStore(builder.Configuration["ReportsFolder"] is { Length: > 0 } folder ? folder : ReportStore.DefaultFolder));
builder.Services.AddSingleton<RunService>();
builder.Services.AddSingleton<TargetStore>();
// Each person's own source folders (the Options tab), applied to the SourceLocator when created.
builder.Services.AddSingleton<SourceStore>();
// Data Protection encrypts the saved server passwords; a fixed name keeps its keys usable wherever the app runs from.
builder.Services.AddDataProtection().SetApplicationName("GS.TestHarness");
builder.Services.AddSingleton<ServerList>();
builder.Services.AddSingleton<ConnectionService>();

// Per browser tab: the server picked (and being dragged), and the popup dialogs.
builder.Services.AddScoped<Workbench>();
builder.Services.AddScoped<DialogService>();
builder.Services.AddScoped<RunPageState>();

var app = builder.Build();

// The source folders this person set apply from the start, the command line included.
app.Services.GetRequiredService<SourceStore>();

// --run: check one device from the command line and exit (see Cli).
if (Cli.IsCli(args))
    return await Cli.RunAsync(args, app.Services.GetRequiredService<SourceLocator>(), app.Services.GetRequiredService<ReportStore>());

if (!app.Environment.IsDevelopment())
{
    app.UseExceptionHandler("/Error", createScopeForErrors: true);
}
app.UseStatusCodePagesWithReExecute("/not-found", createScopeForStatusCodePages: true);
app.UseAntiforgery();
app.MapStaticAssets();

// The Recommendations tab as Markdown. A literal route, so it wins over {id}.md.
app.MapGet("/download/recommendations.md", async (ReportStore store) =>
    Results.File(System.Text.Encoding.UTF8.GetBytes(RecommendationBuilder.ToMarkdown(RecommendationBuilder.Build(await store.ListAsync()))),
        "text/markdown; charset=utf-8", "recommendations.md"));

// Report downloads: Markdown (to paste into an issue) and the raw JSON.
app.MapGet("/download/{id}.md", async (string id, ReportStore store) =>
    await TryLoadAsync(store, id) is { } report
        ? Results.File(System.Text.Encoding.UTF8.GetBytes(ReportStore.ToMarkdown(report)), "text/markdown; charset=utf-8", $"{id}.md")
        : Results.NotFound());
app.MapGet("/download/{id}.csv", async (string id, ReportStore store) =>
    await TryLoadAsync(store, id) is { } report
        ? Results.File(ReportCsv.Report(report), "text/csv; charset=utf-8", $"{report.Reference}_{ReportCsv.FileName(report.Label)}.csv")
        : Results.NotFound());
app.MapGet("/download/compare/{before}/{after}.csv", async (string before, string after, ReportStore store) =>
    await TryLoadAsync(store, before) is { } a && await TryLoadAsync(store, after) is { } b
        ? Results.File(ReportCsv.Comparison(a, b), "text/csv; charset=utf-8",
            $"{ReportCsv.FileName($"{a.Label}_vs_{b.Identity.DriverVersion}")}.csv")
        : Results.NotFound());
app.MapGet("/download/{id}.json", async (string id, ReportStore store) =>
    await TryLoadAsync(store, id) is { } report
        ? Results.File(System.Text.Json.JsonSerializer.SerializeToUtf8Bytes(report, ReportStore.Json), "application/json", $"{id}.json")
        : Results.NotFound());

// Disconnect what the sidebar's Connect button connected, so drivers that count clients don't stay connected.
app.Lifetime.ApplicationStopping.Register(() => app.Services.GetRequiredService<ConnectionService>().DisconnectAll());

app.MapRazorComponents<App>()
    .AddInteractiveServerRenderMode();

app.Run();
return 0;

// A report by ID, or null when there's none, the ID isn't one, or its file can't be read (cut short, not JSON): a 404, not a 500.
static async Task<RunReport?> TryLoadAsync(ReportStore store, string id)
{
    try { return await store.LoadAsync(id); }
    catch (Exception ex) when (ex is ArgumentException or System.Text.Json.JsonException or IOException or UnauthorizedAccessException)
    {
        return null;
    }
}
