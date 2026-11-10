using System.Text.RegularExpressions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using SVS.Application.Common.Exceptions;
using SVS.Application.Common.Interfaces;
using SVS.Domain.Entities;

namespace SVS.Application.Auth;

/// <summary>
/// Đăng ký, đăng nhập, làm mới và thu hồi phiên (FR-AUTH-01..04).
/// Access token là JWT ngắn hạn, không lưu DB. Refresh token là chuỗi ngẫu nhiên, chỉ lưu băm, xoay vòng sau mỗi lần dùng.
/// Dùng lại một refresh token đã bị thay thế (ngoài khoảng chạy đua ngắn) làm vô hiệu cả họ token của phiên đó.
/// </summary>
public sealed class AuthService(
    IApplicationDbContext db, IPasswordService passwords, IAccessTokenFactory accessTokens,
    IOptions<AuthOptions> options, ICurrentUser currentUser)
{
    private static readonly Regex EmailPattern = new(@"^[^@\s]+@[^@\s]+\.[^@\s]+$", RegexOptions.Compiled);
    private static string? _dummyHash;

    private AuthOptions Opt => options.Value;

    // ------------------------------------------------------------------ đăng ký / đăng nhập

    public async Task<AuthResult> RegisterAsync(string? email, string? password, string? displayName, CancellationToken ct)
    {
        var mail = NormalizeEmail(email);
        var name = displayName?.Trim() ?? string.Empty;
        if (mail.Length == 0 || mail.Length > 256 || !EmailPattern.IsMatch(mail))
            throw AppException.Validation("Email không hợp lệ");
        if (password is null || password.Length < Opt.MinPasswordLength || password.Length > 128)
            throw AppException.Validation($"Mật khẩu phải có từ {Opt.MinPasswordLength} đến 128 ký tự");
        if (name.Length is < 1 or > 100)
            throw AppException.Validation("Tên hiển thị phải có 1 đến 100 ký tự");

        if (await db.Users.AnyAsync(u => u.Email == mail, ct))
            throw AppException.Conflict("EMAIL_TAKEN", "Email này đã được đăng ký");

        var user = new User { Email = mail, DisplayName = name, PasswordHash = passwords.Hash(password) };
        db.Users.Add(user);
        var (result, _) = Issue(user, Guid.NewGuid());
        try
        {
            await db.SaveChangesAsync(ct);
        }
        catch (DbUpdateException) // hai request đăng ký cùng email chạy đua: chỉ mục duy nhất chặn bản thứ hai
        {
            throw AppException.Conflict("EMAIL_TAKEN", "Email này đã được đăng ký");
        }
        return result;
    }

    public async Task<AuthResult> LoginAsync(string? email, string? password, CancellationToken ct)
    {
        var mail = NormalizeEmail(email);
        var user = mail.Length == 0 ? null : await db.Users.FirstOrDefaultAsync(u => u.Email == mail, ct);

        if (user is null)
        {
            // Vẫn tốn thời gian băm như trường hợp sai mật khẩu để không lộ email nào đã đăng ký qua độ trễ phản hồi.
            _dummyHash ??= passwords.Hash("dummy-password-for-timing");
            passwords.Verify(_dummyHash, password ?? string.Empty);
            throw InvalidCredentials();
        }
        if (string.IsNullOrEmpty(password) || !passwords.Verify(user.PasswordHash, password))
            throw InvalidCredentials();

        var (result, _) = Issue(user, Guid.NewGuid());
        await db.SaveChangesAsync(ct);
        return result;
    }

    // ------------------------------------------------------------------ làm mới / đăng xuất

    public async Task<AuthResult> RefreshAsync(string? refreshToken, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(refreshToken))
            throw AppException.Unauthorized("INVALID_REFRESH", "Refresh token không hợp lệ");

        var hash = TokenHasher.Hash(refreshToken.Trim());
        var token = await db.RefreshTokens.Include(t => t.User).FirstOrDefaultAsync(t => t.TokenHash == hash, ct)
            ?? throw AppException.Unauthorized("INVALID_REFRESH", "Refresh token không hợp lệ");
        var now = DateTimeOffset.UtcNow;

        if (token.RevokedAt is { } revokedAt)
        {
            if ((now - revokedAt).TotalSeconds <= Opt.RefreshGraceSeconds)
                throw AppException.Unauthorized("REFRESH_IN_USE", "Token vừa được làm mới ở nơi khác, hãy dùng token mới");
            await RevokeFamilyAsync(token.FamilyId, now, ct); // dùng lại token cũ: nghi bị đánh cắp, đăng xuất mọi nơi của phiên này
            throw AppException.Unauthorized("REFRESH_REUSED", "Phiên đăng nhập không còn hợp lệ, hãy đăng nhập lại");
        }
        if (token.ExpiresAt <= now)
            throw AppException.Unauthorized("REFRESH_EXPIRED", "Phiên đăng nhập đã hết hạn, hãy đăng nhập lại");

        var (result, next) = Issue(token.User, token.FamilyId);
        token.RevokedAt = now;
        token.ReplacedByTokenId = next.Id;
        try
        {
            await db.SaveChangesAsync(ct);
        }
        catch (DbUpdateConcurrencyException) // hai request cùng làm mới một token: bản đến sau thua
        {
            throw AppException.Unauthorized("REFRESH_IN_USE", "Token vừa được làm mới ở nơi khác, hãy dùng token mới");
        }
        return result;
    }

    /// <summary>Thu hồi cả họ token của phiên đăng nhập. Idempotent: token lạ hay đã thu hồi vẫn trả thành công.</summary>
    public async Task LogoutAsync(string? refreshToken, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(refreshToken)) return;
        var hash = TokenHasher.Hash(refreshToken.Trim());
        var familyId = await db.RefreshTokens.Where(t => t.TokenHash == hash).Select(t => (Guid?)t.FamilyId).FirstOrDefaultAsync(ct);
        if (familyId is { } id) await RevokeFamilyAsync(id, DateTimeOffset.UtcNow, ct);
    }

    // ------------------------------------------------------------------ hồ sơ

    public async Task<UserProfile> GetProfileAsync(CancellationToken ct)
    {
        var user = await CurrentUserAsync(ct);
        return ToProfile(user);
    }

    public async Task<UserProfile> UpdateProfileAsync(string? displayName, CancellationToken ct)
    {
        var name = displayName?.Trim() ?? string.Empty;
        if (name.Length is < 1 or > 100) throw AppException.Validation("Tên hiển thị phải có 1 đến 100 ký tự");
        var user = await CurrentUserAsync(ct);
        user.DisplayName = name;
        await db.SaveChangesAsync(ct);
        return ToProfile(user);
    }

    // ------------------------------------------------------------------ chi tiết

    private async Task<User> CurrentUserAsync(CancellationToken ct)
    {
        var id = currentUser.RequireUserId();
        return await db.Users.FirstOrDefaultAsync(u => u.Id == id, ct)
            ?? throw AppException.Unauthorized("UNAUTHENTICATED", "Tài khoản không còn tồn tại");
    }

    private (AuthResult Result, RefreshToken Token) Issue(User user, Guid familyId)
    {
        var raw = TokenHasher.NewToken();
        var now = DateTimeOffset.UtcNow;
        var token = new RefreshToken
        {
            UserId = user.Id, FamilyId = familyId, TokenHash = TokenHasher.Hash(raw),
            CreatedAt = now, ExpiresAt = now.AddDays(Opt.RefreshTokenDays),
        };
        db.RefreshTokens.Add(token);
        var access = accessTokens.Create(user);
        return (new AuthResult(access.Token, access.ExpiresAt, raw, token.ExpiresAt, ToProfile(user)), token);
    }

    private async Task RevokeFamilyAsync(Guid familyId, DateTimeOffset now, CancellationToken ct)
    {
        var active = await db.RefreshTokens.Where(t => t.FamilyId == familyId && t.RevokedAt == null).ToListAsync(ct);
        foreach (var t in active) t.RevokedAt = now;
        try { await db.SaveChangesAsync(ct); }
        catch (DbUpdateConcurrencyException) { /* ai đó vừa thu hồi: mục đích đã đạt */ }
    }

    private static AppException InvalidCredentials() =>
        AppException.Unauthorized("INVALID_CREDENTIALS", "Email hoặc mật khẩu không đúng");

    private static string NormalizeEmail(string? email) => (email ?? string.Empty).Trim().ToLowerInvariant();

    private static UserProfile ToProfile(User u) => new(u.Id, u.Email, u.DisplayName, u.CreatedAt);
}
