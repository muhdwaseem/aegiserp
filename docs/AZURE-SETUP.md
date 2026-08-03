# Azure SQL Database — Setup & Connection Runbook

How to create the production database for Aegis ERP on Microsoft Azure and connect the
app to it. The app already supports SQL Server / Azure SQL — this is purely
infrastructure + configuration, no code changes.

Target: **Azure SQL Database** (managed SQL Server), region **UAE North (Dubai)** for
data residency.

---

## 0. Prerequisite — an Azure subscription

If the client hasn't set up Azure yet:

1. Go to <https://portal.azure.com> and sign in (or sign up — new accounts get free credit).
2. Make sure there's an active **Subscription** (Azure's billing container). The client's
   purchased plan is the subscription you'll create resources under.

You do **not** need to install anything locally — everything below can be done in the
browser.

---

## 1. Create the database — Portal (click path)

1. In the portal search bar, type **SQL databases** → open it → **+ Create**.
2. **Basics tab:**
   - **Subscription:** the client's subscription.
   - **Resource group:** **Create new** → `rg-aegis-erp` (a folder for all Aegis resources).
   - **Database name:** `aegis_erp`
   - **Server:** **Create new**
     - Server name: `sql-aegis-erp-XXXX` (must be globally unique — add a few random chars)
     - Location: **(Middle East) UAE North**
     - Authentication: choose **Use SQL authentication** for now (simplest to get running).
       Set an admin login, e.g. `aegisadmin`, and a strong password — **save these**.
       *(You'll harden this to passwordless Entra ID auth later — see §5.)*
   - **Workload environment:** Development (you can scale up later).
   - **Compute + storage:** click **Configure database** → pick
     **General Purpose → Serverless**, min 1 vCore, and enable **auto-pause after 1 hour**.
     This is the cheapest way to start — it pauses when idle.
   - **Backup storage redundancy:** Locally-redundant (cheapest; change later if needed).
3. **Networking tab:**
   - Connectivity method: **Public endpoint** (fine to start; move to Private Endpoint for
     production — see §5).
   - **Allow Azure services and resources to access this server:** **Yes**.
   - **Add current client IP address:** **Yes** (so you can connect from this machine).
4. **Security tab:** leave defaults — **Transparent Data Encryption is ON by default**
   (your data is encrypted at rest automatically). Optionally enable **Microsoft Defender
   for SQL** (threat detection).
5. **Review + create** → **Create**. Wait ~2–3 minutes for deployment.

---

## 2. Create the database — Cloud Shell (script alternative)

Faster and repeatable. In the portal, click the **Cloud Shell** icon (`>_`) in the top bar,
choose **Bash**, and paste this (edit the password first):

```bash
RG=rg-aegis-erp
LOCATION=uaenorth
SERVER=sql-aegis-erp-$RANDOM        # globally unique
DB=aegis_erp
ADMIN=aegisadmin
PASSWORD='CHANGE-ME-Str0ng!Pass'   # <-- set a strong password

# Resource group
az group create --name $RG --location $LOCATION

# Logical SQL server + admin login
az sql server create --name $SERVER --resource-group $RG --location $LOCATION \
  --admin-user $ADMIN --admin-password "$PASSWORD"

# Database — serverless General Purpose, auto-pause to save cost
az sql db create --resource-group $RG --server $SERVER --name $DB \
  --edition GeneralPurpose --compute-model Serverless --family Gen5 --capacity 1 \
  --auto-pause-delay 60 --backup-storage-redundancy Local

# Firewall: allow your current IP + other Azure services
MYIP=$(curl -s https://api.ipify.org)
az sql server firewall-rule create --resource-group $RG --server $SERVER \
  --name allow-my-ip --start-ip-address $MYIP --end-ip-address $MYIP
az sql server firewall-rule create --resource-group $RG --server $SERVER \
  --name allow-azure-services --start-ip-address 0.0.0.0 --end-ip-address 0.0.0.0

# Print the connection string to paste into the app
echo ""
echo "Server=tcp:${SERVER}.database.windows.net,1433;Initial Catalog=${DB};User ID=${ADMIN};Password=${PASSWORD};Encrypt=True;TrustServerCertificate=False;"
```

The last line is the connection string you'll use in §3.

---

## 3. Connect the ERP to it

Point the app at Azure SQL by setting **two config values**. To keep the password out of
the repository, use environment variables (they override `appsettings.json`).

**PowerShell (local test run):**
```powershell
$env:Database__Provider   = "SqlServer"
$env:ConnectionStrings__SqlServer = "Server=tcp:sql-aegis-erp-XXXX.database.windows.net,1433;Initial Catalog=aegis_erp;User ID=aegisadmin;Password=YOUR_PASSWORD;Encrypt=True;TrustServerCertificate=False;"

dotnet run --project src/AegisErp.Web --urls http://localhost:5199
```

(The double underscore `__` is how .NET maps environment variables onto nested config keys.)

On first run the app **creates all its tables in Azure SQL and seeds the demo data**
automatically — the same behavior you saw locally. Sign in with `owner@aegiserp.com` /
`Aegis#Owner2026`.

> When the app itself is hosted on Azure (App Service), you set these same two values in the
> App Service **Configuration → Application settings** instead of environment variables on
> your machine.

---

## 4. Verify

- The app starts without database errors.
- In the Azure portal, open your SQL database → **Query editor**, sign in, and run
  `SELECT COUNT(*) FROM Accounts;` — you should see 12 (the seeded chart of accounts).
- The ERP's Trial Balance shows **In balance**.

---

## 5. Hardening for production (do before go-live)

| Step | Why |
|---|---|
| Switch to **Microsoft Entra ID authentication** + a **Managed Identity** for the App Service, then use `Authentication=Active Directory Default` in the connection string (already templated in `appsettings.json`) | No database password stored anywhere |
| Move any secrets to **Azure Key Vault** | Secrets never in config/repo |
| Replace the public endpoint with a **Private Endpoint** + VNet | Database not reachable from the internet |
| Remove the `allow-azure-services` (0.0.0.0) firewall rule once on a VNet | Tighter network surface |
| Enable **Microsoft Defender for SQL** and **Auditing** | Threat alerts + access log |
| Switch the app from `EnsureCreated()` to **EF Core migrations** | Versioned, safe schema changes |
| Consider **Always Encrypted** for the most sensitive columns (TRN, bank details) | Column-level encryption Azure admins can't read |

---

## Cost note

Serverless General Purpose with auto-pause is the cheapest realistic tier — you effectively
pay for compute only while the app is active, plus a small storage charge. You can start
there and scale up without downtime. (An even cheaper Basic DTU tier exists but doesn't
auto-pause.)

---

## Quick reference

| Thing | Value |
|---|---|
| Resource group | `rg-aegis-erp` |
| Region | UAE North (Dubai) |
| Server | `sql-aegis-erp-XXXX.database.windows.net` |
| Database | `aegis_erp` |
| App config keys | `Database:Provider = SqlServer`, `ConnectionStrings:SqlServer = …` |
| First-run behavior | tables auto-created + demo data seeded |
