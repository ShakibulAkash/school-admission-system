# Assumptions and rationale

1. A separate admission **class** is defined for every class/shift combination. `Class 6 - Morning Shift` and `Class 6 - Day Shift` have separate capacities.
2. Login is not required; the UI takes a 17-digit birth registration number and sends it in `X-Student-Id`. This is **not secure authentication**; a production service needs identity verification. The admin page is public only because the assignment says no login.
3. Class names must be nonblank and at most 120 characters; `totalSeats` must be a positive integer.
4. A student can have at most one active (unexpired HELD or CONFIRMED) application per class. New duplicate applications return HTTP 409. Expired/cancelled ones remain as history but do not block reapplication.
5. A hold lasts `HOLD_DURATION_SECONDS`, default 600. PostgreSQL timestamps are stored in UTC. Seat availability excludes expired HELD applications based on database time (no background job or additional API call needed); API views label them EXPIRED.
6. Confirmed seats remain confirmed past the hold expiry timestamp. Pressing the fee button simulates a successful payment, as specified; there is no payment gateway.
7. Confirmation by the same student is **idempotent**: a second request returns CONFIRMED without counting twice. A different student cannot confirm or cancel another student's application (HTTP 403).
8. A confirmed admission cannot be cancelled with the demo DELETE API. Cancelling a HELD application (including one already expired) is allowed and returns 204; cancelling an already CANCELLED application is idempotent.
9. First come first served is implemented as the order of acquiring a PostgreSQL `SELECT ... FOR UPDATE` class-row lock. Thus even simultaneous requests and multiple backend processes cannot exceed the class capacity, though network-arrival ordering itself cannot be guaranteed.
10. A class's counts are `heldSeats` = unexpired HELD, `confirmedSeats` = CONFIRMED, `availableSeats` = total - held - confirmed. React polls every four seconds and updates expiry countdown every second.
11. `GET /holds/{holdId}` has no student header because the fixed contract does not require one, and it returns only status, class ID and expiry.
12. Frontend persists the input student ID in this browser's `localStorage` for convenience. The authoritative application data is PostgreSQL and survives Docker restart or `docker compose down` due to the named volume. No persistent secrets are stored in localStorage.
13. All API responses are JSON except HTTP 204 DELETE; errors use human-readable `{ "error": "..." }` JSON or a 403/404 status as specified by the chosen behavior.
