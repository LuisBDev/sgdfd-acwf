using ACWF.Configuration;
using ACWF.Firma;
using ACWF.Hosting;
using ACWF.Tray;
using ACWF.Update;
using ACWF.WebSocket;
using System.Reflection;
using Microsoft.Extensions.Options;
using Serilog;
using Velopack;

// Alias para evitar ambigüedad con Velopack.UpdateOptions
using AppUpdateOptions = ACWF.Configuration.UpdateOptions;

// Inicialización de Velopack — DEBE ser la primera instrucción.
// Maneja eventos de ciclo de vida install/update/uninstall y puede salir del proceso.
VelopackApp.Build()
    .OnFirstRun(_ =>
    {
        string exePath = Environment.ProcessPath ?? System.Reflection.Assembly.GetExecutingAssembly().Location;
        UriSchemeHelper.EnsureRegistered("acwf", exePath);
        UriSchemeHelper.EnsureRegistered("acwf-dev", exePath);
    })
    .OnAfterUpdateFastCallback(_ =>
    {
        string exePath = Environment.ProcessPath ?? System.Reflection.Assembly.GetExecutingAssembly().Location;
        UriSchemeHelper.EnsureRegistered("acwf", exePath);
        UriSchemeHelper.EnsureRegistered("acwf-dev", exePath);
    })
    .OnBeforeUninstallFastCallback(_ =>
    {
        UriSchemeHelper.Unregister("acwf");
        UriSchemeHelper.Unregister("acwf-dev");
    })
    .Run();

// Si se lanzó vía URI scheme, inferir el environment del nombre del scheme.
string? uriArg = args.SkipWhile(a => a != "--uri-invoke").Skip(1).FirstOrDefault();
if (uriArg is not null && uriArg.StartsWith("acwf-dev", StringComparison.OrdinalIgnoreCase))
    Environment.SetEnvironmentVariable("ASPNETCORE_ENVIRONMENT", "Development");

// Variante horneada en el build (AssemblyMetadata "AcwfVariant": Dev | Prod).
// Es la fuente de verdad para un build INSTALADO, que no tiene ASPNETCORE_ENVIRONMENT.
// Solo aplica si el environment no fue fijado antes (por URI o por env var en dotnet run).
string bakedVariant = System.Reflection.Assembly.GetEntryAssembly()?
    .GetCustomAttributes<System.Reflection.AssemblyMetadataAttribute>()
    .FirstOrDefault(a => a.Key == "AcwfVariant")?.Value
    ?? "Prod";

if (Environment.GetEnvironmentVariable("ASPNETCORE_ENVIRONMENT") is null
    && Environment.GetEnvironmentVariable("DOTNET_ENVIRONMENT") is null)
{
    Environment.SetEnvironmentVariable(
        "ASPNETCORE_ENVIRONMENT",
        bakedVariant.Equals("Dev", StringComparison.OrdinalIgnoreCase) ? "Development" : "Production");
}

// Determinar el environment y los identificadores derivados.
string env = Environment.GetEnvironmentVariable("ASPNETCORE_ENVIRONMENT")
    ?? Environment.GetEnvironmentVariable("DOTNET_ENVIRONMENT")
    ?? "Production";

string packId = string.Equals(env, "Development", StringComparison.OrdinalIgnoreCase)
    ? "ACWF-Dev"
    : "ACWF";

string uriScheme = string.Equals(env, "Development", StringComparison.OrdinalIgnoreCase)
    ? "acwf-dev"
    : "acwf";

string mutexSuffix = string.Equals(env, "Development", StringComparison.OrdinalIgnoreCase)
    ? "Dev"
    : "Prod";

// Guard de única instancia — salir silenciosamente si otra instancia de esta variante está corriendo.
using IDisposable instanceGuard = InstanceGuard.Acquire(mutexSuffix);

// Construir el ASP.NET Core Generic Host.
var builder = WebApplication.CreateBuilder(args);

builder.WebHost.UseKestrel(o =>
{
    int port = builder.Configuration.GetValue<int>("Acwf:Port", 7272);
    o.ListenLocalhost(port);
});

// Configurar Serilog como proveedor de logging.
string logDir = System.IO.Path.Combine(
    Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
    packId,
    "logs");

builder.Host.UseSerilog((ctx, cfg) =>
{
    cfg.ReadFrom.Configuration(ctx.Configuration)
       .WriteTo.File(
           path: System.IO.Path.Combine(logDir, "acwf-.log"),
           rollingInterval: RollingInterval.Day,
           retainedFileCountLimit: 7,
           fileSizeLimitBytes: 10_000_000);

    cfg.WriteTo.Console();
});

// Registrar todos los servicios.
builder.Services.Configure<AcwfOptions>(builder.Configuration.GetSection("Acwf"));
builder.Services.Configure<AppUpdateOptions>(builder.Configuration.GetSection("Update"));

builder.Services.AddSingleton<ISessionGate, SessionGate>();
builder.Services.AddScoped<IFileDepositService, FileDepositService>();
builder.Services.AddScoped<IFirmaWatcherService, FirmaWatcherService>();

// TrayIconService registrado como singleton, servido tanto por su tipo concreto como por la interfaz notifier.
builder.Services.AddSingleton<TrayIconService>();
builder.Services.AddSingleton<ITrayStateNotifier>(sp => sp.GetRequiredService<TrayIconService>());
builder.Services.AddHostedService(sp => sp.GetRequiredService<TrayIconService>());

// UpdateService registrado como singleton BackgroundService e interfaz trigger.
builder.Services.AddSingleton<UpdateService>();
builder.Services.AddSingleton<IUpdateTrigger>(sp => sp.GetRequiredService<UpdateService>());
builder.Services.AddHostedService(sp => sp.GetRequiredService<UpdateService>());

// Lazy<IUpdateTrigger> rompe la dependencia circular entre TrayIconService y UpdateService.
builder.Services.AddSingleton(sp => new Lazy<IUpdateTrigger>(() => sp.GetRequiredService<IUpdateTrigger>()));

// Construir la aplicación y configurar el pipeline de middleware.
var app = builder.Build();
app.UseWebSockets();
app.UseMiddleware<AcwfWebSocketMiddleware>();

// Escribir el archivo lock del puerto y registrar el URI scheme (idempotente en cada ejecución).
var acwfOptions = app.Services.GetRequiredService<IOptions<AcwfOptions>>().Value;
PortRegistry.Write(packId, acwfOptions.Port);

string exePathForScheme = Environment.ProcessPath
    ?? System.Reflection.Assembly.GetExecutingAssembly().Location;
UriSchemeHelper.EnsureRegistered(uriScheme, exePathForScheme);

// Registrar limpieza en apagado graceful.
var lifetime = app.Services.GetRequiredService<IHostApplicationLifetime>();
lifetime.ApplicationStopping.Register(() =>
{
    PortRegistry.Delete(packId);
    app.Logger.LogInformation("Port lock file deleted for {PackId}", packId);
});

app.Logger.LogInformation(
    "ACWF v{Version} starting — environment: {Env}, packId: {PackId}, port: {Port}",
    System.Reflection.Assembly.GetExecutingAssembly().GetName().Version?.ToString(3) ?? "0.1.0",
    env, packId, acwfOptions.Port);

// Ejecutar — bloquea hasta que el host se apaga.
app.Run();
