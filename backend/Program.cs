using System.Collections.Concurrent;

var builder = WebApplication.CreateBuilder(args);
var app = builder.Build();

// -----------------------------------------------------------------------------
// DATA STORE
// Using a ConcurrentDictionary to simulate a database. In a real-world Minimal API 
// without an external database, this ensures thread safety across concurrent HTTP requests.
// -----------------------------------------------------------------------------
var users = new ConcurrentDictionary<string, User>();

// Seed initial test data
users.TryAdd("1", new User("1", "Alice Smith", "alice@example.com"));
users.TryAdd("2", new User("2", "Bob Jones", "bob@example.com"));

// -----------------------------------------------------------------------------
// ZERO TRUST MIDDLEWARE
// Requirement: "The backend service... must not perform its own JWT validation."
// 
// This middleware enforces the perimeter defense. It assumes the Node.js API Gateway 
// has already verified the JWT and injected the X-Verified-User header. 
// If the header is missing, the request is treated as a direct (unauthorized) access attempt.
// 
// Note for Production: This application would also be network-isolated (e.g., inside a 
// private subnet or requiring mTLS) so it cannot be reached directly from the public internet.
// -----------------------------------------------------------------------------
app.Use(async (context, next) =>
{
    // Extract the header injected by the Gateway proxy
    if (!context.Request.Headers.TryGetValue("X-Verified-User", out var verifiedUserId) 
        || string.IsNullOrWhiteSpace(verifiedUserId))
    {
        // Fail securely: Return a clean 403 Forbidden without leaking internal stack traces
        context.Response.StatusCode = StatusCodes.Status403Forbidden;
        context.Response.ContentType = "application/json";
        await context.Response.WriteAsJsonAsync(new 
        { 
            error = "Forbidden: Direct access to the backend is blocked. All traffic must route through the API Gateway." 
        });
        
        return; // Halt the pipeline here; do not process the endpoints
    }

    // Optional: Store the verified user ID in the HttpContext for downstream endpoints to utilize
    context.Items["UserId"] = verifiedUserId.ToString();

    // Pass execution to the next middleware or endpoint handler
    await next(context);
});

// -----------------------------------------------------------------------------
// RESTful ENDPOINTS (CRUD)
// -----------------------------------------------------------------------------

// GET /api/users
// Returns the full collection of users.
app.MapGet("/api/users", () => 
    Results.Ok(users.Values));

// GET /api/users/{id}
// Looks up a specific user by ID. Returns 404 if not found.
app.MapGet("/api/users/{id}", (string id) =>
    users.TryGetValue(id, out var user) 
        ? Results.Ok(user) 
        : Results.NotFound(new { error = $"User '{id}' not found." }));

// POST /api/users
// Creates a new user. Generates a short unique ID and returns a 201 Created response.
app.MapPost("/api/users", (CreateUserDto dto) =>
{
    // Basic validation
    if (string.IsNullOrWhiteSpace(dto.Name) || string.IsNullOrWhiteSpace(dto.Email))
    {
        return Results.BadRequest(new { error = "Name and email are required." });
    }

    var newId = Guid.NewGuid().ToString("N")[..8];
    var newUser = new User(newId, dto.Name, dto.Email);
    
    users[newId] = newUser;

    // Return 201 Created with the Location header pointing to the newly created resource
    return Results.Created($"/api/users/{newId}", newUser);
});

// PUT /api/users/{id}
// Fully updates an existing user. Returns 404 if the user doesn't exist.
app.MapPut("/api/users/{id}", (string id, UpdateUserDto dto) =>
{
    if (!users.ContainsKey(id))
    {
        return Results.NotFound(new { error = $"User '{id}' not found." });
    }

    if (string.IsNullOrWhiteSpace(dto.Name) || string.IsNullOrWhiteSpace(dto.Email))
    {
        return Results.BadRequest(new { error = "Name and email are required." });
    }

    var updatedUser = new User(id, dto.Name, dto.Email);
    users[id] = updatedUser;

    return Results.Ok(updatedUser);
});

// DELETE /api/users/{id}
// Removes a user from the store. Returns 204 No Content on success.
app.MapDelete("/api/users/{id}", (string id) =>
{
    if (users.TryRemove(id, out _))
    {
        return Results.NoContent();
    }

    return Results.NotFound(new { error = $"User '{id}' not found." });
});

// -----------------------------------------------------------------------------
// APPLICATION BOOTSTRAP
// Force the backend to listen on port 3001 to align with the Gateway proxy target.
// -----------------------------------------------------------------------------
app.Run("http://localhost:3001");

// -----------------------------------------------------------------------------
// DATA TRANSFER OBJECTS & MODELS
// Utilizing C# records for concise, immutable data structures.
// -----------------------------------------------------------------------------
public record User(string Id, string Name, string Email);
public record CreateUserDto(string Name, string Email);
public record UpdateUserDto(string Name, string Email);