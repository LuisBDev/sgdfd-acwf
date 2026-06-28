# ACD — Asistente de Conexión Documental (Document Connection Assistant)

Windows desktop agent that bridges a web application (MFD — Módulo de Firma Documental) with [FirmaONPE](https://www.gob.pe/onpe), the Peruvian national digital signing tool, through a local WebSocket server. Built for government document workflows where digital signatures carry legal weight.

## How It Works

ACD runs as a **system tray application** listening on `localhost:7272`. When a user initiates a digital signature from the MFD web app:

1. The browser connects to ACD via WebSocket and sends the PDF document.
2. ACD deposits the file in a watched directory (`C:\TFIRMA`) where FirmaONPE picks it up.
3. FirmaONPE signs the document using the user's digital certificate.
4. ACD detects the signed file and streams it back to the browser.
5. The browser uploads the signed PDF to the backend, which validates the signature and stores it in the document management system (SGD).

The agent is launched on demand via a custom URI scheme (`acd://`) registered in `HKCU\Software\Classes`, requiring no administrator privileges.

## Tech Stack

| Component | Details |
|-----------|---------|
| Runtime | .NET 10 (`net10.0-windows`), self-contained `win-x64` |
| Server | ASP.NET Core Kestrel (WebSocket + HTTP health check) |
| UI | WinForms system tray icon |
| Logging | Serilog (console + rolling file) |
| Installer & Updates | [Velopack](https://velopack.io) 1.2.0 |
| CI/CD | GitHub Actions |

## Distribution Channels

Two variants coexist side-by-side on the same machine with independent installations, URI schemes, and mutex names:

| Variant | Pack ID | Channel | URI Scheme | Release Type |
|---------|---------|---------|------------|--------------|
| Production | `ACD` | `stable` | `acd://` | Stable |
| Development | `ACD-Dev` | `dev` | `acd-dev://` | Pre-release |

The variant is embedded at build time via `AssemblyMetadata` (`-p:AcdVariant=Prod|Dev`), so the installed binary knows its identity without relying on environment variables.

## Build & Run

**Prerequisites:** [.NET 10 SDK](https://dotnet.microsoft.com/download/dotnet/10.0), [Velopack CLI](https://docs.velopack.io/) (`dotnet tool install -g vpk`)

```powershell
dotnet build src/ACD/ACD.csproj

# Run as Development (dev channel, acd-dev:// scheme)
$env:ASPNETCORE_ENVIRONMENT = "Development"
dotnet run --project src/ACD/ACD.csproj
```

## Release Pipeline

Releases are triggered by git tags and published to GitHub Releases via Velopack.

| Tag Pattern | Example | Workflow | Output |
|-------------|---------|----------|--------|
| `vX.Y.Z` | `v1.2.3` | `release-prod.yml` | Stable release, `stable` channel |
| `vX.Y.Z-dev.N` | `v1.2.3-dev.1` | `release-dev.yml` | Pre-release, `dev` channel |

Each workflow runs on `windows-latest` and follows the Velopack-recommended flow:

```
git tag → GitHub Actions
  ├─ dotnet publish (self-contained, win-x64)
  ├─ vpk download (fetch prior release feed for delta generation)
  ├─ vpk pack (create installer + delta packages)
  └─ vpk upload (publish to GitHub Releases)
```

The installer bundles the .NET 10 runtime — end users install nothing else.

## Auto-Update

A background service checks for updates every 6 hours (first check 60 seconds after startup). Updates are downloaded silently but only applied with explicit user action and never during an active signing session.

## Project Structure

```
src/ACD/
  Configuration/   — App and update options
  Firma/           — File deposit and FirmaONPE file watcher
  System/          — Tray icon, single-instance guard, URI scheme registration
  Update/          — Background update service
  WebSocket/       — WebSocket middleware, session handler, protocol messages
  Program.cs
.github/workflows/
  release-dev.yml
  release-prod.yml
```

## License

[MIT](LICENSE)
