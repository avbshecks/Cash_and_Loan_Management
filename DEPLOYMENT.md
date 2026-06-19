# CALM — Free UAT Deployment Guide

Deploy the **Cash & Liquidity Management** system to a free environment for user testing using:

| Layer | Service | Cost |
|---|---|---|
| Database | **Neon** (serverless PostgreSQL) | Free, no card |
| Backend API | **Render** (Docker web service) | Free, no card |
| Frontend | **Vercel** | Free, no card |

> ⚠️ Free-tier note: the Render API **sleeps after ~15 min idle**, so the first request after a pause takes ~50s (cold start). Hangfire daily jobs won't fire while asleep — fine for UAT, resolved on paid hosting.

---

## Step 1 — Database (Neon)

1. Go to <https://neon.tech> → sign up (GitHub login) → **Create project**.
2. Name it `calm-uat`, region closest to your testers, click **Create**.
3. On the dashboard, open **Connection Details** → set the snippet dropdown to **.NET**.
4. Copy the connection string. It looks like:
   ```
   Host=ep-xxxx.eu-central-1.aws.neon.tech;Database=neondb;Username=neondb_owner;Password=xxxx;SSL Mode=Require;Trust Server Certificate=true
   ```
   Keep this — it's the `ConnectionStrings__DefaultConnection` value below.

The database tables and the default **admin** user are created automatically on first API start (EF migrations + seeder).

---

## Step 2 — Backend API (Render)

1. Go to <https://render.com> → sign up (GitHub) → authorize access to the
   `avbshecks/Cash_and_Loan_Management` repo.
2. **New +** → **Web Service** → pick that repo.
3. Render auto-detects the `Dockerfile` and `render.yaml`. Confirm:
   - **Runtime:** Docker
   - **Plan:** Free
   - **Health Check Path:** `/health`
4. Add the **Environment Variables** (Render → Environment):

   | Key | Value |
   |---|---|
   | `ASPNETCORE_ENVIRONMENT` | `Production` |
   | `ConnectionStrings__DefaultConnection` | *(the Neon .NET string from Step 1)* |
   | `JwtSettings__Secret` | *(generate a long random string — example below)* |
   | `Cors__AllowedOrigins` | *(your Vercel URL — fill in after Step 3, e.g. `https://calm-fe.vercel.app`)* |

   Suggested fresh JWT secret (or generate your own):
   ```
   L792SDN7u1EXL/yfL1AHJ0ycueit1O7Kzr/2pSIqVUEOsFLEHgMT2G7Y2buVCV7Z
   ```
5. Click **Create Web Service**. First build takes a few minutes.
6. When live, note the URL, e.g. `https://calm-api.onrender.com`.
   Verify: open `https://calm-api.onrender.com/health` → should return `{"status":"healthy",...}`.
   API docs: `https://calm-api.onrender.com/swagger`.

---

## Step 3 — Frontend (Vercel)

1. Go to <https://vercel.com> → sign up (GitHub) → **Add New… → Project**.
2. Import `avbshecks/Cash_and_Loan_Management_FE`.
3. Framework preset auto-detects **Next.js**. Leave build settings default.
4. Add **Environment Variable**:

   | Key | Value |
   |---|---|
   | `NEXT_PUBLIC_API_URL` | `https://calm-api.onrender.com/api` *(your Render URL + `/api`)* |

5. **Deploy**. You'll get a URL like `https://calm-fe.vercel.app`.

---

## Step 4 — Connect the two (CORS)

1. Back in **Render → Environment**, set `Cors__AllowedOrigins` to your Vercel URL
   (no trailing slash), e.g. `https://calm-fe.vercel.app`.
2. Save — Render redeploys automatically.

---

## Step 5 — Test

1. Open your Vercel URL.
2. Log in with the seeded admin:
   - **Username:** `admin`
   - **Password:** `Admin@1234`
3. Create users, borrowers, loans, run the maker-checker flows.

> First load after idle may take ~50s while the Render API wakes — subsequent requests are fast.

---

## Configuration reference

The API reads all secrets from environment variables (ASP.NET Core's `__`
nesting maps to JSON sections), so nothing sensitive needs to live in the repo:

| Env var | Maps to | Purpose |
|---|---|---|
| `ConnectionStrings__DefaultConnection` | connection string | PostgreSQL (Neon) |
| `JwtSettings__Secret` | `JwtSettings:Secret` | JWT signing key |
| `Cors__AllowedOrigins` | `Cors:AllowedOrigins` | comma-separated allowed frontend origins |
| `PORT` | — | injected by Render; the app binds to it automatically |

## Local run (unchanged)

```bash
# API
cd "Cash and Loan Management" && dotnet run --project src/Api      # http://localhost:5012
# Frontend
cd cash-loan-frontend && npm run dev                                # http://localhost:3000
```

## Moving to paid (later)

- Upgrade the Render service to a paid instance → no cold starts, Hangfire jobs run reliably.
- Keep Neon (paid tier for more storage/compute) or migrate to managed Postgres.
- Point a custom domain at Vercel + Render.
