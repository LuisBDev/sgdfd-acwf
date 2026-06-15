# ACWF — Agente Cliente WebSocket FirmaONPE

Agente de escritorio para Windows que actúa de puente entre la aplicación web **MFD** y el firmador de escritorio **FirmaONPE**, mediante un servidor WebSocket local. Distribución y auto-actualización con **Velopack**.

- **Stack:** .NET 10 (`net10.0-windows`, WinForms para el tray), Kestrel (WebSocket en `localhost:7272`), Serilog.
- **Empaquetado / updates:** Velopack 1.2.0 (`vpk`).
- **Releases:** GitHub Releases por **canal** (`stable` / `dev`), disparados por **tags**.

---

## Variantes (Dev / Prod)

ACWF se distribuye en **dos variantes que coexisten en la misma PC** (instalaciones, accesos directos, mutex y esquemas URI independientes):

| Variante | packId | Channel | Esquema URI | Mutex | GitHub Release |
|----------|-----------|----------|--------------|--------|----------------|
| **Producción** | `ACWF` | `stable` | `acwf://` | `ACWF-Prod` | estable |
| **Desarrollo** | `ACWF-Dev` | `dev` | `acwf-dev://` | `ACWF-Dev` | pre-release |

### Cómo sabe el build instalado qué variante es

La variante se **hornea en el build** (no se deriva de `ASPNETCORE_ENVIRONMENT`, que no sobrevive a la instalación). En CI se publica con `-p:AcwfVariant=Dev|Prod`, que se embebe como `AssemblyMetadata` en el ejecutable. Al arrancar, `Program.cs` lee esa metadata y fija el environment correspondiente (y con él el `packId`, `channel` vía appsettings, esquema URI y mutex).

Precedencia al determinar el environment:
1. Lanzamiento vía URI `acwf-dev://` → `Development`.
2. `ASPNETCORE_ENVIRONMENT` / `DOTNET_ENVIRONMENT` explícito (útil en `dotnet run`).
3. **Variante horneada** `AcwfVariant` (caso del app instalado).
4. Default → `Production`.

---

## Requisitos

- [.NET 10 SDK](https://dotnet.microsoft.com/download/dotnet/10.0)
- [Velopack CLI](https://docs.velopack.io/): `dotnet tool install -g vpk`

## Build y ejecución local

```powershell
# Build
dotnet build src/ACWF/ACWF.csproj

# Ejecutar como Development (channel dev, esquema acwf-dev://)
$env:ASPNETCORE_ENVIRONMENT = "Development"
dotnet run --project src/ACWF/ACWF.csproj

# Ejecutar como Production
$env:ASPNETCORE_ENVIRONMENT = "Production"
dotnet run --project src/ACWF/ACWF.csproj
```

## Configuración (`appsettings`)

Sección `Update` (overlay por environment en `appsettings.{Development|Production}.json`):

| Clave | Prod | Dev | Descripción |
|-------|------|-----|-------------|
| `RepoUrl` | repo GitHub | repo GitHub | Origen de releases para el auto-update |
| `Channel` | `stable` | `dev` | Canal Velopack; **debe coincidir** con el `--channel` del CI |
| `IncludePrerelease` | `false` | `true` | Si considera GitHub releases marcados pre-release |
| `CheckIntervalHours` | `6` | `6` | Frecuencia del chequeo de updates |
| `AccessToken` | `""` | `""` | Token OAuth solo si el repo es **privado** |

---

## CI/CD — Flujo de Release

Repositorio de releases: `https://github.com/LuisBDev/sgdfd-acwf`

### Convención de tags

| Tipo | Patrón | Ejemplo | Workflow | Resultado |
|------|--------|---------|----------|-----------|
| Producción | `vX.Y.Z` | `v1.2.3` | `release-prod.yml` | Release **estable**, channel `stable` |
| Desarrollo | `vX.Y.Z-dev.N` | `v1.2.3-dev.4` | `release-dev.yml` | **Pre-release**, channel `dev` |

> Los filtros de tags de GitHub Actions son **glob, no regex**. Prod escucha `v[0-9]*` y un tag dev también lo matchea; por eso el workflow de prod tiene un step *Resolve version* que **omite limpiamente** los tags con sufijo de pre-release (`-`/`+`). Dev escucha `v[0-9]*-dev.*`.

### Qué hace cada workflow

Ambos siguen el flujo recomendado por Velopack: **resolve version → publish → download → pack → upload**.

```
push tag ──▶ GitHub Actions (windows-latest)
   │
   ├─ Resolve version   : tag → SemVer sin la 'v'  (prod: omite pre-release)
   ├─ Setup .NET 10 + vpk
   ├─ Publish           : dotnet publish -r win-x64 --self-contained true
   │                       -p:Version=<v> -p:AcwfVariant=<Prod|Dev>
   ├─ Download          : vpk download github --channel <stable|dev> [--pre]
   │                       (trae el feed previo → habilita deltas; no-op en el 1er release)
   ├─ Pack              : vpk pack -u <ACWF|ACWF-Dev> -v <v> -c <stable|dev> -e ACWF.exe
   └─ Upload            : vpk upload github --channel <...> --merge --publish [--pre]
                           (crea el GitHub Release y mergea el manifiesto del canal)
```

Detalles clave:
- **`--self-contained true`**: el instalador incluye el runtime .NET 10; el firmante no instala nada aparte.
- **`-p:AcwfVariant`**: hornea la variante en el binario (ver sección Variantes).
- **`vpk download` antes de `pack`**: necesario para generar **deltas** y un feed `RELEASES` acumulativo. Tiene `continue-on-error` para el primer release (no hay nada que bajar).
- **`vpk upload github --merge`**: crea/actualiza el Release y fusiona `releases.<channel>.json` con lo existente. No se usa `gh release create`.
- **`permissions: contents: write`** y `secrets.GITHUB_TOKEN`: requeridos para que `vpk` publique el Release.

### Cómo cortar un release

```bash
# Producción
git tag v1.2.3
git push origin v1.2.3

# Desarrollo (pre-release)
git tag v1.2.3-dev.1
git push origin v1.2.3-dev.1
```

> El workflow se ejecuta sobre el commit al que apunta el tag, y el archivo del workflow debe existir en ese commit.

---

## Auto-actualización (runtime)

`UpdateService` (BackgroundService) consulta el repo vía `GithubSource(repoUrl, token, prerelease)` con `ExplicitChannel = <Channel>`:

- Primer chequeo 60 s después de arrancar; luego cada `CheckIntervalHours` (default 6 h).
- Descarga el update en background **sin aplicarlo**; notifica por el tray.
- Se aplica solo con acción explícita (ventana de update) y **solo si no hay sesión WebSocket activa**.
- Chequeo manual: menú del tray → **“Check for updates”**.

Cada variante solo ve su propio canal: prod (`stable`, releases estables) y dev (`dev`, pre-releases) no se pisan, aunque vivan en el mismo repositorio.

---

## Esquemas URI

En el primer arranque y tras cada update, ACWF registra en `HKCU\Software\Classes` (sin elevación):

- `acwf://` — Producción
- `acwf-dev://` — Desarrollo

---

## Estructura del proyecto

```
src/
  ACWF/
    Configuration/   — AcwfOptions, UpdateOptions
    Firma/           — FileDepositService, FirmaWatcherService
    System/          — TrayIconService, InstanceGuard, UriSchemeHelper, PortRegistry
    Update/          — UpdateService, UpdateWindow
    WebSocket/       — AcwfWebSocketMiddleware, AcwfSessionHandler, SessionGate, Messages/
    Program.cs
    appsettings.json (+ .Development / .Production)
.github/workflows/
  release-dev.yml    — channel dev,    pre-release
  release-prod.yml   — channel stable, release estable
```

## Protocolo WebSocket

Endpoint: `ws://localhost:7272/acwf` (una sola sesión a la vez). La especificación completa del protocolo está en `prd/PRD-ACWF-v0.1.0.md`.

---

## Notas y pendientes

- **Repositorio privado:** si `sgdfd-acwf` es privado, el auto-update necesita un token OAuth (`Update:AccessToken`). Incrustar un token en un app de escritorio tiene implicancias de seguridad — evaluar antes.
- **Primer release de cada canal:** no genera deltas (no hay versión previa); a partir del segundo sí.
- **Code signing (pendiente):** sin firmar el instalador con un certificado Authenticode, Windows SmartScreen mostrará advertencias a los firmantes. Recomendado para distribución amplia.
