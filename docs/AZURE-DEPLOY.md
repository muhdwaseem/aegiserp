# Deploying Aegis ERP to Azure App Service

How to host the Blazor Server app on **Azure App Service** and connect it to the
**Azure SQL Database** (from [AZURE-SETUP.md](AZURE-SETUP.md)) with passwordless
authentication. No code changes — the app already supports SQL Server via config.

Target: one resource group (`rg-aegis-erp`), region **UAE North (Dubai)**, Linux
App Service running .NET 8, connected to Azure SQL via a **managed identity** (no password).

```
        rg-aegis-erp  (UAE North)
   ┌──────────────────────────────────────┐
   │  App Service (Linux, .NET 8)  ─HTTPS─►  users
   │    Aegis ERP (Blazor Server)          │
   │        │  system-assigned identity     │
   │        ▼  (no password)                │
   │  Azure SQL Database  aegis_erp         │
   └──────────────────────────────────────┘
```

---

## 0. Prerequisites

- The **Azure SQL Database** already created ([AZURE-SETUP.md](AZURE-SETUP.md)).
- An **Entra ID admin** set on the SQL server (needed to grant the app access). You can set
  it in the portal: SQL server → **Microsoft Entra ID** → *Set admin* → pick your account.
- Everything below runs in **Azure Cloud Shell** (portal → `>_` icon → Bash) — no local
  installs needed. Publishing the app also works from Visual Studio / VS Code (see §5).

Set shared variables once in Cloud Shell:

```bash
RG=rg-aegis-erp
LOCATION=uaenorth
PLAN=plan-aegis-erp
APP=aegis-erp-$RANDOM                 # your public URL becomes https://$APP.azurewebsites.net
SQLSERVER=sql-aegis-erp-XXXX          # the server name you created (without .database.windows.net)
DB=aegis_erp
echo "App will be: https://$APP.azurewebsites.net"
```

---

## 1. Create the App Service

```bash
# Linux App Service plan. B1 (Basic) is the smallest tier that supports Always On,
# which Blazor Server needs. (Free F1 does NOT support Always On.)
az appservice plan create --name $PLAN --resource-group $RG --location $LOCATION --sku B1 --is-linux

# The web app on the .NET 8 runtime
az webapp create --name $APP --resource-group $RG --plan $PLAN --runtime "DOTNETCORE:8.0"
```

---

## 2. Give the app passwordless access to the database

**2a. Turn on the app's managed identity:**
```bash
az webapp identity assign --name $APP --resource-group $RG
```

**2b. Create a database user for that identity.** Open the Azure portal → your SQL database
→ **Query editor** → sign in **as the Entra admin**, and run (the user name is the App
Service name, `$APP`):

```sql
CREATE USER [aegis-erp-XXXX] FROM EXTERNAL PROVIDER;   -- replace with your $APP name
ALTER ROLE db_owner ADD MEMBER [aegis-erp-XXXX];        -- lets the app create+seed tables on first run
```

> `db_owner` is used here because the app creates its own tables (`EnsureCreated`) and seeds
> data on first start. Once you move to EF migrations (§7), you can drop this to
> `db_datareader` + `db_datawriter` and run migrations separately.

---

## 3. Configure the app

```bash
# Point the app at Azure SQL, passwordless (managed identity), and run as Production
az webapp config appsettings set --name $APP --resource-group $RG --settings \
  Database__Provider=SqlServer \
  ASPNETCORE_ENVIRONMENT=Production \
  "ConnectionStrings__SqlServer=Server=tcp:${SQLSERVER}.database.windows.net,1433;Initial Catalog=${DB};Authentication=Active Directory Default;Encrypt=True;TrustServerCertificate=False;"

# Blazor Server needs Web Sockets; Always On keeps the app warm; enforce HTTPS.
az webapp config set   --name $APP --resource-group $RG --web-sockets-enabled true --always-on true
az webapp update       --name $APP --resource-group $RG --https-only true --client-affinity-enabled true
```

- **Web Sockets** — the Blazor Server circuit runs over a WebSocket; this must be on.
- **Client affinity (sticky sessions)** — keeps a user pinned to one instance; required if you
  ever scale to multiple instances (see §7 for the proper scale-out answer).
- **`Authentication=Active Directory Default`** — on App Service this transparently uses the
  managed identity from step 2, so there is **no password anywhere**.

---

## 4. Publish the code

From the repo (Cloud Shell after `git clone`, or your machine with the Azure CLI):

```bash
# Build a release publish folder
dotnet publish src/AegisErp.Web -c Release -o ./publish

# Zip it and deploy
cd publish && zip -r ../app.zip . && cd ..
az webapp deploy --resource-group $RG --name $APP --src-path app.zip --type zip
```

On **Windows PowerShell** (local), zip with `Compress-Archive` instead:

```powershell
dotnet publish src/AegisErp.Web -c Release -o ./publish
Compress-Archive -Path ./publish/* -DestinationPath app.zip -Force
az webapp deploy --resource-group $RG --name $APP --src-path app.zip --type zip
```

On first request the app creates its tables in Azure SQL and seeds the demo data (same as it
did locally). If you deployed **before** granting DB access in §2, just restart:
`az webapp restart --name $APP --resource-group $RG`.

---

## 5. Alternative: publish from Visual Studio / VS Code

- **Visual Studio:** right-click the `AegisErp.Web` project → **Publish** → **Azure** →
  **Azure App Service (Linux)** → pick your app → Publish. Set the connection string and
  `Database:Provider` under the publish profile's **Service Dependencies / Configuration**.
- **VS Code:** install the *Azure App Service* extension → sign in → right-click the app →
  **Deploy to Web App** → select the `AegisErp.Web` publish output.

---

## 6. Verify

1. Browse **https://$APP.azurewebsites.net** → you should get the sign-in page.
2. Sign in with a seeded account (`admin@aegisfze.com` / `Admin@123!`).
3. Check the Trial Balance reads **In balance**.
4. If something's off, stream the logs:
   ```bash
   az webapp log tail --name $APP --resource-group $RG
   ```

---

## 7. Production hardening (before real go-live)

| Item | Why / What |
|---|---|
| **Remove the demo seed** | Don't ship `admin@aegisfze.com` / `Admin@123!` to production. Gate the demo users + demo company behind `Development`, and create the first real admin from secure config or Key Vault. |
| **EF migrations** | Switch from `EnsureCreated()` to migrations; then the app identity only needs `db_datareader` + `db_datawriter`, and schema changes are versioned. |
| **Scale-out** | Blazor Server across multiple instances needs sticky sessions (already enabled) **or**, better, an **Azure SignalR Service** in Default mode to offload circuits. |
| **Private networking** | VNet-integrate the app and put SQL behind a **Private Endpoint** so the database isn't publicly reachable. |
| **Custom domain + TLS** | Add your domain and a free App Service managed certificate. |
| **Monitoring** | Enable **Application Insights** for logs, performance, and failure alerts. |
| **Secrets** | Any secrets go in **Azure Key Vault** (referenced from App Service settings). |

---

## 8. Cost note

A **B1** Linux App Service plan is a small fixed monthly cost and is the practical minimum for
Blazor Server (needs Always On). You can start there and scale the plan up/out later without
downtime. Pair it with the serverless/free-tier Azure SQL database and total cost stays low
for a pilot.

---

## Quick reference

| Thing | Value |
|---|---|
| App URL | `https://<APP>.azurewebsites.net` |
| Runtime | .NET 8 (Linux) |
| Required settings | Web Sockets **on**, Always On **on**, HTTPS-only **on** |
| App config | `Database__Provider=SqlServer`, `ConnectionStrings__SqlServer=…Authentication=Active Directory Default…` |
| DB auth | System-assigned managed identity → SQL user with `db_owner` (first run) |
| Publish | `dotnet publish -c Release` → zip → `az webapp deploy` |
