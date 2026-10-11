using System.Collections.Concurrent;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.AspNetCore.Mvc;

// -----------------------------------------------------------------------------
// BACKEND INTERNAL API layer - protected by the node Gateway - no public access
// 
// This backend service relies entirely on the Node.js API Gateway for authentication. It 
// does not do any of it's own token handling or querying of the Auth0 service.
//
// A request coming through to here SHOULD have a UserId within a custom header named "X-Verified-User".
// If the request does not have this header, it is considered a direct access attempt and MUST be rejected.
//
// Important - for this demo, authorisation is ASSUMED to be given by presence of the header. In a real system, 
// additional checks would be necessary to determine if the user is ACTUALLY authorised to access the API routes (and which ones).
//
// Also, this API should be network-isolated (e.g., inside a private subnet or requiring mTLS) so it 
// cannot be reached directly from the public internet.
// -----------------------------------------------------------------------------

var builder = WebApplication.CreateBuilder(args);
var app = builder.Build();


// -----------------------------------------------------------------------------
// DATA STORE
//
// For the purposes of the demo, the API will be using a ConcurrentDictionary to simulate a database - a simple 
// in-memory key-value store. The ConcurrentDictionary ensures thread safety across concurrent HTTP requests.
// 
// Seed initial test data for 3 users
// User 3 is the "real" Auth0 Demo User
// 
var UserDatabase = new ConcurrentDictionary<string, User>();
UserDatabase.TryAdd("1", new User("1", "Alice Smith", "alice@example.com","auth0|6ac71cc41c7eaaaaaaaa"));
UserDatabase.TryAdd("2", new User("2", "Bob Jones", "bob@example.com","auth0|6ac71cc41c7e5406bbbbbbbb"));
UserDatabase.TryAdd("3", new User("3", "Timbo Jones", "tim@example.com","auth0|6ac71cc41c7e5406672aa35f"));

// -----------------------------------------------------------------------------
// ACTIVE API USER CACHE
//
// This is used to track users who are currently active in the system and accessing the API.
// This cache would help performance by reducing the need to repeatedly query the main data store for user data.
// 
// For the purposes of the demo, this is using the builtin .Net MemCache object so the cache will not live past the 
// lifetime of the application.
//
// In a production system, either a distributed cache (e.g., Redis) or a database-backed 
// cache would be used to maintain state across application restarts, or across servers
// 
var ActiveApiUserCache = new MemoryCache(new MemoryCacheOptions());


// -----------------------------------------------------------------------------
app.Use(async (context, next) =>
{
    // The "X-Verified-User" header is expected to contain the external user ID provided by the gateway.
    //
    // Note - depending on the API's functions, it may be more appropriate to implement this 
    // particular check as a EndPointFilter so it can be applied to paricular routes as required. 
    // There may be routes that do not require this check.
    //
    if (!context.Request.Headers.TryGetValue("X-Verified-User", out var verifiedUserId) 
        || string.IsNullOrWhiteSpace(verifiedUserId))
    {
        // if the header is missing or empty, it indicates a direct access attempt
        // Fail securely: Return a clean 403 Forbidden without leaking internal stack traces
        var problem = new ProblemDetails
        {
            Status = StatusCodes.Status403Forbidden,
            Title = "Forbidden",
            Detail = "Zero Trust boundary violation: Missing verification context.",
            Type = "https://tools.ietf.org/html/rfc7231#section-6.5.3",
            Instance = context.Request.Path
        };

        context.Response.StatusCode = StatusCodes.Status403Forbidden;
        context.Response.ContentType = "application/problem+json";
        
        await context.Response.WriteAsJsonAsync(problem);
        return; // Abort the pipeline, do not call next()
    }

    // Perform a lookup of the external user ID in the cache or database as needed
    var externalUserId = verifiedUserId.ToString();
    var requestUser = ActiveApiUserCache.Get(externalUserId);
    if (requestUser == null)
    {
        // Simulate a database lookup or other retrieval mechanism
        requestUser = UserDatabase.TryGetValue(externalUserId, out var user) ? user : null;
        if(requestUser == null)
        {
            // If the user is still not found, return a 403 Forbidden
            // the alternative here is that the API creates a new user record automatically
            // but for this demo, simply return a 403.
            var problem = new ProblemDetails
            {
                Status = StatusCodes.Status403Forbidden,
                Title = "Forbidden",
                Detail = "Zero Trust boundary violation: User not found.",
                Type = "https://tools.ietf.org/html/rfc7231#section-6.5.3",
                Instance = context.Request.Path
            };

            context.Response.StatusCode = StatusCodes.Status403Forbidden;
            context.Response.ContentType = "application/problem+json";
            
            await context.Response.WriteAsJsonAsync(problem);
            return; // Abort the pipeline, do not call next()
        }

        // got the user from the "database", cache it for 10 minutes
        //
        // in a production system, this would most likely be a sliding expiration or some other caching strategy.
        //
        ActiveApiUserCache.Set(externalUserId, requestUser, TimeSpan.FromMinutes(10));
    }
    
    // Optional: Store the rich "Api User" object in the HttpContext for downstream endpoints to use
    // (downstream endpoints can also access the full "Api User" object via the ActiveApiUserCache)
    context.Items["ApiUser"] = requestUser;

    // Pass execution to the next middleware or endpoint handler
    await next(context);
});

// -----------------------------------------------------------------------------
// RESTful ENDPOINTS (CRUD)
// -----------------------------------------------------------------------------

// GET /api/users
// Returns the full collection of users, without the externalId.
app.MapGet("/api/users", () => 
{
    var usersWithoutExternalId = UserDatabase.Values.Select(u => new UserDto(u.Id, u.Name, u.Email));
    return Results.Ok(usersWithoutExternalId);
});

// GET /api/users/{id}
// Looks up a specific user by ID. Returns 404 if not found.
app.MapGet("/api/users/{id}", (string id) =>
    UserDatabase.TryGetValue(id, out var user) 
        ? Results.Ok(new UserDto(user.Id, user.Name, user.Email)) 
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
    var newUser = new User(newId, dto.Name, dto.Email, "");  // default set the externalId to an empty string
    
    UserDatabase[newId] = newUser;

    // Return 201 Created with the Location header pointing to the newly created resource
    return Results.Created($"/api/users/{newId}", new UserDto(newUser.Id, newUser.Name, newUser.Email));
});

// PUT /api/users/{id}
// Fully updates an existing user. Returns 404 if the user doesn't exist.
app.MapPut("/api/users/{id}", (string id, UpdateUserDto dto) =>
{
    // check the inputs
    if (string.IsNullOrWhiteSpace(dto.Name) || string.IsNullOrWhiteSpace(dto.Email))
    {
        return Results.BadRequest(new { error = "Name and email are required." });
    }

    // get the current user from the database and update it with the new values
    if (!UserDatabase.TryGetValue(id, out var existingUser) || existingUser is null)
    {
        return Results.NotFound(new { error = $"User '{id}' not found." });
    }
    var updatedUser = existingUser with { Name = dto.Name, Email = dto.Email };
    UserDatabase[id] = updatedUser;

    return Results.Ok(new UserDto(updatedUser.Id, updatedUser.Name, updatedUser.Email));
});

// DELETE /api/users/{id}
// Removes a user from the store. Returns 204 No Content on success.
app.MapDelete("/api/users/{id}", (string id) =>
{
    if (UserDatabase.TryRemove(id, out _))
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
public record User(string Id, string Name, string Email, string ExternalId);
public record UserDto(string Id, string Name, string Email);  
public record CreateUserDto(string Name, string Email);
public record UpdateUserDto(string Name, string Email);