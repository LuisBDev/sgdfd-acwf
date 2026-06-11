# ACWF — Agent Cliente WebSocket FirmaONPE

Windows desktop agent that bridges the MFD web application and the FirmaONPE desktop signer via a local WebSocket server.

## Prerequisites

- [.NET 10 SDK](https://dotnet.microsoft.com/download/dotnet/10.0)
- [Velopack CLI](https://docs.velopack.io/): `dotnet tool install -g vpk`

## Build

```powershell
dotnet build src/ACWF/ACWF.csproj
```

## Run (Development)

```powershell
$env:ASPNETCORE_ENVIRONMENT = "Development"
dotnet run --project src/ACWF/ACWF.csproj
```

## Run (Production)

```powershell
$env:ASPNETCORE_ENVIRONMENT = "Production"
dotnet run --project src/ACWF/ACWF.csproj
```

## Environment Variables

| Variable | Values | Description |
|----------|--------|-------------|
| `ASPNETCORE_ENVIRONMENT` | `Development`, `Production` | Selects the appsettings overlay and Mutex/packId variant |

## URI Scheme Registration

On first run and after updates, ACWF registers Windows URI schemes:

- `acwf://` — Production
- `acwf-dev://` — Development

Registration is written to `HKCU\Software\Classes` (current user only, no elevation required).

## CI/CD Tag Patterns

| Tag pattern | Workflow | Channel |
|-------------|----------|---------|
| `*-dev.*` (e.g. `1.0.0-dev.1`) | `release-dev.yml` | GitHub pre-release |
| `v[0-9]*` (e.g. `v1.0.0`) | `release-prod.yml` | GitHub stable release |

Push a tag matching the pattern to trigger the corresponding workflow. The workflow produces a Velopack installer and uploads it to GitHub Releases.

## Project Structure

```
src/
  ACWF/
    Configuration/   — AcwfOptions, UpdateOptions
    Firma/           — IFileDepositService, FileDepositService, IFirmaWatcherService, FirmaWatcherService
    System/          — TrayIconService, InstanceGuard, UriSchemeHelper, PortRegistry
    Update/          — UpdateService, UpdateWindow
    WebSocket/       — AcwfWebSocketMiddleware, AcwfSessionHandler, SessionGate, Messages/
    Program.cs
.github/workflows/
  release-dev.yml
  release-prod.yml
```

## WebSocket Protocol

Endpoint: `ws://localhost:7272/acwf`

The agent enforces single-session at a time. See `prd/PRD-ACWF-v0.1.0.md` for the full protocol specification.
