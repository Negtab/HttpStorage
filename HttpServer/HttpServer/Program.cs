using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Text;
using HttpServer.Models;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.EntityFrameworkCore;
using Microsoft.IdentityModel.Tokens;



var rootFolder = args.Length > 0 ? args[0] : Path.Combine(Directory.GetCurrentDirectory(), "storage");
Directory.CreateDirectory(rootFolder);

var builder = WebApplication.CreateBuilder(args);


builder.Services.AddDbContext<AppDbContext>(options =>
    options.UseNpgsql(builder.Configuration.GetConnectionString("DefaultConnection")));

var jwtKey = Encoding.UTF8.GetBytes(builder.Configuration["JwtSettings:SecretKey"]!);
builder.Services.AddAuthentication(JwtBearerDefaults.AuthenticationScheme)
    .AddJwtBearer(options =>
    {
        options.TokenValidationParameters = new TokenValidationParameters
        {
            ValidateIssuer = true,
            ValidIssuer = builder.Configuration["JwtSettings:Issuer"],
            ValidateAudience = true,
            ValidAudience = builder.Configuration["JwtSettings:Audience"],
            ValidateLifetime = true,
            IssuerSigningKey = new SymmetricSecurityKey(jwtKey)
        };
    });


builder.Services.AddAuthorization();
builder.Services.AddCors();
builder.WebHost.ConfigureKestrel(serverOptions => { serverOptions.Limits.MaxRequestBodySize = null; });

var app = builder.Build();

using (var scope = app.Services.CreateScope())
{
    var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
    db.Database.EnsureCreated();
}

app.UseCors(policy => policy.AllowAnyOrigin().AllowAnyMethod().AllowAnyHeader());
app.UseAuthentication();  
app.UseAuthorization();

// Эндпоинты авторизаци

app.MapPost("/auth/login", async (LoginRequest req, AppDbContext db) =>
{
    var user = await db.Users.FirstOrDefaultAsync(u => u.Login == req.Login);
    if (user == null)
        return Results.Unauthorized();
    
    bool valid = BCrypt.Net.BCrypt.Verify(req.Password, user.PasswordHash);
    
    if (!valid) return Results.Unauthorized();

    var token = GenerateJwtToken(req.Login, builder.Configuration);
    return Results.Ok(new { Token = token });
});

app.MapPost("/auth/register", async (RegisterRequest req, AppDbContext db) =>
{
    if (await db.Users.AnyAsync(u => u.Login == req.Login))
        return Results.Conflict("Login already exists");

    var user = new User
    {
        Login = req.Login,
        PasswordHash = BCrypt.Net.BCrypt.HashPassword(req.Password)
    };
    db.Users.Add(user);
    await db.SaveChangesAsync();
    return Results.Ok();
});

app.MapGet("/", () =>
{
    var files = Directory.GetFiles(rootFolder)
                         .Select(Path.GetFileName)
                         .Where(name => name != null)
                         .ToList();
    return Results.Json(files);
}).RequireAuthorization();

// 2. GET /{filename} - скачать файл
app.MapGet("/{filename}", (string filename) =>
{
    var safePath = GetSafePath(rootFolder, filename);
    if (safePath == null || !File.Exists(safePath))
        return Results.NotFound();
    
    var stream = File.OpenRead(safePath);
    
    return Results.File(stream, "application/octet-stream", filename);
}).RequireAuthorization();

// 3. PUT /{filename} - перезаписать или создать
app.MapPut("/{filename}", async (string filename, HttpRequest request) =>
{
    var safePath = GetSafePath(rootFolder, filename);
    if (safePath == null)
        return Results.BadRequest("Invalid filename");

    var dir = Path.GetDirectoryName(safePath);
    if (!Directory.Exists(dir))
        Directory.CreateDirectory(dir!);

    await using var stream = File.Create(safePath);
    await request.Body.CopyToAsync(stream);
    return Results.Ok();
}).RequireAuthorization();

// 4. POST /{filename}?append=true - добавить в конец файла
app.MapPost("/{filename}", async (string filename, HttpRequest request, bool append = false) =>
{
    var safePath = GetSafePath(rootFolder, filename);
    if (safePath == null)
        return Results.BadRequest("Invalid filename");

    if (!append)
        return Results.BadRequest("Use PUT for overwrite or POST with ?append=true");

    if (!File.Exists(safePath))
        return Results.NotFound();

    await using var stream = File.Open(safePath, FileMode.Append);
    await request.Body.CopyToAsync(stream);
    return Results.Ok();
}).RequireAuthorization();

// 5. DELETE /{filename} - удалить файл
app.MapDelete("/{filename}", (string filename) =>
{
    var safePath = GetSafePath(rootFolder, filename);
    if (safePath == null || !File.Exists(safePath))
        return Results.NotFound();

    File.Delete(safePath);
    return Results.Ok();
}).RequireAuthorization();

// 6. COPY – используем POST с кастомным маршрутом, т.к. COPY не поддерживается стандартными маршрутами
app.MapPost("/copy", (CopyRequest req) =>
{
    var srcPath = GetSafePath(rootFolder, req.Source);
    var destPath = GetSafePath(rootFolder, req.Destination);
    
    if (srcPath == null || destPath == null)
        return Results.BadRequest("Invalid filenames");
    
    if (!File.Exists(srcPath))
        return Results.NotFound($"Source '{req.Source}' not found");
    
    if (File.Exists(destPath))
        return Results.Conflict($"Destination '{req.Destination}' already exists");

    File.Copy(srcPath, destPath);
    return Results.Ok();
}).RequireAuthorization();

// 7. MOVE – аналогично
app.MapPost("/move", (MoveRequest req) =>
{
    var srcPath = GetSafePath(rootFolder, req.Source);
    var destPath = GetSafePath(rootFolder, req.Destination);
    if (srcPath == null || destPath == null)
        return Results.BadRequest("Invalid filenames");
    if (!File.Exists(srcPath))
        return Results.NotFound($"Source '{req.Source}' not found");
    if (File.Exists(destPath))
        return Results.Conflict($"Destination '{req.Destination}' already exists");

    File.Move(srcPath, destPath);
    return Results.Ok();
}).RequireAuthorization();

app.Urls.Add("http://*:5096");
app.Run();

static string? GetSafePath(string rootFolder, string filename)
{
    if (string.IsNullOrWhiteSpace(filename) || filename.Contains("..") || filename.Contains("/") || filename.Contains("\\"))
        return null;
    
    var invalidChars = Path.GetInvalidFileNameChars();
    if (filename.Any(c => invalidChars.Contains(c)))
        return null;
    
    var fullPath = Path.GetFullPath(Path.Combine(rootFolder, filename));
    if (!fullPath.StartsWith(rootFolder, StringComparison.OrdinalIgnoreCase))
        return null;
    
    return fullPath;
}

string GenerateJwtToken(string login, IConfiguration config)
{
    var secretKey = Encoding.UTF8.GetBytes(config["JwtSettings:SecretKey"]!);
    var securityKey = new SymmetricSecurityKey(secretKey);
    var credentials = new SigningCredentials(securityKey, SecurityAlgorithms.HmacSha256);
    var claims = new[] { new Claim(ClaimTypes.Name, login) };
    var token = new JwtSecurityToken(
        issuer: config["JwtSettings:Issuer"],
        audience: config["JwtSettings:Audience"],
        claims: claims,
        expires: DateTime.Now.AddMinutes(Convert.ToDouble(config["JwtSettings:ExpiryMinutes"])),
        signingCredentials: credentials);
    return new JwtSecurityTokenHandler().WriteToken(token);
}

public record CopyRequest(string Source, string Destination);
public record MoveRequest(string Source, string Destination);
public record LoginRequest(string Login, string Password);
public record RegisterRequest(string Login, string Password);