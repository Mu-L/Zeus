using System.Globalization;
using System.Text.Json;

namespace Zeus;

/// <summary>
/// JSON 配置 <c>options</c> 对象读取工具。协议包通过它读取自己的配置字段，避免把协议细节扩散到核心配置模型。
/// </summary>
public static class ZeusConfigurationOptions
{
    /// <summary>返回指定选项是否存在。</summary>
    public static bool Contains(IReadOnlyDictionary<string, JsonElement> options, string name)
    {
        ArgumentNullException.ThrowIfNull(options);
        return options.ContainsKey(name);
    }

    /// <summary>读取字符串选项；数字和布尔值会按 JSON 文本转成字符串。</summary>
    public static string? GetString(
        IReadOnlyDictionary<string, JsonElement> options,
        string name,
        string? defaultValue = null,
        string? path = null)
    {
        if (!TryGet(options, name, out var value) || IsNull(value))
        {
            return defaultValue;
        }

        return value.ValueKind switch
        {
            JsonValueKind.String => value.GetString(),
            JsonValueKind.Number or JsonValueKind.True or JsonValueKind.False => value.ToString(),
            _ => throw InvalidType(path, name, "字符串、数字或布尔值")
        };
    }

    /// <summary>读取必填字符串选项。</summary>
    public static string RequireString(IReadOnlyDictionary<string, JsonElement> options, string name, string? path = null)
    {
        var value = GetString(options, name, null, path);
        if (string.IsNullOrWhiteSpace(value))
        {
            throw new ZeusException($"{Label(path, name)} 不能为空。");
        }

        return value;
    }

    /// <summary>读取 32 位整数选项。字符串可使用十进制或 0x 前缀十六进制。</summary>
    public static int GetInt32(
        IReadOnlyDictionary<string, JsonElement> options,
        string name,
        int defaultValue = 0,
        string? path = null)
        => GetNullableInt32(options, name, path) ?? defaultValue;

    /// <summary>读取可空 32 位整数选项。字符串可使用十进制或 0x 前缀十六进制。</summary>
    public static int? GetNullableInt32(IReadOnlyDictionary<string, JsonElement> options, string name, string? path = null)
    {
        if (!TryGet(options, name, out var value) || IsNull(value))
        {
            return null;
        }

        if (value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out var number))
        {
            return number;
        }

        if (value.ValueKind == JsonValueKind.String)
        {
            var text = value.GetString()?.Trim();
            if (string.IsNullOrEmpty(text))
            {
                throw new ZeusException($"{Label(path, name)} 不能为空字符串。");
            }

            try
            {
                return text.StartsWith("0x", StringComparison.OrdinalIgnoreCase)
                    ? int.Parse(text[2..], NumberStyles.HexNumber, CultureInfo.InvariantCulture)
                    : int.Parse(text, NumberStyles.Integer, CultureInfo.InvariantCulture);
            }
            catch (Exception ex) when (ex is FormatException or OverflowException)
            {
                throw new ZeusException($"{Label(path, name)} 必须是整数，或形如 \"0x10\" 的十六进制字符串。", ex);
            }
        }

        throw InvalidType(path, name, "整数或整数格式字符串");
    }

    /// <summary>读取双精度浮点选项。</summary>
    public static double GetDouble(
        IReadOnlyDictionary<string, JsonElement> options,
        string name,
        double defaultValue = 0,
        string? path = null)
        => GetNullableDouble(options, name, path) ?? defaultValue;

    /// <summary>读取可空双精度浮点选项。</summary>
    public static double? GetNullableDouble(IReadOnlyDictionary<string, JsonElement> options, string name, string? path = null)
    {
        if (!TryGet(options, name, out var value) || IsNull(value))
        {
            return null;
        }

        if (value.ValueKind == JsonValueKind.Number && value.TryGetDouble(out var number))
        {
            return number;
        }

        if (value.ValueKind == JsonValueKind.String
            && double.TryParse(value.GetString(), NumberStyles.Float, CultureInfo.InvariantCulture, out number))
        {
            return number;
        }

        throw InvalidType(path, name, "数值或数值格式字符串");
    }

    /// <summary>读取布尔选项。字符串 true/false 也被接受。</summary>
    public static bool GetBoolean(
        IReadOnlyDictionary<string, JsonElement> options,
        string name,
        bool defaultValue = false,
        string? path = null)
    {
        if (!TryGet(options, name, out var value) || IsNull(value))
        {
            return defaultValue;
        }

        return value.ValueKind switch
        {
            JsonValueKind.True => true,
            JsonValueKind.False => false,
            JsonValueKind.String when bool.TryParse(value.GetString(), out var parsed) => parsed,
            _ => throw InvalidType(path, name, "布尔值")
        };
    }

    /// <summary>读取选项指纹，供热更新判断是否需要重建拓扑。</summary>
    public static string Fingerprint(IReadOnlyDictionary<string, JsonElement> options)
    {
        ArgumentNullException.ThrowIfNull(options);
        return string.Join(';', options
            .OrderBy(pair => pair.Key, StringComparer.OrdinalIgnoreCase)
            .Select(pair => pair.Key + '=' + pair.Value.ToString()));
    }

    private static bool TryGet(IReadOnlyDictionary<string, JsonElement> options, string name, out JsonElement value)
    {
        ArgumentNullException.ThrowIfNull(options);
        if (options.TryGetValue(name, out value))
        {
            return true;
        }

        foreach (var pair in options)
        {
            if (string.Equals(pair.Key, name, StringComparison.OrdinalIgnoreCase))
            {
                value = pair.Value;
                return true;
            }
        }

        value = default;
        return false;
    }

    private static bool IsNull(JsonElement value)
        => value.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined;

    private static ZeusException InvalidType(string? path, string name, string expected)
        => new($"{Label(path, name)} 必须是{expected}。");

    private static string Label(string? path, string name)
        => string.IsNullOrWhiteSpace(path) ? name : $"{path}.options.{name}";
}
