using Microsoft.AspNetCore.WebUtilities;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using System.Buffers.Binary;
using System.Formats.Cbor;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;

var builder = WebApplication.CreateBuilder(args);

// ── rpOrigin injected by Aspire; used to validate WebAuthn clientData.origin ──
var rpOrigin = (builder.Configuration["WebAuthn:Origins"] ?? "https://localhost")
    .Split(',')[0].Trim();

const string RpId   = "localhost";
const string RpName = "Fingerprint Demo";

// ── Services ──────────────────────────────────────────────────────────────────
builder.Services.AddCors(options =>
    options.AddDefaultPolicy(p => p.AllowAnyOrigin().AllowAnyMethod().AllowAnyHeader()));

builder.Services.AddMemoryCache();

var connectionString = builder.Configuration.GetConnectionString("fingerprint-db")
    ?? throw new InvalidOperationException("Connection string 'fingerprint-db' not found.");

builder.Services.AddDbContext<FidoDbContext>(opts => opts.UseNpgsql(connectionString));

var app = builder.Build();

app.UseCors();

// ── Bootstrap DB ──────────────────────────────────────────────────────────────
using (var scope = app.Services.CreateScope())
{
    var db = scope.ServiceProvider.GetRequiredService<FidoDbContext>();
    await db.Database.EnsureCreatedAsync();

    // Idempotent: create AttendanceEntries if an older DB exists without it
    await db.Database.ExecuteSqlRawAsync(@"
        CREATE TABLE IF NOT EXISTS ""AttendanceEntries"" (
            ""Id""        uuid        NOT NULL PRIMARY KEY,
            ""UserId""    uuid        NOT NULL REFERENCES ""Users""(""Id"") ON DELETE CASCADE,
            ""Type""      integer     NOT NULL,
            ""Timestamp"" timestamptz NOT NULL
        );
        CREATE INDEX IF NOT EXISTS ""IX_AttendanceEntries_UserId_Timestamp""
            ON ""AttendanceEntries""(""UserId"", ""Timestamp"");
    ");
}

// ── JSON options (camelCase + skip null for WebAuthn spec compliance) ─────────
var camelCase = new JsonSerializerOptions
{
    PropertyNamingPolicy      = JsonNamingPolicy.CamelCase,
    DefaultIgnoreCondition    = JsonIgnoreCondition.WhenWritingNull
};

// ── GET /api/users ────────────────────────────────────────────────────────────
app.MapGet("/api/users", async (FidoDbContext db) =>
    await db.Users
            .OrderBy(u => u.CreatedAt)
            .Select(u => new { u.Id, u.Username, u.CreatedAt, CredentialCount = u.Credentials.Count })
            .ToListAsync());

// ── POST /api/webauthn/register/options ───────────────────────────────────────
app.MapPost("/api/webauthn/register/options", async (
    RegisterOptionsRequest req,
    FidoDbContext db,
    IMemoryCache cache) =>
{
    if (string.IsNullOrWhiteSpace(req.Username))
        return Results.BadRequest("Username is required.");

    var username   = req.Username.Trim().ToLowerInvariant();
    var challenge  = RandomNumberGenerator.GetBytes(32);
    var userHandle = await db.Users
                             .Where(u => u.Username == username)
                             .Select(u => u.UserHandle)
                             .FirstOrDefaultAsync()
                 ?? RandomNumberGenerator.GetBytes(32);

    var excludeIds = await db.Credentials
                             .Where(c => c.User.Username == username)
                             .Select(c => c.CredentialId)
                             .ToListAsync();

    var options = new RegOptions(
        new RpInfo(RpId, RpName),
        new UserInfo(WebEncoders.Base64UrlEncode(userHandle), username, username),
        WebEncoders.Base64UrlEncode(challenge),
        [new PubKeyParam("public-key", -7)],
        60_000,
        excludeIds.Select(id => new AllowCred("public-key", WebEncoders.Base64UrlEncode(id))).ToArray(),
        // No authenticatorAttachment → any authenticator is accepted:
    //   - Staff using their own phone via QR code (cross-device) ← ideal for multi-person kiosk
    //   - Hardware security keys (YubiKey etc.)
    //   - Personal laptop / phone Touch ID / Face ID
    // residentKey "required" → passkey is stored as discoverable credential
    //   → enables the usernameless "just touch fingerprint" flow at reception
    new AuthSelCriteria(null, false, "required", "required"),
        "none");

    var sessionKey = Guid.NewGuid().ToString("N");
    cache.Set($"reg:{sessionKey}", (username, userHandle, challenge), TimeSpan.FromMinutes(5));

    return Results.Ok(new { sessionKey, optionsJson = JsonSerializer.Serialize(options, camelCase) });
});

// ── POST /api/webauthn/register/complete ──────────────────────────────────────
app.MapPost("/api/webauthn/register/complete", async (
    RegisterCompleteRequest req,
    FidoDbContext db,
    IMemoryCache cache) =>
{
    if (!cache.TryGetValue<(string, byte[], byte[])>($"reg:{req.SessionKey}", out var cached))
        return Results.BadRequest("Session expired. Please start registration again.");

    cache.Remove($"reg:{req.SessionKey}");
    var (username, userHandle, challenge) = cached;

    AttestationReq attestation;
    try { attestation = JsonSerializer.Deserialize<AttestationReq>(req.AttestationResponse, camelCase)!; }
    catch { return Results.BadRequest("Invalid attestation format."); }

    RegistrationResult regResult;
    try   { regResult = WebAuthnHelper.VerifyRegistration(attestation, challenge, rpOrigin, RpId); }
    catch (Exception ex) { return Results.BadRequest($"Registration failed: {ex.Message}"); }

    var user = await db.Users.FirstOrDefaultAsync(u => u.Username == username);
    if (user == null)
    {
        user = new FidoUser { Id = Guid.NewGuid(), Username = username, UserHandle = userHandle, CreatedAt = DateTime.UtcNow };
        db.Users.Add(user);
        await db.SaveChangesAsync();
    }

    if (await db.Credentials.AnyAsync(c => c.CredentialId == regResult.CredentialId))
        return Results.Conflict("Credential already registered.");

    db.Credentials.Add(new FidoCredential
    {
        CredentialId    = regResult.CredentialId,
        PublicKey       = regResult.PublicKey,
        UserHandle      = userHandle,
        SignatureCounter = 0,
        CreatedAt       = DateTime.UtcNow,
        UserId          = user.Id
    });
    await db.SaveChangesAsync();

    return Results.Ok(new { message = $"Passkey registered for '{username}'." });
});

// ── POST /api/webauthn/authenticate/options ───────────────────────────────────
app.MapPost("/api/webauthn/authenticate/options", async (
    AuthOptionsRequest req,
    FidoDbContext db,
    IMemoryCache cache) =>
{
    AllowCred[] allowedCreds;
    var username = req.Username?.Trim().ToLowerInvariant() ?? "";

    if (!string.IsNullOrEmpty(username))
    {
        // Named flow: only offer credentials belonging to the given user
        var credentialIds = await db.Credentials
                                    .Where(c => c.User.Username == username)
                                    .Select(c => c.CredentialId)
                                    .ToListAsync();

        if (credentialIds.Count == 0)
            return Results.NotFound($"No registered passkeys found for '{username}'.");

        allowedCreds = credentialIds
            .Select(id => new AllowCred("public-key", WebEncoders.Base64UrlEncode(id)))
            .ToArray();
    }
    else
    {
        // Discoverable (usernameless) flow: empty list → browser picks from all stored passkeys
        allowedCreds = [];
    }

    var challenge = RandomNumberGenerator.GetBytes(32);
    var options   = new AuthOptions(
        WebEncoders.Base64UrlEncode(challenge),
        60_000,
        RpId,
        allowedCreds,
        "required");

    var sessionKey = Guid.NewGuid().ToString("N");
    // Store username as empty string for the discoverable flow — resolved in /complete
    cache.Set($"auth:{sessionKey}", (username, challenge), TimeSpan.FromMinutes(5));

    return Results.Ok(new { sessionKey, optionsJson = JsonSerializer.Serialize(options, camelCase) });
});

// ── POST /api/webauthn/authenticate/complete ──────────────────────────────────
app.MapPost("/api/webauthn/authenticate/complete", async (
    AuthCompleteRequest req,
    FidoDbContext db,
    IMemoryCache cache) =>
{
    if (!cache.TryGetValue<(string, byte[])>($"auth:{req.SessionKey}", out var cached))
        return Results.BadRequest("Session expired. Please start authentication again.");

    cache.Remove($"auth:{req.SessionKey}");
    var (cachedUsername, challenge) = cached;

    AssertionReq assertion;
    try { assertion = JsonSerializer.Deserialize<AssertionReq>(req.AssertionResponse, camelCase)!; }
    catch { return Results.BadRequest("Invalid assertion format."); }

    var rawCredId = WebEncoders.Base64UrlDecode(assertion.RawId);
    var cred = await db.Credentials
                       .Include(c => c.User)
                       .FirstOrDefaultAsync(c => c.CredentialId == rawCredId);

    if (cred == null) return Results.NotFound("Credential not found.");

    // Named flow: make sure the credential actually belongs to the requested user
    if (!string.IsNullOrEmpty(cachedUsername) && cred.User.Username != cachedUsername)
        return Results.BadRequest("Credential does not belong to the specified user.");

    uint newCounter;
    try   { newCounter = WebAuthnHelper.VerifyAuthentication(assertion, challenge, rpOrigin, RpId, cred.PublicKey, cred.SignatureCounter); }
    catch (Exception ex) { return Results.BadRequest($"Authentication failed: {ex.Message}"); }

    // ── Attendance: toggle check-in / check-out ───────────────────────────────
    var todayUtc   = DateTime.UtcNow.Date;
    var lastEntry  = await db.AttendanceEntries
                             .Where(e => e.UserId == cred.User.Id && e.Timestamp >= todayUtc)
                             .OrderByDescending(e => e.Timestamp)
                             .FirstOrDefaultAsync();

    // First entry of the day OR previous was a check-out → check-in; otherwise → check-out
    var entryType = (lastEntry is null || lastEntry.Type == EntryType.CheckOut)
                    ? EntryType.CheckIn
                    : EntryType.CheckOut;

    var now = DateTime.UtcNow;
    db.AttendanceEntries.Add(new AttendanceEntry
    {
        Id        = Guid.NewGuid(),
        UserId    = cred.User.Id,
        Type      = entryType,
        Timestamp = now
    });

    cred.SignatureCounter = newCounter;
    await db.SaveChangesAsync();

    var timeStr = now.ToString("HH:mm") + " UTC";
    var message = entryType == EntryType.CheckIn
        ? $"Checked in \u2014 {cred.User.Username} at {timeStr}"
        : $"Checked out \u2014 {cred.User.Username} at {timeStr}";

    return Results.Ok(new
    {
        message,
        username  = cred.User.Username,
        entryType = entryType.ToString(),
        timestamp = now
    });
});

// ── GET /api/attendance/today ─────────────────────────────────────────────────
// Returns all entries for today grouped by user, with current status and entry list.
app.MapGet("/api/attendance/today", async (FidoDbContext db) =>
{
    var todayUtc = DateTime.UtcNow.Date;

    var entries = await db.AttendanceEntries
        .Include(e => e.User)
        .Where(e => e.Timestamp >= todayUtc)
        .OrderBy(e => e.User.Username)
        .ThenBy(e => e.Timestamp)
        .ToListAsync();

    var grouped = entries
        .GroupBy(e => new { e.UserId, e.User.Username })
        .Select(g => new
        {
            username      = g.Key.Username,
            currentStatus = g.Last().Type == EntryType.CheckIn ? "Inside" : "Outside",
            entries       = g.Select(e => new
            {
                type      = e.Type.ToString(),
                timestamp = e.Timestamp
            }).ToList()
        });

    return Results.Ok(grouped);
});

app.Run();

// ── Request / Response DTOs ───────────────────────────────────────────────────
record RegisterOptionsRequest(string Username);
record RegisterCompleteRequest(string SessionKey, string AttestationResponse);
record AuthOptionsRequest(string? Username);
record AuthCompleteRequest(string SessionKey, string AssertionResponse);

// Options JSON shapes — property names serialised camelCase to match the WebAuthn spec
record RpInfo(string Id, string Name);
record UserInfo(string Id, string Name, string DisplayName);
record PubKeyParam(string Type, int Alg);
record AllowCred(string Type, string Id);
record AuthSelCriteria(string? AuthenticatorAttachment, bool RequireResidentKey, string ResidentKey, string UserVerification);
record RegOptions(RpInfo Rp, UserInfo User, string Challenge, PubKeyParam[] PubKeyCredParams,
                  int Timeout, AllowCred[] ExcludeCredentials, AuthSelCriteria AuthenticatorSelection, string Attestation);
record AuthOptions(string Challenge, int Timeout, string RpId, AllowCred[] AllowCredentials, string UserVerification);

// Browser → server shapes (all byte fields transmitted as base64url strings)
record AttestationRespData(string AttestationObject, string ClientDataJSON);
record AttestationReq(string Id, string RawId, string Type, AttestationRespData Response);
record AssertionRespData(string AuthenticatorData, string ClientDataJSON, string Signature, string? UserHandle);
record AssertionReq(string Id, string RawId, string Type, AssertionRespData Response);

record RegistrationResult(byte[] CredentialId, byte[] PublicKey);

// ── EF Core models ────────────────────────────────────────────────────────────
public class FidoDbContext(DbContextOptions<FidoDbContext> options) : DbContext(options)
{
    public DbSet<FidoUser>        Users             => Set<FidoUser>();
    public DbSet<FidoCredential>  Credentials       => Set<FidoCredential>();
    public DbSet<AttendanceEntry> AttendanceEntries => Set<AttendanceEntry>();

    protected override void OnModelCreating(ModelBuilder b)
    {
        b.Entity<FidoUser>(e =>
        {
            e.HasKey(u => u.Id);
            e.HasIndex(u => u.Username).IsUnique();
            e.Property(u => u.Username).HasMaxLength(100);
        });
        b.Entity<FidoCredential>(e =>
        {
            e.HasKey(c => c.PkId);
            e.HasIndex(c => c.CredentialId);
            e.HasOne(c => c.User).WithMany(u => u.Credentials).HasForeignKey(c => c.UserId);
        });
        b.Entity<AttendanceEntry>(e =>
        {
            e.HasKey(a => a.Id);
            e.HasIndex(a => new { a.UserId, a.Timestamp });
            e.HasOne(a => a.User).WithMany().HasForeignKey(a => a.UserId);
            e.Property(a => a.Type).HasConversion<int>();
        });
    }
}

public class FidoUser
{
    public Guid     Id         { get; set; }
    public required string Username   { get; set; }
    public required byte[] UserHandle { get; set; }
    public DateTime CreatedAt  { get; set; }
    public List<FidoCredential> Credentials { get; set; } = [];
}

public class FidoCredential
{
    public int      PkId             { get; set; }
    public required byte[] CredentialId     { get; set; }
    public required byte[] PublicKey        { get; set; }
    public required byte[] UserHandle       { get; set; }
    public uint     SignatureCounter  { get; set; }
    public DateTime CreatedAt        { get; set; }
    public Guid     UserId           { get; set; }
    public FidoUser User             { get; set; } = null!;
}

public enum EntryType { CheckIn, CheckOut }

public class AttendanceEntry
{
    public Guid      Id        { get; set; }
    public Guid      UserId    { get; set; }
    public EntryType Type      { get; set; }
    public DateTime  Timestamp { get; set; }
    public FidoUser  User      { get; set; } = null!;
}

// ── WebAuthn Verification — native .NET 10 (no third-party library) ───────────
// Implements WebAuthn Level 2 §7.1 (registration) and §7.2 (authentication).
// Supported algorithm: ES256 — ECDSA P-256 / SHA-256 (alg=-7).
// This is the universal algorithm for platform authenticators (Touch ID, Windows Hello, etc.)
static class WebAuthnHelper
{
    // ── Registration (§7.1) ───────────────────────────────────────────────────
    public static RegistrationResult VerifyRegistration(
        AttestationReq req,
        byte[] storedChallenge,
        string expectedOrigin,
        string expectedRpId)
    {
        var clientDataBytes = WebEncoders.Base64UrlDecode(req.Response.ClientDataJSON);
        VerifyClientData(clientDataBytes, "webauthn.create", storedChallenge, expectedOrigin);

        var attObjBytes  = WebEncoders.Base64UrlDecode(req.Response.AttestationObject);
        var authDataBytes = ParseAttestationObject(attObjBytes);
        var authData      = ParseAuthData(authDataBytes);

        VerifyRpIdHash(authData.RpIdHash, expectedRpId);
        VerifyFlags(authData.Flags, requireUV: true);

        if (!authData.HasAttestedCredentialData || authData.CredentialId == null || authData.CosePublicKey == null)
            throw new Exception("Attested credential data missing from authData.");

        var publicKey = ParseCosePublicKey(authData.CosePublicKey);
        return new RegistrationResult(authData.CredentialId, publicKey);
    }

    // ── Authentication (§7.2) ─────────────────────────────────────────────────
    public static uint VerifyAuthentication(
        AssertionReq req,
        byte[] storedChallenge,
        string expectedOrigin,
        string expectedRpId,
        byte[] storedPublicKey,
        uint storedCounter)
    {
        var clientDataBytes = WebEncoders.Base64UrlDecode(req.Response.ClientDataJSON);
        VerifyClientData(clientDataBytes, "webauthn.get", storedChallenge, expectedOrigin);

        var authDataBytes = WebEncoders.Base64UrlDecode(req.Response.AuthenticatorData);
        var authData      = ParseAuthData(authDataBytes);

        VerifyRpIdHash(authData.RpIdHash, expectedRpId);
        VerifyFlags(authData.Flags, requireUV: true);

        // Counter must increase (0 means the authenticator doesn't support it — allow)
        if (authData.SignCount > 0 && authData.SignCount <= storedCounter)
            throw new Exception("Signature counter did not increase — possible cloned authenticator.");

        var clientDataHash = SHA256.HashData(clientDataBytes);
        var signature      = WebEncoders.Base64UrlDecode(req.Response.Signature);
        VerifyEs256Signature(storedPublicKey, authDataBytes, clientDataHash, signature);

        return authData.SignCount;
    }

    // ── Private helpers ───────────────────────────────────────────────────────

    static void VerifyClientData(byte[] bytes, string expectedType, byte[] expectedChallenge, string expectedOrigin)
    {
        using var doc = JsonDocument.Parse(bytes);
        var root = doc.RootElement;

        if (root.GetProperty("type").GetString() != expectedType)
            throw new Exception($"clientData.type mismatch (expected '{expectedType}').");

        var challengeB64 = root.GetProperty("challenge").GetString()
            ?? throw new Exception("clientData.challenge missing.");
        if (!WebEncoders.Base64UrlDecode(challengeB64).SequenceEqual(expectedChallenge))
            throw new Exception("Challenge mismatch.");

        var origin = root.GetProperty("origin").GetString();
        if (origin != expectedOrigin)
            throw new Exception($"Origin mismatch: expected '{expectedOrigin}', got '{origin}'.");
    }

    static byte[] ParseAttestationObject(byte[] bytes)
    {
        var reader   = new CborReader(bytes);
        reader.ReadStartMap();
        byte[]? authData = null;
        while (reader.PeekState() != CborReaderState.EndMap)
        {
            var key = reader.ReadTextString();
            if (key == "authData") authData = reader.ReadByteString().ToArray();
            else                   reader.SkipValue();
        }
        reader.ReadEndMap();
        return authData ?? throw new Exception("authData missing from attestationObject.");
    }

    static AuthDataParsed ParseAuthData(byte[] d)
    {
        if (d.Length < 37) throw new Exception("authData too short.");
        var rpIdHash  = d[0..32];
        var flags     = d[32];
        var signCount = BinaryPrimitives.ReadUInt32BigEndian(d[33..37]);
        byte[]? credId = null, coseKey = null;
        if ((flags & 0x40) != 0 && d.Length > 55)
        {
            var credIdLen = BinaryPrimitives.ReadUInt16BigEndian(d[53..55]);
            if (d.Length < 55 + credIdLen) throw new Exception("authData truncated.");
            credId  = d[55..(55 + credIdLen)];
            coseKey = d[(55 + credIdLen)..];
        }
        return new AuthDataParsed(rpIdHash, flags, signCount, credId, coseKey);
    }

    static byte[] ParseCosePublicKey(byte[] coseBytes)
    {
        var reader = new CborReader(coseBytes);
        reader.ReadStartMap();
        int kty = 0, alg = 0;
        byte[]? x = null, y = null;
        while (reader.PeekState() != CborReaderState.EndMap)
        {
            int key = reader.PeekState() == CborReaderState.UnsignedInteger
                ? (int)reader.ReadUInt64()
                : reader.ReadInt32();
            switch (key)
            {
                case  1: kty = (int)reader.ReadUInt64(); break;
                case  3: alg = reader.PeekState() == CborReaderState.NegativeInteger
                               ? reader.ReadInt32() : (int)reader.ReadUInt64(); break;
                case -1: reader.SkipValue(); break;                    // crv — assume P-256
                case -2: x = reader.ReadByteString().ToArray(); break; // x coordinate
                case -3: y = reader.ReadByteString().ToArray(); break; // y coordinate
                default: reader.SkipValue(); break;
            }
        }
        reader.ReadEndMap();
        if (kty != 2 || alg != -7)
            throw new NotSupportedException($"Only ES256 (kty=2, alg=-7) is supported. Got kty={kty}, alg={alg}.");
        if (x is not { Length: > 0 } || y is not { Length: > 0 })
            throw new Exception("COSE key missing x or y coordinate.");
        return [.. PadTo32(x), .. PadTo32(y)]; // 64 bytes: x ∥ y
    }

    static void VerifyEs256Signature(byte[] storedPublicKey, byte[] authDataBytes, byte[] clientDataHash, byte[] signature)
    {
        using var ecdsa = ECDsa.Create(new ECParameters
        {
            Curve = ECCurve.NamedCurves.nistP256,
            Q     = new ECPoint { X = storedPublicKey[0..32], Y = storedPublicKey[32..64] }
        });
        // Signed data = authData ∥ SHA-256(clientDataJSON)  (WebAuthn spec §7.2 step 20)
        var signedData = new byte[authDataBytes.Length + 32];
        authDataBytes.CopyTo(signedData, 0);
        clientDataHash.CopyTo(signedData, authDataBytes.Length);
        // WebAuthn ECDSA signatures are DER-encoded ASN.1 SEQUENCE (RFC 3279)
        if (!ecdsa.VerifyData(signedData, signature, HashAlgorithmName.SHA256, DSASignatureFormat.Rfc3279DerSequence))
            throw new Exception("Signature verification failed.");
    }

    static void VerifyRpIdHash(byte[] rpIdHash, string rpId)
    {
        if (!SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(rpId)).SequenceEqual(rpIdHash))
            throw new Exception("rpIdHash mismatch.");
    }

    static void VerifyFlags(byte flags, bool requireUV)
    {
        if ((flags & 0x01) == 0) throw new Exception("User-presence flag not set.");
        if (requireUV && (flags & 0x04) == 0) throw new Exception("User-verification flag not set.");
    }

    // Pad byte array to exactly 32 bytes with leading zeros (big-endian coordinates)
    static byte[] PadTo32(byte[] b)
    {
        if (b.Length == 32) return b;
        var padded = new byte[32];
        b.CopyTo(padded, 32 - b.Length);
        return padded;
    }

    record AuthDataParsed(byte[] RpIdHash, byte Flags, uint SignCount, byte[]? CredentialId, byte[]? CosePublicKey)
    {
        public bool HasAttestedCredentialData => (Flags & 0x40) != 0;
    }
}
