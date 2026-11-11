using Npgsql;
using System.Text.RegularExpressions;

var builder = WebApplication.CreateBuilder(args);
builder.WebHost.UseUrls(Environment.GetEnvironmentVariable("ASPNETCORE_URLS") ?? "http://0.0.0.0:8080");
builder.Services.AddCors(options => options.AddDefaultPolicy(p => p.AllowAnyOrigin().AllowAnyHeader().AllowAnyMethod()));
var connectionString = Environment.GetEnvironmentVariable("DATABASE_URL")
    ?? "Host=localhost;Port=5432;Database=school_admission;Username=school_user;Password=local_dev_password";
builder.Services.AddSingleton(NpgsqlDataSource.Create(connectionString));
var app = builder.Build();
app.UseCors();

// Store records in PostgreSQL, never in process memory.
await using (var conn = await app.Services.GetRequiredService<NpgsqlDataSource>().OpenConnectionAsync())
await using (var init = new NpgsqlCommand("""
    CREATE TABLE IF NOT EXISTS classes (
      id TEXT PRIMARY KEY, name TEXT NOT NULL, total_seats INT NOT NULL CHECK (total_seats > 0)
    );
    CREATE TABLE IF NOT EXISTS holds (
      id TEXT PRIMARY KEY, class_id TEXT NOT NULL REFERENCES classes(id),
      student_id VARCHAR(17) NOT NULL, status TEXT NOT NULL
      CHECK (status IN ('HELD','CONFIRMED','CANCELLED')),
      expires_at TIMESTAMPTZ NOT NULL, created_at TIMESTAMPTZ NOT NULL DEFAULT NOW()
    );
    CREATE INDEX IF NOT EXISTS ix_holds_class ON holds(class_id);
    CREATE INDEX IF NOT EXISTS ix_holds_student ON holds(student_id);
    """, conn))
    await init.ExecuteNonQueryAsync();

int duration = int.TryParse(Environment.GetEnvironmentVariable("HOLD_DURATION_SECONDS"), out int seconds)
    && seconds > 0 ? seconds : 600;

app.MapGet("/health", () => Results.Ok(new { status = "ok" }));

app.MapPost("/classes", async (ClassRequest request, NpgsqlDataSource db) =>
{
    if (string.IsNullOrWhiteSpace(request.Name) || request.Name.Length > 120 || request.TotalSeats <= 0)
        return Results.BadRequest(new { error = "Provide a class name (1-120 characters) and positive totalSeats." });
    string id = $"c_{Guid.NewGuid():N}";
    await using var cn = await db.OpenConnectionAsync();
    await using var cmd = new NpgsqlCommand("INSERT INTO classes(id,name,total_seats) VALUES(@id,@name,@seats)", cn);
    cmd.Parameters.AddWithValue("id", id);
    cmd.Parameters.AddWithValue("name", request.Name.Trim());
    cmd.Parameters.AddWithValue("seats", request.TotalSeats);
    await cmd.ExecuteNonQueryAsync();
    return Results.Created($"/classes/{id}", new { id, name = request.Name.Trim(), totalSeats = request.TotalSeats });
});

const string ClassSeatsSql = """
    SELECT c.id,c.name,c.total_seats,
      COUNT(h.id) FILTER (WHERE h.status='HELD' AND h.expires_at > NOW()) AS held,
      COUNT(h.id) FILTER (WHERE h.status='CONFIRMED') AS confirmed
    FROM classes c LEFT JOIN holds h ON h.class_id=c.id
    """;

app.MapGet("/classes", async (NpgsqlDataSource db) =>
{
    var items = new List<object>();
    await using var cn = await db.OpenConnectionAsync();
    await using var cmd = new NpgsqlCommand(ClassSeatsSql + " GROUP BY c.id,c.name,c.total_seats ORDER BY c.name", cn);
    await using var rd = await cmd.ExecuteReaderAsync();
    while (await rd.ReadAsync()) items.Add(ClassView(rd));
    return Results.Ok(items);
});

app.MapGet("/classes/{classId}", async (string classId, NpgsqlDataSource db) =>
{
    await using var cn = await db.OpenConnectionAsync();
    await using var cmd = new NpgsqlCommand(ClassSeatsSql + " WHERE c.id=@id GROUP BY c.id,c.name,c.total_seats", cn);
    cmd.Parameters.AddWithValue("id", classId);
    await using var rd = await cmd.ExecuteReaderAsync();
    return await rd.ReadAsync() ? Results.Ok(ClassView(rd)) : Results.NotFound(new { error = "Class not found." });
});

app.MapPost("/classes/{classId}/holds", async (string classId, HttpRequest req, NpgsqlDataSource db) =>
{
    var student = StudentId(req);
    if (student == null) return Results.BadRequest(new { error = "X-Student-Id must be a 17-digit birth registration number." });
    await using var cn = await db.OpenConnectionAsync();
    await using var tx = await cn.BeginTransactionAsync();
    // All applications for one class are serialized by the class row lock.
    await using var lockCmd = new NpgsqlCommand("SELECT total_seats FROM classes WHERE id=@id FOR UPDATE", cn, tx);
    lockCmd.Parameters.AddWithValue("id", classId);
    var capacityObj = await lockCmd.ExecuteScalarAsync();
    if (capacityObj == null) return Results.NotFound(new { error = "Class not found." });
    int capacity = (int)capacityObj;

    await using var dup = new NpgsqlCommand("""
        SELECT COUNT(*) FROM holds WHERE class_id=@id AND student_id=@student
        AND (status='CONFIRMED' OR (status='HELD' AND expires_at > NOW()))
        """, cn, tx);
    dup.Parameters.AddWithValue("id", classId);
    dup.Parameters.AddWithValue("student", student);
    if ((long)(await dup.ExecuteScalarAsync())! > 0)
        return Results.Conflict(new { error = "You already have an active application for this class." });

    await using var count = new NpgsqlCommand("""
        SELECT COUNT(*) FROM holds WHERE class_id=@id
        AND (status='CONFIRMED' OR (status='HELD' AND expires_at > NOW()))
        """, cn, tx);
    count.Parameters.AddWithValue("id", classId);
    if ((long)(await count.ExecuteScalarAsync())! >= capacity)
        return Results.Conflict(new { error = "No seats available." });

    var id = $"h_{Guid.NewGuid():N}";
    var expiresAt = DateTime.UtcNow.AddSeconds(duration);
    await using var insert = new NpgsqlCommand("""
        INSERT INTO holds(id,class_id,student_id,status,expires_at)
        VALUES(@id,@class,@student,'HELD',@expiry)
        """, cn, tx);
    insert.Parameters.AddWithValue("id", id);
    insert.Parameters.AddWithValue("class", classId);
    insert.Parameters.AddWithValue("student", student);
    insert.Parameters.AddWithValue("expiry", expiresAt);
    await insert.ExecuteNonQueryAsync();
    await tx.CommitAsync();
    return Results.Created($"/holds/{id}", new { holdId = id, classId, studentId = student, status = "HELD", expiresAt });
});

app.MapGet("/holds/{holdId}", async (string holdId, NpgsqlDataSource db) =>
{
    await using var cn = await db.OpenConnectionAsync();
    await using var cmd = new NpgsqlCommand("""
        SELECT id,class_id,
          CASE WHEN status='HELD' AND expires_at <= NOW() THEN 'EXPIRED' ELSE status END,
          expires_at FROM holds WHERE id=@id
        """, cn);
    cmd.Parameters.AddWithValue("id", holdId);
    await using var rd = await cmd.ExecuteReaderAsync();
    if (!await rd.ReadAsync()) return Results.NotFound(new { error = "Application not found." });
    return Results.Ok(new { holdId = rd.GetString(0), classId = rd.GetString(1),
        status = rd.GetString(2), expiresAt = rd.GetDateTime(3) });
});

app.MapPost("/holds/{holdId}/confirm", async (string holdId, HttpRequest req, NpgsqlDataSource db) =>
{
    var student = StudentId(req);
    if (student == null) return Results.BadRequest(new { error = "Valid X-Student-Id required." });
    await using var cn = await db.OpenConnectionAsync();
    await using var tx = await cn.BeginTransactionAsync();
    await using var get = new NpgsqlCommand("SELECT student_id,status,expires_at FROM holds WHERE id=@id FOR UPDATE", cn, tx);
    get.Parameters.AddWithValue("id", holdId);
    string owner, status;
    DateTime expires;
    await using (var rd = await get.ExecuteReaderAsync())
    {
        if (!await rd.ReadAsync()) return Results.NotFound(new { error = "Application not found." });
        owner = rd.GetString(0); status = rd.GetString(1); expires = rd.GetDateTime(2);
    }
    if (owner != student) return Results.Json(new { error = "This application belongs to another student." }, statusCode: 403);
    if (status == "CONFIRMED") return Results.Ok(new { holdId, status = "CONFIRMED" });
    if (status != "HELD" || expires <= DateTime.UtcNow)
        return Results.Conflict(new { error = "Application is cancelled or expired." });
    await using var update = new NpgsqlCommand("UPDATE holds SET status='CONFIRMED' WHERE id=@id", cn, tx);
    update.Parameters.AddWithValue("id", holdId);
    await update.ExecuteNonQueryAsync();
    await tx.CommitAsync();
    return Results.Ok(new { holdId, status = "CONFIRMED" });
});

app.MapDelete("/holds/{holdId}", async (string holdId, HttpRequest req, NpgsqlDataSource db) =>
{
    var student = StudentId(req);
    if (student == null) return Results.BadRequest(new { error = "Valid X-Student-Id required." });
    await using var cn = await db.OpenConnectionAsync();
    await using var tx = await cn.BeginTransactionAsync();
    await using var get = new NpgsqlCommand("SELECT student_id,status FROM holds WHERE id=@id FOR UPDATE", cn, tx);
    get.Parameters.AddWithValue("id", holdId);
    string owner, status;
    await using (var rd = await get.ExecuteReaderAsync())
    {
        if (!await rd.ReadAsync()) return Results.NotFound(new { error = "Application not found." });
        owner = rd.GetString(0); status = rd.GetString(1);
    }
    if (owner != student) return Results.Json(new { error = "This application belongs to another student." }, statusCode: 403);
    if (status == "CONFIRMED") return Results.Conflict(new { error = "Confirmed admission cannot be cancelled." });
    await using var update = new NpgsqlCommand("UPDATE holds SET status='CANCELLED' WHERE id=@id AND status='HELD'", cn, tx);
    update.Parameters.AddWithValue("id", holdId);
    await update.ExecuteNonQueryAsync();
    await tx.CommitAsync();
    return Results.NoContent();
});

app.MapGet("/me/holds", async (HttpRequest req, NpgsqlDataSource db) =>
{
    var student = StudentId(req);
    if (student == null) return Results.BadRequest(new { error = "Valid X-Student-Id required." });
    var items = new List<object>();
    await using var cn = await db.OpenConnectionAsync();
    await using var cmd = new NpgsqlCommand("""
        SELECT h.id,h.class_id,c.name,
          CASE WHEN h.status='HELD' AND h.expires_at <= NOW() THEN 'EXPIRED' ELSE h.status END,
          h.expires_at FROM holds h JOIN classes c ON c.id=h.class_id
        WHERE h.student_id=@student ORDER BY h.created_at DESC
        """, cn);
    cmd.Parameters.AddWithValue("student", student);
    await using var rd = await cmd.ExecuteReaderAsync();
    while (await rd.ReadAsync()) items.Add(new { holdId = rd.GetString(0), classId = rd.GetString(1),
        className = rd.GetString(2), status = rd.GetString(3), expiresAt = rd.GetDateTime(4) });
    return Results.Ok(items);
});

app.Run();

static string? StudentId(HttpRequest req)
{
    var id = req.Headers["X-Student-Id"].ToString().Trim();
    return Regex.IsMatch(id, @"^\d{17}$") ? id : null;
}

static object ClassView(NpgsqlDataReader rd)
{
    var total = rd.GetInt32(2);
    var held = (int)rd.GetInt64(3);
    var confirmed = (int)rd.GetInt64(4);
    return new { id = rd.GetString(0), name = rd.GetString(1), totalSeats = total,
        heldSeats = held, confirmedSeats = confirmed, availableSeats = total - held - confirmed };
}

record ClassRequest(string Name, int TotalSeats);
