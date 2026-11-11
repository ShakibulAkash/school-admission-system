using System.Net;
using System.Net.Http.Json;
using System.Text.Json;

var baseUrl = Environment.GetEnvironmentVariable("API_URL") ?? "http://localhost:8080";
using var client = new HttpClient { BaseAddress = new Uri(baseUrl) };
int passed = 0;

async Task<JsonDocument> ReadJson(HttpResponseMessage response)
{
    var text = await response.Content.ReadAsStringAsync();
    if (!response.IsSuccessStatusCode) throw new Exception($"HTTP {(int)response.StatusCode}: {text}");
    return JsonDocument.Parse(text);
}
async Task<string> NewClass(int seats)
{
    var r = await client.PostAsJsonAsync("/classes", new { name = $"Test Class {Guid.NewGuid():N}", totalSeats = seats });
    Assert(r.StatusCode == HttpStatusCode.Created, "New class HTTP 201");
    using var json = await ReadJson(r);
    return json.RootElement.GetProperty("id").GetString()!;
}
async Task<HttpResponseMessage> Hold(string classId, string studentId)
{
    using var msg = new HttpRequestMessage(HttpMethod.Post, $"/classes/{classId}/holds");
    msg.Headers.TryAddWithoutValidation("X-Student-Id", studentId);
    return await client.SendAsync(msg);
}
async Task<HttpResponseMessage> StudentAction(HttpMethod method, string url, string studentId)
{
    using var msg = new HttpRequestMessage(method, url);
    msg.Headers.TryAddWithoutValidation("X-Student-Id", studentId);
    return await client.SendAsync(msg);
}
void Assert(bool condition, string name)
{
    if (!condition) throw new Exception("FAILED: " + name);
}
async Task Test(string name, Func<Task> action)
{
    Console.WriteLine("RUN " + name);
    await action(); passed++;
    Console.WriteLine("PASS " + name);
}

try
{
    await Test("Create class and verify available seat count", async () =>
    {
        string id = await NewClass(3);
        using var r = await client.GetAsync("/classes/" + id);
        using var json = await ReadJson(r);
        Assert(json.RootElement.GetProperty("totalSeats").GetInt32() == 3, "Total 3");
        Assert(json.RootElement.GetProperty("availableSeats").GetInt32() == 3, "Available 3");
    });

    await Test("Duplicate student hold is rejected", async () =>
    {
        string id = await NewClass(2);
        const string student = "20150000000000001";
        using var first = await Hold(id, student);
        Assert(first.StatusCode == HttpStatusCode.Created, "First hold created");
        using var duplicate = await Hold(id, student);
        Assert(duplicate.StatusCode == HttpStatusCode.Conflict, "Duplicate returns 409");
    });

    await Test("Cancel immediately releases a seat", async () =>
    {
        string id = await NewClass(1);
        const string student = "20150000000000002";
        using var first = await Hold(id, student);
        using var data = await ReadJson(first);
        string holdId = data.RootElement.GetProperty("holdId").GetString()!;
        using var cancel = await StudentAction(HttpMethod.Delete, "/holds/" + holdId, student);
        Assert(cancel.StatusCode == HttpStatusCode.NoContent, "Cancel returns 204");
        using var seats = await client.GetAsync("/classes/" + id);
        using var json = await ReadJson(seats);
        Assert(json.RootElement.GetProperty("availableSeats").GetInt32() == 1, "Seat returned");
    });

    await Test("Confirm is owner-only and idempotent", async () =>
    {
        string id = await NewClass(1);
        const string owner = "20150000000000003", stranger = "20150000000000004";
        using var first = await Hold(id, owner);
        using var data = await ReadJson(first);
        string holdId = data.RootElement.GetProperty("holdId").GetString()!;
        using var rejected = await StudentAction(HttpMethod.Post, "/holds/" + holdId + "/confirm", stranger);
        Assert(rejected.StatusCode == HttpStatusCode.Forbidden, "Stranger forbidden");
        using var confirmed = await StudentAction(HttpMethod.Post, "/holds/" + holdId + "/confirm", owner);
        Assert(confirmed.StatusCode == HttpStatusCode.OK, "Owner confirmed");
        using var again = await StudentAction(HttpMethod.Post, "/holds/" + holdId + "/confirm", owner);
        Assert(again.StatusCode == HttpStatusCode.OK, "Repeat idempotent");
        using var seats = await client.GetAsync("/classes/" + id);
        using var json = await ReadJson(seats);
        Assert(json.RootElement.GetProperty("confirmedSeats").GetInt32() == 1, "Confirmed exactly one");
        Assert(json.RootElement.GetProperty("heldSeats").GetInt32() == 0, "Held zero");
    });

    await Test("25 simultaneous requests for 1 seat never oversell", async () =>
    {
        string id = await NewClass(1);
        var attempts = Enumerable.Range(1, 25).Select(i => Hold(id, "2015" + i.ToString("D13"))).ToArray();
        var responses = await Task.WhenAll(attempts);
        try
        {
            int created = responses.Count(r => r.StatusCode == HttpStatusCode.Created);
            Assert(created == 1, $"Exactly one 201, found {created}");
            Assert(responses.All(r => r.StatusCode is HttpStatusCode.Created or HttpStatusCode.Conflict), "All others 409");
            using var seats = await client.GetAsync("/classes/" + id);
            using var json = await ReadJson(seats);
            Assert(json.RootElement.GetProperty("heldSeats").GetInt32() == 1, "One held seat");
            Assert(json.RootElement.GetProperty("availableSeats").GetInt32() == 0, "Zero seats left");
        }
        finally {foreach(var r in responses) r.Dispose();}
    });

    Console.WriteLine($"SUCCESS: {passed}/5 backend integration tests passed.");
}
catch (Exception ex)
{
    Console.Error.WriteLine(ex.Message);
    Environment.ExitCode = 1;
}
