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

var ApiRootUrl = builder.Configuration["ApiRootUrl"];

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
UserDatabase.TryAdd("1", new User("1", "Alice Smith", "alice@example.com", true, "auth0|6ac71cc41c7eaaaaaaaa"));
UserDatabase.TryAdd("2", new User("2", "Bob Jones", "bob@example.com", true, "auth0|6ac71cc41c7e5406bbbbbbbb"));
UserDatabase.TryAdd("3", new User("3", "Timbo Jones", "tim@example.com", true, "auth0|6ac71cc41c7e5406672aa35f"));

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
    // check as a EndPointFilter so it can be applied to particular routes as required. 
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

    // -------------------------------------
    // Try to get the rich Api User object
    var externalUserId = verifiedUserId.ToString();

    //Pass 1 - ActiveApiUserCache
    var requestUser = ActiveApiUserCache.Get(externalUserId);
    if (requestUser == null)
    {
        // Pass 2 - Database/DataStore lookup
        // he lookup is based on the user's externalId, ie their Auth0 userId, that is passed through
        // from the gateway
        requestUser = UserDatabase.Where(u => u.Value.ExternalId == externalUserId).Select(u => u.Value).FirstOrDefault();
        if(requestUser == null)
        {
            // If the User is still not found in the database, return a 403 Forbidden or something appropriate
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

        // got the rich Api User from the "database", cache it for 10 minutes
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
    var usersWithoutExternalId = UserDatabase.Values
        .Where(u => u.active)
        .Select(u => new UserDto(u.Id, u.Name, u.Email, u.active));
    return Results.Ok(usersWithoutExternalId);
});

// GET /api/users/{id}
// Looks up a specific user by ID. Returns 404 if not found.
app.MapGet("/api/users/{id}", (string id) =>
{
    UserDatabase.TryGetValue(id, out var user);
    if(user is not null && user.active)
    {
        return Results.Ok(new UserDto(user.Id, user.Name, user.Email, user.active));
    }
    else
    {
        return Results.NotFound(new { error = $"User '{id}' not found." });
    }
});

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
    var newUser = new User(newId, dto.Name, dto.Email, true, "");  // default set the externalId to an empty string
    
    UserDatabase[newId] = newUser;

    // Return 201 Created with the Location header pointing to the newly created resource
    return Results.Created($"/api/users/{newId}", new UserDto(newUser.Id, newUser.Name, newUser.Email, newUser.active));
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

    // get the current user from the database
    if (!UserDatabase.TryGetValue(id, out var existingUser) || existingUser is null || !existingUser.active)
    {
        // if the user doesn't exist or is inactive, return 404
        return Results.NotFound(new { error = $"User '{id}' not found." });
    }

    // update it with the new values
    var updatedUser = existingUser with { Name = dto.Name, Email = dto.Email };
    UserDatabase[id] = updatedUser;

    return Results.Ok(new UserDto(updatedUser.Id, updatedUser.Name, updatedUser.Email, updatedUser.active));
});

// DELETE /api/users/{id}
// This LOGICALLY removes a user from the store (sets the active flag to false). Returns 204 No Content on success.
app.MapDelete("/api/users/{id}", (string id) =>
{
    // get the user from the database
    if (UserDatabase.TryGetValue(id, out var user) && user is not null && user.active)
    {
        // set the active flag to false
        var updatedUser = user with { active = false };
        UserDatabase[id] = updatedUser; 
        return Results.NoContent();
    }

    return Results.NotFound(new { error = $"User '{id}' not found." });
});

// -----------------------------------------------------------------------------
// APPLICATION BOOTSTRAP
// This url needs to match the Api url configured within the Gateway side
// -----------------------------------------------------------------------------
app.Run(ApiRootUrl);

// -----------------------------------------------------------------------------
// DATA TRANSFER OBJECTS & MODELS
// Utilizing C# records for concise, immutable data structures.
// -----------------------------------------------------------------------------
public record User(string Id, string Name, string Email, bool active, string ExternalId);
public record UserDto(string Id, string Name, string Email, bool active);  
public record CreateUserDto(string Name, string Email);
public record UpdateUserDto(string Name, string Email);