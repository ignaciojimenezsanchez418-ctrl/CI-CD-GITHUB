# FinDash CR · Backend

API REST en ASP.NET Core (.NET 10) para la app FinDash CR: usuarios, watchlist, portafolio
y alertas, con precios de CoinGecko y tipo de cambio del BCCR.

```
backend/
├── src/FinDash.Core     Lógica sin web ni MAUI: PortfolioCalculator, AlertRuleEngine
├── src/FinDash.Api      Endpoints, EF Core (SQLite), JWT, PriceService y AlertWorker
└── tests/FinDash.Tests  xUnit: lógica, lectura de CoinGecko/BCCR y pruebas de la API
```

## Correrlo

Requisito: SDK de .NET 10.

```bash
cd backend
dotnet test FinDash.slnx                 # 45 pruebas
dotnet run --project src/FinDash.Api     # http://localhost:5080
```

Sin configurar nada, la API usa precios **falsos** (los mismos de los mocks de la app), crea
`findash.db` y una llave JWT temporal. El emulador de Android la ve en `http://10.0.2.2:5080`.
En desarrollo, el contrato está en `http://localhost:5080/openapi/v1.json`.

### Llaves (nunca en el repositorio, RNF-05)

```bash
cd backend/src/FinDash.Api
dotnet user-secrets set "Jwt:Key" "una-frase-larga-de-al-menos-32-caracteres"
dotnet user-secrets set "CoinGecko:ApiKey" "CG-..."
dotnet user-secrets set "Bccr:Email" "correo-registrado@ejemplo.com"
dotnet user-secrets set "Bccr:Token" "TOKEN-DEL-BCCR"
```

En un servidor se usan variables de entorno: `Jwt__Key`, `CoinGecko__ApiKey`, `Bccr__Email`, `Bccr__Token`.
Fuera de Development la API no arranca sin `Jwt:Key`.

`Providers:Mode` decide las fuentes: `Auto` (real si hay llave), `Real` o `Fake` (pruebas y plan B de la demo).

### Migraciones

La API aplica las migraciones al arrancar. Si se cambia una entidad:

```bash
cd backend
dotnet tool restore
dotnet ef migrations add NombreDelCambio --project src/FinDash.Api --output-dir Data/Migrations
```

El CI falla si alguien cambia el modelo y olvida la migración.

## Endpoints

Todos menos `/auth/*` y `/health` piden `Authorization: Bearer <token>`. Los errores vienen
como ProblemDetails, con el mensaje para el usuario en `detail`. Enums como texto (`"Buy"`, `"CRC"`).

| Método | Ruta | Para qué | Equivale en la app |
|---|---|---|---|
| POST | `/auth/register`, `/auth/login` | Cuenta y token (7 días) | `IAuthService` |
| GET/PUT | `/me` | Correo, nombre, moneda base | Ajustes |
| GET | `/assets`, `/assets/search?q=` | Catálogo y búsqueda | `IMarketService.Assets / SearchAsync` |
| GET | `/market` | Tipo de cambio y precios de la caché | `GetSnapshotAsync` |
| GET | `/assets/{id}/history?range=Day\|Week\|Month\|Year` | Gráfica | `GetHistoryAsync` |
| GET/PUT/DELETE | `/watchlist`, `/watchlist/{assetId}` | Seguir activos | `Get/Add/RemoveWatchlist` |
| GET | `/portfolio` | Patrimonio y ganancia en CRC (y USD) | `IPortfolioService.GetSummaryAsync` |
| GET | `/portfolio/quantity/{assetId}` | Saldo para vender | `GetQuantityAsync` |
| GET/POST/DELETE | `/transactions`, `/transactions/{id}` | Movimientos | `GetTransactionsAsync / AddTransactionAsync` |
| GET/POST/PUT/DELETE | `/alerts`, `/alerts/{id}` | Alertas | `IAlertService` |
| PUT | `/alerts/{id}/enabled` | Encender o apagar | `SetActiveAsync` |
| GET | `/alerts/events?pending=true` | Disparadas sin notificar | `GetFiredAsync` |
| POST | `/alerts/events/delivered` | Marcarlas como notificadas | (nuevo) |

## Decisiones que cambian respecto al documento

- **.NET 10 (LTS)** en vez de .NET 9: el soporte de .NET 9 terminó el 10 nov 2026 y la app ya usa .NET 10.
- **SQLite** en el servidor para arrancar rápido. Para SQL Server: cambiar `UseSqlite` por
  `UseSqlServer`, quitar el conversor de `DateTimeOffset` en `FinDashDbContext` y crear las migraciones de nuevo.
- **CoinGecko cada 5 min** (no 30–60 s): además del límite por minuto, el plan Demo tiene un tope
  mensual de llamadas. Todas las cripto van en una sola llamada y hay un contador que nunca pasa
  de 25/min; ante un 429 se sirve la caché.
- **Costo promedio por fecha:** las transacciones se procesan en orden cronológico; una venta no
  cambia el promedio y deja ganancia realizada. Una venta con fecha anterior a la compra se rechaza.
- **Tipo de cambio por transacción (`FxRate`):** cada compra en USD guarda el tipo de cambio de su
  fecha. Con el de hoy, la ganancia en colones del ahorro en dólares siempre daría cero.
- **Gráfica histórica:** se pide a la fuente al abrirla y se guarda en memoria (10 min a 1 h).
- **Alertas (D11):** estado `Armed`/`Triggered` + `IsEnabled` + cooldown (60 min por defecto).
  Editar una alerta no la enciende si estaba apagada.

## Pendiente

- Validar `BccrProvider` contra el servicio real cuando haya token (el formato está probado con una respuesta guardada).
- Bloque 2: conectar la app (clientes HTTP, token en SecureStorage, caché SQLite, notificaciones).
