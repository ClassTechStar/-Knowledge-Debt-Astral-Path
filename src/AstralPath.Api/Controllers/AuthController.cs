using Microsoft.AspNetCore.Mvc;
using AstralPath.Contracts;
using AstralPath.Infrastructure;

namespace AstralPath.Api.Controllers;

public sealed record RegisterRequest(string Email, string Password, string? DisplayName = null, string? DemoStudentId = null);
public sealed record LoginRequest(string Email, string? Password = null, string? DeviceName = null);
public sealed record ProfileUpdateRequest(string? DisplayName = null, string? DemoStudentId = null);
public sealed record PasswordChangeRequest(string OldPassword, string NewPassword);

[ApiController]
public sealed class AuthController : ControllerBase
{
    private readonly AuthStore _auth;
    private readonly AstralPathStore _store;

    public AuthController(AuthStore auth, AstralPathStore store)
    {
        _auth = auth;
        _store = store;
    }

    private static string? Bearer(HttpContext ctx)
    {
        var header = ctx.Request.Headers.Authorization.FirstOrDefault();
        if (string.IsNullOrWhiteSpace(header)) return null;
        if (!header.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase)) return null;
        return header["Bearer ".Length..].Trim();
    }

    private AuthUser? CurrentUser() => _auth.FindByAccessToken(Bearer(HttpContext));

    [HttpPost("/api/v1/auth/register")]
    public IResult Register([FromBody] RegisterRequest request)
    {
        try
        {
            if (request.DemoStudentId is not (null or "demo-student-a" or "demo-student-b"))
                return HttpResults.Fail(400, ErrorCodes.ValidationError, "demoStudentId 仅支持 demo-student-a/b");

            var user = _auth.Register(request.Email, request.Password, request.DisplayName, request.DemoStudentId);
            _store.EnsureStudent(user.DemoStudentId);
            var session = _auth.Login(user.Email, request.Password, "web");
            return Results.Json(new
            {
                data = new
                {
                    sessionId = session.SessionId,
                    accessToken = session.AccessToken,
                    refreshToken = session.RefreshToken,
                    accessExpiresAt = session.AccessExpiresAt,
                    profile = new
                    {
                        userId = user.UserId,
                        email = user.Email,
                        displayName = user.DisplayName,
                        demoStudentId = user.DemoStudentId,
                        createdAt = user.CreatedAt
                    }
                },
                meta = new { },
                traceId = Guid.NewGuid().ToString("N")
            }, statusCode: 201);
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException)
        {
            return HttpResults.Fail(400, ErrorCodes.ValidationError, ex.Message);
        }
    }

    [HttpPost("/api/v1/auth/sessions")]
    public IResult Login([FromBody] LoginRequest request)
    {
        try
        {
            var session = _auth.Login(request.Email, request.Password ?? "", request.DeviceName);
            var profile = _auth.GetProfile(session.UserId);
            if (profile == null)
                return HttpResults.Fail(404, ErrorCodes.ResourceNotFound, "用户不存在");
            _store.EnsureStudent(profile.DemoStudentId);
            return Results.Json(new
            {
                data = new
                {
                    sessionId = session.SessionId,
                    accessToken = session.AccessToken,
                    refreshToken = session.RefreshToken,
                    accessExpiresAt = session.AccessExpiresAt,
                    profile
                },
                meta = new { },
                traceId = Guid.NewGuid().ToString("N")
            }, statusCode: 201);
        }
        catch (UnauthorizedAccessException ex)
        {
            return HttpResults.Fail(401, ErrorCodes.AuthRequired, ex.Message);
        }
        catch (ArgumentException ex)
        {
            return HttpResults.Fail(400, ErrorCodes.ValidationError, ex.Message);
        }
    }

    [HttpGet("/api/v1/auth/me")]
    public IResult Me()
    {
        var user = CurrentUser();
        if (user == null)
            return HttpResults.Fail(401, ErrorCodes.AuthRequired, "登录状态已失效，请重新登录");
        var profile = _auth.GetProfile(user.UserId);
        return HttpResults.Success(profile);
    }

    [HttpPut("/api/v1/auth/profile")]
    public IResult UpdateProfile([FromBody] ProfileUpdateRequest request)
    {
        var user = CurrentUser();
        if (user == null)
            return HttpResults.Fail(401, ErrorCodes.AuthRequired, "登录状态已失效，请重新登录");
        if (request.DemoStudentId is not (null or "" or "demo-student-a" or "demo-student-b"))
            return HttpResults.Fail(400, ErrorCodes.ValidationError, "demoStudentId 仅支持 demo-student-a/b");
        var updated = _auth.UpdateProfile(user.UserId, request.DisplayName, request.DemoStudentId);
        var profile = _auth.GetProfile(user.UserId);
        return profile != null
            ? HttpResults.Success(profile)
            : HttpResults.Fail(404, ErrorCodes.ResourceNotFound, "用户不存在");
    }

    [HttpPost("/api/v1/auth/password-changes")]
    public IResult ChangePassword([FromBody] PasswordChangeRequest request)
    {
        var user = CurrentUser();
        if (user == null)
            return HttpResults.Fail(401, ErrorCodes.AuthRequired, "登录状态已失效，请重新登录");
        if (!_auth.ChangePassword(user.UserId, request.OldPassword, request.NewPassword))
            return HttpResults.Fail(400, ErrorCodes.ValidationError, "原密码不正确，或新密码少于 6 位");
        return HttpResults.Success(new { ok = true, message = "密码已更新，请重新登录" });
    }

    [HttpPost("/api/v1/auth/logout")]
    public IResult Logout([FromBody] LogoutRequest? request)
    {
        var user = CurrentUser();
        var sessionId = request?.SessionId;
        if (user == null || string.IsNullOrWhiteSpace(sessionId))
            return HttpResults.Success(new { ok = true });
        _auth.Logout(sessionId, user.UserId);
        return HttpResults.Success(new { ok = true });
    }

    [HttpDelete("/api/v1/auth/sessions/{sessionId}")]
    public IResult DeleteSession(string sessionId)
    {
        var user = CurrentUser();
        if (user == null)
            return HttpResults.Fail(401, ErrorCodes.AuthRequired, "登录状态已失效，请重新登录");
        var ok = _auth.Logout(sessionId, user.UserId);
        return ok ? Results.NoContent() : HttpResults.Fail(404, ErrorCodes.ResourceNotFound, "会话不存在");
    }

    /// <summary>为已登录用户生成基于教材解析结果的今日任务（支持轮换）。</summary>
    [HttpPost("/api/v1/auth/material-today")]
    public IResult MaterialToday([FromQuery] int maxTasks = 6, [FromQuery] bool rotate = false)
    {
        var user = CurrentUser();
        if (user == null)
            return HttpResults.Fail(401, ErrorCodes.AuthRequired, "登录状态已失效，请重新登录");
        var profile = _auth.GetProfile(user.UserId)!;
        var studentId = profile.DemoStudentId;

        var graphs = MaterialRegistry.ListGraphs();
        var tasks = new List<AstralPath.Contracts.TodayTaskDto>();
        string materialName = "未指定教材";
        object? bankStats = null;
        string? bank = null;
        foreach (var g in graphs)
        {
            materialName = g.MaterialName;
            bank = TextbookQuestionBank.MatchBankKey(materialName);
            var batch = TextbookQuestionBank.ToTodayTasks(materialName, maxTasks, rotate);
            if (batch.Count > 0)
            {
                bankStats = TextbookQuestionBank.BankStats(materialName);
                tasks = batch;
                break;
            }
            tasks.AddRange(MaterialTaskGenerator.GenerateFromGraph(g, g.MaterialName, maxTasks));
            if (tasks.Count >= maxTasks) break;
        }
        tasks = tasks.Take(maxTasks).ToList();

        var brief = MaterialTaskGenerator.BuildDayBrief(tasks, materialName);
        return HttpResults.Success(new
        {
            studentId,
            userId = profile.UserId,
            displayName = profile.DisplayName,
            email = profile.Email,
            material = materialName,
            bank,
            bankStats,
            rotated = rotate,
            day = _store.Students.GetValueOrDefault(studentId)?.CurrentDay ?? 1,
            brief,
            tasks
        });
    }
}

public sealed record LogoutRequest(string? SessionId = null);
