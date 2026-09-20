using System.Security.Cryptography;

namespace AstralPath.Infrastructure;

public sealed record AuthUser(
    string UserId,
    string Email,
    string DisplayName,
    string PasswordHash,
    string DemoStudentId,
    DateTime CreatedAt,
    DateTime UpdatedAt);

public sealed record AuthSession(
    string SessionId,
    string UserId,
    string AccessToken,
    string RefreshToken,
    DateTime CreatedAt,
    DateTime AccessExpiresAt,
    DateTime RefreshExpiresAt,
    DateTime? RevokedAt)
{
    public string Status => RevokedAt != null ? "revoked"
        : RefreshExpiresAt < DateTime.UtcNow ? "expired"
        : "active";
}

public sealed record AuthProfile(
    string UserId,
    string Email,
    string DisplayName,
    string DemoStudentId,
    DateTime CreatedAt);

/// <summary>GalReview 风格账户：邮箱+密码注册登录，会话令牌，演示学生绑定。</summary>
public sealed class AuthStore
{
    private readonly object _gate = new();
    private readonly Dictionary<string, AuthUser> _usersByEmail = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, AuthUser> _usersById = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, AuthSession> _sessions = new(StringComparer.Ordinal);

    public AuthUser Register(string email, string password, string? displayName = null, string? demoStudentId = null)
    {
        email = (email ?? "").Trim().ToLowerInvariant();
        if (string.IsNullOrWhiteSpace(email) || !email.Contains('@') || !email.Contains('.'))
            throw new ArgumentException("请输入有效邮箱");
        if (string.IsNullOrWhiteSpace(password) || password.Length < 6)
            throw new ArgumentException("密码至少 6 位");

        lock (_gate)
        {
            if (_usersByEmail.ContainsKey(email))
                throw new InvalidOperationException("该邮箱已注册，请直接登录");

            var userId = Guid.NewGuid().ToString("N");
            var student = string.IsNullOrWhiteSpace(demoStudentId)
                ? (email.Contains("b") && email.Length > 2 ? "demo-student-b" : "demo-student-a")
                : demoStudentId!;
            var user = new AuthUser(
                userId,
                email,
                string.IsNullOrWhiteSpace(displayName) ? email.Split('@')[0] : displayName!.Trim(),
                HashPassword(password),
                student,
                DateTime.UtcNow,
                DateTime.UtcNow);
            _usersByEmail[email] = user;
            _usersById[userId] = user;
            return user;
        }
    }

    public AuthSession Login(string email, string password, string? deviceName = null)
    {
        email = (email ?? "").Trim().ToLowerInvariant();
        lock (_gate)
        {
            if (!_usersByEmail.TryGetValue(email, out var user) || !VerifyPassword(password, user.PasswordHash))
                throw new UnauthorizedAccessException("邮箱或密码不正确");

            var now = DateTime.UtcNow;
            var session = new AuthSession(
                Guid.NewGuid().ToString("N"),
                user.UserId,
                Convert.ToHexString(RandomNumberGenerator.GetBytes(24)).ToLowerInvariant(),
                Convert.ToHexString(RandomNumberGenerator.GetBytes(24)).ToLowerInvariant(),
                now,
                now.AddHours(12),
                now.AddDays(14),
                null);
            _sessions[session.SessionId] = session;
            _ = deviceName;
            return session;
        }
    }

    public AuthUser? FindByAccessToken(string? token)
    {
        if (string.IsNullOrWhiteSpace(token)) return null;
        lock (_gate)
        {
            var session = _sessions.Values.FirstOrDefault(s =>
                string.Equals(s.AccessToken, token, StringComparison.Ordinal)
                && s.RevokedAt == null
                && s.AccessExpiresAt > DateTime.UtcNow);
            return session == null ? null : _usersById.GetValueOrDefault(session.UserId);
        }
    }

    public AuthUser? FindById(string userId)
    {
        lock (_gate) return _usersById.GetValueOrDefault(userId);
    }

    public AuthProfile? GetProfile(string userId)
    {
        var user = FindById(userId);
        return user == null
            ? null
            : new AuthProfile(user.UserId, user.Email, user.DisplayName, user.DemoStudentId, user.CreatedAt);
    }

    public AuthUser? UpdateProfile(string userId, string? displayName, string? demoStudentId)
    {
        lock (_gate)
        {
            if (!_usersById.TryGetValue(userId, out var user)) return null;
            var next = user with
            {
                DisplayName = string.IsNullOrWhiteSpace(displayName) ? user.DisplayName : displayName!.Trim(),
                DemoStudentId = string.IsNullOrWhiteSpace(demoStudentId) ? user.DemoStudentId : demoStudentId!,
                UpdatedAt = DateTime.UtcNow
            };
            _usersById[userId] = next;
            _usersByEmail[next.Email] = next;
            return next;
        }
    }

    public bool Logout(string sessionId, string userId)
    {
        lock (_gate)
        {
            if (!_sessions.TryGetValue(sessionId, out var session) || session.UserId != userId) return false;
            if (session.RevokedAt != null) return true;
            _sessions[sessionId] = session with { RevokedAt = DateTime.UtcNow };
            return true;
        }
    }

    public bool ChangePassword(string userId, string oldPassword, string newPassword)
    {
        lock (_gate)
        {
            if (!_usersById.TryGetValue(userId, out var user)) return false;
            if (!VerifyPassword(oldPassword, user.PasswordHash)) return false;
            if (string.IsNullOrWhiteSpace(newPassword) || newPassword.Length < 6) return false;
            var next = user with { PasswordHash = HashPassword(newPassword), UpdatedAt = DateTime.UtcNow };
            _usersById[userId] = next;
            _usersByEmail[next.Email] = next;
            foreach (var sid in _sessions.Values.Where(s => s.UserId == userId && s.RevokedAt == null).Select(s => s.SessionId).ToList())
            {
                _sessions[sid] = _sessions[sid] with { RevokedAt = DateTime.UtcNow };
            }
            return true;
        }
    }

    /// <summary>演示种子账户，便于竞赛现场直接登录。</summary>
    public void SeedDemoUsers()
    {
        try
        {
            if (FindByEmail("demo@astralpath.local") == null)
            {
                var u = Register("demo@astralpath.local", "demo123456", "演示学习者", "demo-student-a");
                Login("demo@astralpath.local", "demo123456", "demo-device");
                _ = u;
            }
            if (FindByEmail("teacher@astralpath.local") == null)
                Register("teacher@astralpath.local", "teacher123", "演示教师", "demo-student-b");
        }
        catch
        {
            // ignore duplicate seed races
        }
    }

    public AuthUser? FindByEmail(string email)
    {
        email = (email ?? "").Trim().ToLowerInvariant();
        lock (_gate) return _usersByEmail.GetValueOrDefault(email);
    }

    private static string HashPassword(string password)
    {
        var salt = RandomNumberGenerator.GetBytes(16);
        var hash = Rfc2898DeriveBytes.Pbkdf2(password, salt, 100_000, HashAlgorithmName.SHA256, 32);
        return $"pbkdf2$100000${Convert.ToBase64String(salt)}${Convert.ToBase64String(hash)}";
    }

    private static bool VerifyPassword(string password, string stored)
    {
        try
        {
            var parts = stored.Split('$');
            if (parts.Length != 4 || parts[0] != "pbkdf2") return false;
            var iter = int.Parse(parts[1]);
            var salt = Convert.FromBase64String(parts[2]);
            var expected = Convert.FromBase64String(parts[3]);
            var actual = Rfc2898DeriveBytes.Pbkdf2(password ?? "", salt, iter, HashAlgorithmName.SHA256, expected.Length);
            return CryptographicOperations.FixedTimeEquals(actual, expected);
        }
        catch
        {
            return false;
        }
    }
}
