namespace SmartFactory.MES.Data
{
    /// <summary>
    /// M5（security-assessment-and-plan.md）：AuditLog 敏感 PII 遮罩。
    /// 稽核紀錄需保留「某欄位有異動」的信號，但值不得落明文個資（Email/電話）。
    /// 與整欄排除（PasswordHash/SecurityStamp 等）的差異：遮罩仍保留可辨識線索
    /// （Email 網域、末 3 碼），供追查時比對「是否確實改到某人」，同時不外洩完整 PII。
    /// 純函式，供 <see cref="AuditInterceptor"/> 呼叫並可獨立單元測試。
    /// </summary>
    public static class AuditPiiMasker
    {
        // 依「屬性名稱」判定 PII（跨所有實體皆遮罩，如 ApplicationUser.Email／Company.Email）。
        // 正規化欄位（NormalizedEmail 等）另由 AuditInterceptor 整欄排除，不進此處。
        private static readonly HashSet<string> _emailProperties = new(StringComparer.OrdinalIgnoreCase)
        {
            "Email"
        };

        private static readonly HashSet<string> _phoneProperties = new(StringComparer.OrdinalIgnoreCase)
        {
            "PhoneNumber"
        };

        /// <summary>此屬性名稱是否屬於需遮罩的 PII。</summary>
        public static bool IsMaskedProperty(string propertyName)
            => _emailProperties.Contains(propertyName) || _phoneProperties.Contains(propertyName);

        /// <summary>
        /// 若 <paramref name="propertyName"/> 為 PII 且值為非空字串，回傳遮罩後字串；
        /// 否則原值原樣回傳（含 null、非字串型別）。
        /// </summary>
        public static object? Mask(string propertyName, object? value)
        {
            if (value is not string s || s.Length == 0) return value;
            if (_emailProperties.Contains(propertyName)) return MaskEmail(s);
            if (_phoneProperties.Contains(propertyName)) return MaskGeneric(s);
            return value;
        }

        /// <summary>Email 遮罩：保留本地部分首字元與完整網域（例：john@corp.com → j***@corp.com）。</summary>
        public static string MaskEmail(string email)
        {
            if (string.IsNullOrEmpty(email)) return email;

            var at = email.IndexOf('@');
            // 無 '@' 或本地部分為空（"@x"）→ 退回一般遮罩，不洩漏內容
            if (at <= 0) return MaskGeneric(email);

            var local = email[..at];
            var domain = email[at..]; // 含 '@'
            var head = local.Length <= 1 ? string.Empty : local[..1];
            return head + new string('*', local.Length - head.Length) + domain;
        }

        /// <summary>一般 PII 遮罩：保留末 3 碼供辨識，其餘以 '*' 取代；長度 ≤3 全遮。</summary>
        public static string MaskGeneric(string value)
        {
            if (string.IsNullOrEmpty(value)) return value;
            if (value.Length <= 3) return new string('*', value.Length);
            return new string('*', value.Length - 3) + value[^3..];
        }
    }
}
