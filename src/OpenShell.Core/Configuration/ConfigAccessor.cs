namespace OpenShell.Configuration;

/// <summary>
/// <see cref="OpenShellConfig"/> 的按名称读写助手。
/// 用于 get-config / set-config 命令的动态属性访问。
/// </summary>
internal static class ConfigAccessor
{
    /// <summary>按名称读取配置值 (字符串表示)。</summary>
    /// <param name="config">配置对象。</param>
    /// <param name="name">配置项名称 (大小写不敏感)。</param>
    /// <returns>值的字符串表示; 未知 key 返回 "(unknown)"。</returns>
    public static string Get(OpenShellConfig config, string name)
    {
        return name.ToLowerInvariant() switch
        {
            "theme" => config.Theme,
            "promptstyle" => config.PromptStyle,
            "historysize" => config.HistorySize.ToString(),
            "maxparalleloperations" => config.MaxParallelOperations.ToString(),
            "profilestoponerror" => config.ProfileStopOnError.ToString(),
            "autoupdate" => config.AutoUpdate.ToString(),
            "updatechannel" => config.UpdateChannel,
            "updatecheckfrequency" => config.UpdateCheckFrequency,
            "includeprerelease" => config.IncludePrerelease.ToString(),
            "securityrole" => config.SecurityRole,
            "securitystrictness" => config.SecurityStrictness,
            "protectedpaths" => string.Join(";", config.ProtectedPaths),
            "executionpolicy" => config.ExecutionPolicy,
            _ => "(unknown)",
        };
    }

    /// <summary>按名称设置配置值, 自动做类型转换。</summary>
    /// <param name="config">配置对象。</param>
    /// <param name="name">配置项名称 (大小写不敏感)。</param>
    /// <param name="value">值的字符串表示。</param>
    /// <returns>设置成功返回 true; 未知 key 或类型转换失败返回 false。</returns>
    public static bool TrySet(OpenShellConfig config, string name, string value)
    {
        switch (name.ToLowerInvariant())
        {
            case "theme":
                config.Theme = value;
                return true;
            case "promptstyle":
                config.PromptStyle = value;
                return true;
            case "historysize":
                if (int.TryParse(value, out var hs) && hs > 0)
                {
                    config.HistorySize = hs;
                    return true;
                }
                return false;
            case "maxparalleloperations":
                if (int.TryParse(value, out var mp) && mp > 0)
                {
                    config.MaxParallelOperations = mp;
                    return true;
                }
                return false;
            case "profilestoponerror":
                if (bool.TryParse(value, out var pse))
                {
                    config.ProfileStopOnError = pse;
                    return true;
                }
                return false;
            case "autoupdate":
                if (bool.TryParse(value, out var au))
                {
                    config.AutoUpdate = au;
                    return true;
                }
                return false;
            case "updatechannel":
                config.UpdateChannel = value;
                return true;
            case "updatecheckfrequency":
                config.UpdateCheckFrequency = value;
                return true;
            case "includeprerelease":
                if (bool.TryParse(value, out var ip))
                {
                    config.IncludePrerelease = ip;
                    return true;
                }
                return false;
            case "securityrole":
                if (IsValidSecurityRole(value))
                {
                    config.SecurityRole = value.ToLowerInvariant();
                    return true;
                }
                return false;
            case "securitystrictness":
                if (IsValidSecurityStrictness(value))
                {
                    config.SecurityStrictness = value.ToLowerInvariant();
                    return true;
                }
                return false;
            case "protectedpaths":
                // 用 ';' 分隔多值; 简化处理, 不允许路径本身含 ';'。
                config.ProtectedPaths = value
                    .Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                    .ToList();
                return true;
            case "executionpolicy":
                // ADR-0054 §10: 校验 ExecutionPolicy 枚举值。
                if (IsValidExecutionPolicy(value))
                {
                    config.ExecutionPolicy = value;
                    return true;
                }
                return false;
            default:
                return false;
        }
    }

    private static bool IsValidSecurityRole(string value)
    {
        return value.Equals("user", StringComparison.OrdinalIgnoreCase)
            || value.Equals("admin", StringComparison.OrdinalIgnoreCase)
            || value.Equals("restricted", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsValidSecurityStrictness(string value)
    {
        return value.Equals("lax", StringComparison.OrdinalIgnoreCase)
            || value.Equals("default", StringComparison.OrdinalIgnoreCase)
            || value.Equals("strict", StringComparison.OrdinalIgnoreCase)
            || value.Equals("paranoid", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsValidExecutionPolicy(string value)
    {
        return value.Equals("Restricted", StringComparison.OrdinalIgnoreCase)
            || value.Equals("RemoteSigned", StringComparison.OrdinalIgnoreCase)
            || value.Equals("Unrestricted", StringComparison.OrdinalIgnoreCase)
            || value.Equals("Bypass", StringComparison.OrdinalIgnoreCase);
    }
}
