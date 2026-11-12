using Microsoft.AspNetCore.Identity;
using System.Globalization;

namespace SmartFactory.MES.Services;

public class LocalizedIdentityErrorDescriber : IdentityErrorDescriber
{
    private static bool IsZhTw =>
        CultureInfo.CurrentUICulture.Name != "en-US";

    public override IdentityError DuplicateRoleName(string role) => IsZhTw
        ? new IdentityError { Code = nameof(DuplicateRoleName), Description = $"角色名稱「{role}」已被使用。" }
        : base.DuplicateRoleName(role);

    public override IdentityError DuplicateUserName(string userName) => IsZhTw
        ? new IdentityError { Code = nameof(DuplicateUserName), Description = $"使用者名稱「{userName}」已被使用。" }
        : base.DuplicateUserName(userName);

    public override IdentityError DuplicateEmail(string email) => IsZhTw
        ? new IdentityError { Code = nameof(DuplicateEmail), Description = $"電子郵件「{email}」已被使用。" }
        : base.DuplicateEmail(email);

    public override IdentityError InvalidEmail(string? email) => IsZhTw
        ? new IdentityError { Code = nameof(InvalidEmail), Description = $"電子郵件「{email}」格式不正確。" }
        : base.InvalidEmail(email);

    public override IdentityError InvalidUserName(string? userName) => IsZhTw
        ? new IdentityError { Code = nameof(InvalidUserName), Description = $"使用者名稱「{userName}」格式不正確，只能包含字母或數字。" }
        : base.InvalidUserName(userName);

    public override IdentityError InvalidRoleName(string? role) => IsZhTw
        ? new IdentityError { Code = nameof(InvalidRoleName), Description = $"角色名稱「{role}」格式不正確。" }
        : base.InvalidRoleName(role);

    public override IdentityError PasswordTooShort(int length) => IsZhTw
        ? new IdentityError { Code = nameof(PasswordTooShort), Description = $"密碼長度至少需要 {length} 個字元。" }
        : base.PasswordTooShort(length);

    public override IdentityError PasswordRequiresUpper() => IsZhTw
        ? new IdentityError { Code = nameof(PasswordRequiresUpper), Description = "密碼必須包含至少一個大寫英文字母（A-Z）。" }
        : base.PasswordRequiresUpper();

    public override IdentityError PasswordRequiresLower() => IsZhTw
        ? new IdentityError { Code = nameof(PasswordRequiresLower), Description = "密碼必須包含至少一個小寫英文字母（a-z）。" }
        : base.PasswordRequiresLower();

    public override IdentityError PasswordRequiresDigit() => IsZhTw
        ? new IdentityError { Code = nameof(PasswordRequiresDigit), Description = "密碼必須包含至少一個數字（0-9）。" }
        : base.PasswordRequiresDigit();

    public override IdentityError PasswordRequiresNonAlphanumeric() => IsZhTw
        ? new IdentityError { Code = nameof(PasswordRequiresNonAlphanumeric), Description = "密碼必須包含至少一個非英數字元。" }
        : base.PasswordRequiresNonAlphanumeric();

    public override IdentityError PasswordRequiresUniqueChars(int uniqueChars) => IsZhTw
        ? new IdentityError { Code = nameof(PasswordRequiresUniqueChars), Description = $"密碼必須包含至少 {uniqueChars} 個不重複的字元。" }
        : base.PasswordRequiresUniqueChars(uniqueChars);

    public override IdentityError UserNotInRole(string role) => IsZhTw
        ? new IdentityError { Code = nameof(UserNotInRole), Description = $"使用者不屬於「{role}」角色。" }
        : base.UserNotInRole(role);

    public override IdentityError UserAlreadyInRole(string role) => IsZhTw
        ? new IdentityError { Code = nameof(UserAlreadyInRole), Description = $"使用者已屬於「{role}」角色。" }
        : base.UserAlreadyInRole(role);

    public override IdentityError UserLockoutNotEnabled() => IsZhTw
        ? new IdentityError { Code = nameof(UserLockoutNotEnabled), Description = "此使用者未啟用帳號鎖定功能。" }
        : base.UserLockoutNotEnabled();

    public override IdentityError UserAlreadyHasPassword() => IsZhTw
        ? new IdentityError { Code = nameof(UserAlreadyHasPassword), Description = "此使用者已設有密碼。" }
        : base.UserAlreadyHasPassword();

    public override IdentityError PasswordMismatch() => IsZhTw
        ? new IdentityError { Code = nameof(PasswordMismatch), Description = "密碼不正確。" }
        : base.PasswordMismatch();

    public override IdentityError InvalidToken() => IsZhTw
        ? new IdentityError { Code = nameof(InvalidToken), Description = "驗證碼無效。" }
        : base.InvalidToken();

    public override IdentityError LoginAlreadyAssociated() => IsZhTw
        ? new IdentityError { Code = nameof(LoginAlreadyAssociated), Description = "此外部登入已與其他帳號綁定。" }
        : base.LoginAlreadyAssociated();

    public override IdentityError ConcurrencyFailure() => IsZhTw
        ? new IdentityError { Code = nameof(ConcurrencyFailure), Description = "資料已被其他人修改，請重新整理後再試。" }
        : base.ConcurrencyFailure();

    public override IdentityError DefaultError() => IsZhTw
        ? new IdentityError { Code = nameof(DefaultError), Description = "發生未知的錯誤。" }
        : base.DefaultError();
}
