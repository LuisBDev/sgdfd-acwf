using System.Reflection;
using ACD.Configuration;
using ACD.Firma;
using ACD.Firma.Signing;
using ACD.Hosting;
using ACD.Tray;
using ACD.Update;
using ACD.WebSocket;
using Microsoft.Extensions.Options;
using Serilog;
using Velopack;
// Alias para evitar ambigüedad con Velopack.UpdateOptions
using AppUpdateOptions = ACD.Configuration.UpdateOptions;

// Inicialización de Velopack — DEBE ser la primera instrucción.
// Maneja eventos de ciclo de vida install/update/uninstall y puede salir del proceso.
VelopackApp.Build()
    .OnFirstRun(_ =>
    {
        var exePath = Environment.ProcessPath ?? Assembly.GetExecutingAssembly().Location;
        UriSchemeHelper.EnsureRegistered("acd", exePath);
        UriSchemeHelper.EnsureRegistered("acd-dev", exePath);
    })
    .OnAfterUpdateFastCallback(_ =>
    {
        var exePath = Environment.ProcessPath ?? Assembly.GetExecutingAssembly().Location;
        UriSchemeHelper.EnsureRegistered("acd", exePath);
        UriSchemeHelper.EnsureRegistered("acd-dev", exePath);
    })
    .OnBeforeUninstallFastCallback(_ =>
    {
        UriSchemeHelper.Unregister("acd");
        UriSchemeHelper.Unregister("acd-dev");
    })
    .Run();

// Si se lanzó vía URI scheme, inferir el environment del nombre del scheme.
var uriArg = args.SkipWhile(a => a != "--uri-invoke").Skip(1).FirstOrDefault();
if (uriArg is not null && uriArg.StartsWith("acd-dev", StringComparison.OrdinalIgnoreCase))
    Environment.SetEnvironmentVariable("ASPNETCORE_ENVIRONMENT", "Development");

// Variante incrustada en el build (AssemblyMetadata "AcdVariant": Dev | Prod).
// Es el SOF para un build INSTALADO, que no tiene ASPNETCORE_ENVIRONMENT.
// Solo aplica si el environment no fue fijado antes (por URI o por env var en dotnet run).
var bakedVariant = Assembly.GetEntryAssembly()?
                       .GetCustomAttributes<AssemblyMetadataAttribute>()
                       .FirstOrDefault(a => a.Key == "AcdVariant")?.Value
                   ?? "Prod";

if (Environment.GetEnvironmentVariable("ASPNETCORE_ENVIRONMENT") is null
    && Environment.GetEnvironmentVariable("DOTNET_ENVIRONMENT") is null)
    Environment.SetEnvironmentVariable(
        "ASPNETCORE_ENVIRONMENT",
        bakedVariant.Equals("Dev", StringComparison.OrdinalIgnoreCase) ? "Development" : "Production");

// Determinar el environment y los identificadores derivados.
var env = Environment.GetEnvironmentVariable("ASPNETCORE_ENVIRONMENT")
          ?? Environment.GetEnvironmentVariable("DOTNET_ENVIRONMENT")
          ?? "Production";

var packId = string.Equals(env, "Development", StringComparison.OrdinalIgnoreCase)
    ? "ACD-Dev"
    : "ACD";

var uriScheme = string.Equals(env, "Development", StringComparison.OrdinalIgnoreCase)
    ? "acd-dev"
    : "acd";

var mutexSuffix = string.Equals(env, "Development", StringComparison.OrdinalIgnoreCase)
    ? "Dev"
    : "Prod";

// Guard de única instancia — salir silenciosamente si otra instancia de esta variante está corriendo.
using var instanceGuard = InstanceGuard.Acquire(mutexSuffix);

var builder = WebApplication.CreateBuilder(args);

builder.WebHost.UseKestrel(o =>
{
    var port = builder.Configuration.GetValue("Acd:Port", 7272);
    o.ListenLocalhost(port);
});

var logDir = Path.Combine(
    Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
    packId,
    "logs");

builder.Host.UseSerilog((ctx, cfg) =>
{
    cfg.ReadFrom.Configuration(ctx.Configuration)
        .WriteTo.File(
            Path.Combine(logDir, "acd-.log"),
            rollingInterval: RollingInterval.Day,
            retainedFileCountLimit: 7,
            fileSizeLimitBytes: 10_000_000);

    cfg.WriteTo.Console();
});

builder.Services.Configure<AcdOptions>(builder.Configuration.GetSection("Acd"));
builder.Services.Configure<AppUpdateOptions>(builder.Configuration.GetSection("Update"));

builder.Services.AddSingleton<ISessionGate, SessionGate>();
builder.Services.AddSingleton<IAcdSessionHandlerFactory, AcdSessionHandlerFactory>();
builder.Services.AddScoped<IFileDepositService, FileDepositService>();
builder.Services.AddScoped<IFirmaWatcherService, FirmaWatcherService>();

// Subsistema de firma (FirmaONPE).
builder.Services.AddSingleton<IProcessRunner, ProcessRunner>();
builder.Services.AddSingleton<IFirmaSignerResolver, RegistryFirmaSignerResolver>();
builder.Services.AddSingleton<IFirmaCommandBuilder, FirmaOnpeCommandBuilder>();
builder.Services.AddSingleton<IFirmaLauncher, FirmaLauncher>();

builder.Services.AddSingleton<TrayIconService>();
builder.Services.AddSingleton<ITrayStateNotifier>(sp => sp.GetRequiredService<TrayIconService>());
builder.Services.AddHostedService(sp => sp.GetRequiredService<TrayIconService>());

builder.Services.AddSingleton<UpdateService>();
builder.Services.AddSingleton<IUpdateTrigger>(sp => sp.GetRequiredService<UpdateService>());
builder.Services.AddHostedService(sp => sp.GetRequiredService<UpdateService>());

// Lazy<IUpdateTrigger> rompe la dependencia circular entre TrayIconService y UpdateService.
builder.Services.AddSingleton(sp => new Lazy<IUpdateTrigger>(() => sp.GetRequiredService<IUpdateTrigger>()));

var app = builder.Build();
app.UseWebSockets();
app.UseMiddleware<AcdWebSocketMiddleware>();

// Escribir el archivo lock del puerto y registrar el URI scheme (idempotente en cada ejecución).
var acdOptions = app.Services.GetRequiredService<IOptions<AcdOptions>>().Value;
PortRegistry.Write(packId, acdOptions.Port);

var exePathForScheme = Environment.ProcessPath
                       ?? Assembly.GetExecutingAssembly().Location;
UriSchemeHelper.EnsureRegistered(uriScheme, exePathForScheme);

// Registrar limpieza al apagar.
var lifetime = app.Services.GetRequiredService<IHostApplicationLifetime>();
lifetime.ApplicationStopping.Register(() =>
{
    PortRegistry.Delete(packId);
    app.Logger.LogInformation("Port lock file deleted for {PackId}", packId);
});

app.Logger.LogInformation(
    "ACD v{Version} starting — environment: {Env}, packId: {PackId}, port: {Port}",
    Assembly.GetExecutingAssembly().GetName().Version?.ToString(3) ?? "0.1.0",
    env, packId, acdOptions.Port);

app.Run();