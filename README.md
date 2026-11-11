# School Admission System

.NET 8 minimal API + PostgreSQL + React/Vite + Docker. A time-limited, first-come-first-served admission system with eight fixed endpoints, safe seat allocation and a no-login student interface.

## Quick start (Docker only)

```bash
docker compose up --build
```

First startup downloads the base images and builds the application. PostgreSQL healthcheck runs before the backend starts.

- Frontend: http://localhost:5173
- Backend: http://localhost:8080
- Backend health check: http://localhost:8080/health

Start a class using the **Create Class (Admin)** tab; enter a 17-digit student birth registration number at the top; select **Class List → Apply Now**, then visit **Application** or **My Applications** to confirm or cancel. The **Pay Admission Fee (Demo)** button simulates successful payment (no payment gateway).

## Run automated backend integration tests

With `docker compose up --build -d` running:

```bash
dotnet run --project tests/SchoolAdmission.Tests.csproj
```

This runs five automated HTTP tests including **25 concurrent applications for one seat**. A failure makes the process exit non-zero. The test suite creates disposable classes for its tests; it does not delete existing admissions.

## Stop and restart without losing data

```bash
docker compose down
docker compose up --build -d
```

The `postgres_data` named volume persists. Do not use `docker compose down -v` unless you deliberately want to erase the database.

## Environment configuration

- `HOLD_DURATION_SECONDS` (default `600`): hold expiry, used by the backend.
- `POSTGRES_PASSWORD` (default local demo value): database credentials wired into Docker Compose.
- `DATABASE_URL`: PostgreSQL connection string consumed by backend (set by Docker Compose).
- `VITE_API_URL` (default `http://localhost:8080`): base URL for React API calls. Set this in the Docker Compose environment for the frontend or in a local `frontend/.env` file during local development; restart the frontend after changing it.
- `API_URL` (default `http://localhost:8080`): endpoint for integration tests.

Use production-strength secrets and authentication in any actual deployment; this is only a take-home exercise.

## API contract

Every endpoint below returns JSON except `DELETE /holds/{holdId}` which returns HTTP 204 without a body.

| Method | Path | Description |
|---|---|---|
| POST | `/classes` | JSON `{ "name": "Class 6 - Morning Shift", "totalSeats": 120 }`; 201 `{id,name,totalSeats}` |
| GET | `/classes` | Classes with `heldSeats`, `confirmedSeats`, `availableSeats` |
| GET | `/classes/{classId}` | One class plus seat counts |
| POST | `/classes/{classId}/holds` | `X-Student-Id` required; 201 `{holdId,classId,studentId,status,expiresAt}`; 409 if full/duplicate |
| GET | `/holds/{holdId}` | `{holdId,classId,status,expiresAt}` |
| POST | `/holds/{holdId}/confirm` | `X-Student-Id` required; 200 `{holdId,status}` |
| DELETE | `/holds/{holdId}` | `X-Student-Id` required; 204 on success |
| GET | `/me/holds` | `X-Student-Id` required; list with `{holdId,classId,className,status,expiresAt}` |

## Seat correctness and persistence

Every seat reservation transaction locks the relevant PostgreSQL `classes` row with `SELECT ... FOR UPDATE`, checks the active count and the student's existing class application, and inserts the hold before committing. This serializes capacity checks across concurrent backend processes using one PostgreSQL database. Counts consider only HELD entries whose `expires_at > NOW()` and all CONFIRMED entries, so a hold expires without a cleanup API call, including after a container restart. Confirmation and cancellation lock the application row to prevent concurrent state conflicts. Repeated confirmation by the owner is idempotent.

A deliberate assignment assumption is that first come first served corresponds to successfully acquiring the class row lock, not an independently audited arrival-time queue. See [ASSUMPTIONS.md](ASSUMPTIONS.md) for other choices and [AI_USAGE.md](AI_USAGE.md) for AI disclosure.

## Local developer run (optional)

Start PostgreSQL using `docker compose up -d db`, then in one PowerShell terminal:

```powershell
cd backend
dotnet restore
dotnet run
```

In another terminal:

```powershell
cd frontend
npm.cmd install
npm.cmd run dev
```

Backend listens on port 8080. Frontend on 5173. The Docker command is the intended evaluation path.
