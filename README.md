# Aegis ERP

A full-stack ERP for finance & operations, being built out from the original
single-file HTML prototype (`aegis_erp_bc 17.05.2026.html`).

**Stack:** ASP.NET Core 8 · Blazor Server · MudBlazor · Entity Framework Core ·
ASP.NET Core Identity · PostgreSQL (production) / SQLite (zero-setup local dev).

The repository contains a **working General Ledger core**, a **complete Accounts
Receivable slice**, and **cookie-based authentication with roles**. Every posting flow
runs through one domain posting engine inside a database transaction, so the books are
balanced by construction.

## What works today

| Screen | Route | Access |
|---|---|---|
| Sign in / access denied | `/Account/Login`, `/Account/AccessDenied` | anonymous |
| Dashboard (KPI cards) | `/` | any signed-in user |
| Chart of Accounts | `/chart-of-accounts` | any signed-in user |
| General Ledger | `/general-ledger` | any signed-in user |
| Trial Balance | `/trial-balance` | any signed-in user |
| Cash Flow (indirect method, reconciles to cash) | `/cash-flow` | any signed-in user |
| MIS &amp; Segment Reports (segment P&amp;L, customer revenue, vendor spend) | `/reports` | any signed-in user |
| Journal Voucher | `/journal-voucher` | Admin, Accountant |
| Customers (+ statement drill-down) | `/customers` | any signed-in user (create: posters) |
| Sales Invoice → **auto-generates GL voucher** | `/sales-invoice` | Admin, Accountant |
| Estimate / Quotation (non-posting, converts to invoice) | `/estimate` | any signed-in user (edit: posters) |
| Delivery Note (non-posting) | `/delivery-note` | any signed-in user (edit: posters) |
| Receipt Voucher (invoice allocation or on-account) | `/receipt-voucher` | Admin, Accountant |
| Credit Note → **auto-generates GL voucher** | `/credit-note` | Admin, Accountant |
| AR Aging (buckets by days past due) | `/ar-aging` | any signed-in user |
| Vendors (+ statement drill-down) | `/vendors` | any signed-in user (create: posters) |
| Purchase Invoice → **auto-generates GL voucher** | `/purchase-invoice` | Admin, Accountant |
| Payment Voucher (invoice allocation or on-account) | `/payment-voucher` | Admin, Accountant |
| Debit Note → **auto-generates GL voucher** | `/debit-note` | Admin, Accountant |
| AP Aging (buckets by days past due) | `/ap-aging` | any signed-in user |
| Users & Roles | `/users` | Admin |
| Remaining modules | `/soon/{name}` | placeholders |

### Demo accounts (seeded)

| Email | Password | Roles |
|---|---|---|
| owner@aegiserp.com | Aegis#Owner2026 | Admin, Accountant |
| accounts@aegiserp.com | Aegis#Finance2026 | Accountant |
| readonly@aegiserp.com | Aegis#View2026 | Viewer (read-only) |

These credentials are not shown on the login page itself — keep this table private.

The database is seeded on first run with the demo company (Aegis FZE, AED): chart of
accounts, opening balances, three customers, four sales invoices and two receipts —
all posted through the real double-entry engine, so the trial balance is in balance
and the AR aging shows a live overdue picture.

## Multi-company (multi-tenancy)

The app manages **several companies from one installation** — an accounting/audit firm keeping the
books for multiple clients.

- Every business entity carries a `CompanyId` (`ICompanyScoped`), and `AegisDbContext` applies a
  **global query filter** on it. Isolation is therefore structural: a service that forgets to filter
  still cannot read — or post into — another company's books.
- `SaveChanges` stamps the active company onto new rows and **throws on a cross-company write**, so
  a stray record can't land in the wrong client's ledger.
- Codes and document numbers are unique **per company** (`(CompanyId, Code)`), so each company has
  its own chart of accounts and its own `INV-2026-0001`.
- `UserCompanyAccess (UserId, CompanyId, Role)` restricts staff to the engagements they're assigned
  to; a user can be Accountant in one company and Viewer — or absent — in another.
- `FirmAdmin` is the only global role: it spans every company and manages companies and access at
  `/companies`. The header switcher changes the active company.

Seeded demo: **Aegis FZE** (full dataset) and **Meridian Trading LLC** (separate books, Jan–Dec year).
`admin@` is FirmAdmin (both), `finance@` is assigned to Aegis only, `viewer@` is read-only on both.

⚠️ Multi-company changed the schema. The app uses `EnsureCreated()`, so delete `aegis_erp.db` to
re-seed in dev; before production, switch to EF migrations (see below) — an existing single-company
database needs a migration that adds `CompanyId` and backfills it to the one existing company.

## Architecture

```
AegisErp.sln
src/
  AegisErp.Domain          # Entities + posting rules (pure C#, no dependencies)
  AegisErp.Infrastructure  # EF Core DbContext (+Identity stores), posting engine, services, seed
  AegisErp.Web             # Blazor Server UI (MudBlazor), auth wiring, pages
tests/
  AegisErp.Tests           # xunit: posting rules, invoice/receipt flows, aging buckets
```

Key design points:

- **One posting engine.** `JournalPoster.PostAsync` builds and validates every GL
  voucher (period open, date within period, debits = credits) on the *caller's*
  DbContext without committing. Document services (`SalesInvoiceService`,
  `ReceiptService`, `JournalService`) wrap it in their own transaction so a document
  and its voucher are saved atomically — an invoice can never exist without its GL entry.
- **Shared document numbers.** An invoice and its voucher share one number
  (`INV-2026-0143`); numbering continues from the highest existing suffix per year.
- **Subledger over the GL.** Customer balances, statements and aging are computed from
  posted AR documents; the AR control account (12010) holds the GL side. Receipts can
  be allocated to an invoice (capped at its outstanding) or left on account.
- **Auth.** Static-SSR login page (render-mode gated under `/Account`), cookie Identity,
  role-gated pages via `[Authorize]`, security-stamp revalidation on long-lived circuits.

## Run it

```bash
dotnet run --project src/AegisErp.Web
```

Open the URL it prints and sign in with a demo account. First launch creates
`aegis_erp.db` (SQLite) and seeds everything.

Run the tests:

```bash
dotnet test
```

## Switching to PostgreSQL

Edit `src/AegisErp.Web/appsettings.json` — no code changes needed:

```json
"Database": { "Provider": "Postgres" },
"ConnectionStrings": {
  "Postgres": "Host=localhost;Database=aegis_erp;Username=postgres;Password=yourpassword"
}
```

## Migrations

Local Sqlite dev still uses `EnsureCreated()` for a friction-free start — schema changes there
require deleting the dev `aegis_erp.db`. **Postgres (production) uses real EF migrations
instead**: `Program.cs` calls `db.Database.MigrateAsync()` whenever `Database:Provider` is
`Postgres`, so a schema change ships as a normal upgrade — the client's database is never wiped.

Migrations live in `src/AegisErp.Infrastructure/Migrations` and are authored against Npgsql via
`AegisDbContextFactory` (a design-time-only factory — it never opens a real connection, so `dotnet
ef migrations add` doesn't need a live Postgres server). After changing the model:

```bash
dotnet tool restore   # dotnet-ef is pinned in .config/dotnet-tools.json
dotnet ef migrations add <Name> -p src/AegisErp.Infrastructure -s src/AegisErp.Web
```

`dotnet ef database update` is optional — the app applies pending migrations itself on startup.

## Going live for a real client

1. **Postgres, not Sqlite.** Point `Database:Provider` at `Postgres` with a real connection
   string (a managed instance with persistent storage/backups — e.g. Supabase's free tier, or
   Render/Azure managed Postgres). Sqlite on Render's free tier has no persistent disk — the
   database resets on every restart/redeploy, which is fine for a demo but destroys real data.
   * If using Supabase specifically: free projects auto-pause after ~1 week of no activity
     (needs a manual "restore" click to wake back up), and the pooled connection string defaults
     to pgbouncer "transaction" mode, which doesn't fully support EF Core's prepared-statement
     caching — use the "Session" pooler port, or the direct connection string.
2. **Skip the demo data.** Set `Seed__DemoData=false` plus `Seed__AdminEmail` /
   `Seed__AdminPassword` (see `render.yaml`) so the client's database gets roles and one real
   FirmAdmin account instead of the seeded demo companies/users/sample invoices. Create the
   client's actual company and remaining users afterwards from `/companies` and `/users`.
3. **Off the free hosting tier** — persistent disk/managed DB, no cold-sleep, a custom domain
   with HTTPS, and connection strings/secrets set via the host's env vars rather than committed
   to `appsettings.json`.
4. Address the concurrency items below if the client will have multiple people posting
   concurrently against Postgres.

## Known production-hardening items

- **Concurrent document numbering** — fixed. `JournalPoster.NextDocNoAsync` takes a per-(prefix,
  year) Postgres advisory lock (`pg_advisory_xact_lock`, transaction-scoped, auto-released) before
  computing `max(existing) + 1`, so two simultaneous posts can no longer compute the same number.
  No-op on Sqlite (a single writer already serializes this). **Not yet exercised against a live
  Postgres server — run a real concurrent-post test once the client's Postgres is wired up.**
- **Concurrent receipt allocation** — fixed. `ReceiptService` takes a `SELECT ... FOR UPDATE` row
  lock on the target invoice (transaction-scoped) before checking its outstanding balance, so two
  simultaneous receipts against the same invoice serialize instead of both reading the same
  balance and both passing. No-op on Sqlite. **Same caveat: not yet verified against a live
  Postgres server.**
- **Credit notes / payment vouchers** aren't locked the same way yet — they have the identical
  read-then-check-then-insert shape as receipts. Worth the same treatment if a client will have
  multiple people posting concurrently against those flows.

## Roadmap (next slices, same pattern)

1. **AP** — Vendors, Purchase Invoice, Payment Voucher (mirror of AR).
2. Credit notes / debit notes and receipt allocation across multiple invoices.
3. Approval workflows (the prototype's Initiator → Finance → CFO → Posted chains).
4. Period close + P&L/Balance Sheet/Cash Flow computed from the ledger.
5. Inventory, HR/Payroll (WPS), Fixed Assets with depreciation runs.
6. Production hardening: EF migrations, HTTPS/hosting, backups, real user management UI.
