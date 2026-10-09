using MystiaStewardCompanion.Save;

namespace MystiaStewardCompanion.LocalApi;

/// <summary>
/// 本地 API 与进程内业务发布共同使用的纯托管 UI 目标解析边界。
/// </summary>
/// <remarks>
/// 这些实现从 LocalApiServer 原文件等价拆出，字段、格式限制及异常语义保持原协议。
/// 离线宿主直接链接本文件和生产目标值类型，使颜色、身份、布尔、整数及列表校验
/// 与实际 Mod 使用相同代码；解析过程不接触 Unity 对象、网络、文件或设备权威状态。
/// </remarks>
internal sealed partial class LocalApiServer
{
    private static readonly string[] UiPinningTargetFieldSuffixes =
    {
        "Kind",
        "Revision",
        "Color",
        "ListPinningEnabled",
        "RecipeVariantEnabled",
        "CookerHighlightEnabled",
        "SeatHighlightEnabled",
        "OrderHighlightEnabled",
        "TraceId",
        "OrderKey",
        "OrderLifecycleSequence",
        "DeskCode",
        "RecipeId",
        "IngredientIds",
        "ExtraIngredientIds",
        "BeverageId",
        "CookerTypeId",
    };

    private static RuntimeUiTargetSnapshot ReadUiPinningTarget(string query, int index)
    {
        var prefix = $"target{index}";
        var requiredKeys = UiPinningTargetFieldSuffixes.Select(suffix => prefix + suffix);
        var missing = requiredKeys.FirstOrDefault(key => !HasQueryParameter(query, key));
        if (missing != null)
        {
            throw new FormatException($"Missing required UI target parameter {missing}.");
        }

        var kindValue = ReadStringQuery(query, $"{prefix}Kind");
        var kind = kindValue switch
        {
            "rare" => RuntimeUiTargetKind.Rare,
            "normal" => RuntimeUiTargetKind.Normal,
            _ => throw new FormatException($"{prefix}Kind must be exactly rare or normal."),
        };
        var colorValue = ReadStringQuery(query, $"{prefix}Color");
        if (!RuntimeTargetHighlightColor.TryParseExactHex(colorValue, out var color))
        {
            throw new FormatException($"{prefix}Color must be exactly six uppercase hexadecimal RGB digits.");
        }

        return new RuntimeUiTargetSnapshot(
            kind,
            color,
            ReadRequiredExactBoolQuery(query, $"{prefix}ListPinningEnabled"),
            ReadRequiredExactBoolQuery(query, $"{prefix}RecipeVariantEnabled"),
            ReadRequiredExactBoolQuery(query, $"{prefix}CookerHighlightEnabled"),
            ReadRequiredExactBoolQuery(query, $"{prefix}SeatHighlightEnabled"),
            ReadRequiredExactBoolQuery(query, $"{prefix}OrderHighlightEnabled"),
            ReadStringQuery(query, $"{prefix}TraceId"),
            ReadStringQuery(query, $"{prefix}OrderKey"),
            ReadRequiredPositiveLongQuery(query, $"{prefix}OrderLifecycleSequence"),
            ReadRequiredNonNegativeIntQuery(query, $"{prefix}DeskCode"),
            ReadRequiredOptionalIdQuery(query, $"{prefix}RecipeId"),
            ReadExactNonNegativeIntListQuery(query, $"{prefix}IngredientIds", 12),
            ReadExactNonNegativeIntListQuery(query, $"{prefix}ExtraIngredientIds", 5),
            ReadRequiredOptionalIdQuery(query, $"{prefix}BeverageId"),
            ReadRequiredOptionalIdQuery(query, $"{prefix}CookerTypeId"),
            ReadStringQuery(query, $"{prefix}Revision"));
    }

    private static void ValidateUiPinningTargetParameters(string query, int targetCount)
    {
        foreach (var pair in query.Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            var separator = pair.IndexOf('=');
            var encodedName = separator < 0 ? pair : pair[..separator];
            var name = Uri.UnescapeDataString(encodedName.Replace("+", " ", StringComparison.Ordinal));
            if (string.Equals(name, "businessGeneration", StringComparison.Ordinal)
                || string.Equals(name, "targetCount", StringComparison.Ordinal))
            {
                continue;
            }
            if (!name.StartsWith("target", StringComparison.Ordinal))
            {
                throw new FormatException($"Unexpected UI target parameter {name}.");
            }

            var digitStart = "target".Length;
            var digitEnd = digitStart;
            while (digitEnd < name.Length && name[digitEnd] is >= '0' and <= '9') digitEnd++;
            if (digitEnd == digitStart
                || !int.TryParse(
                    name[digitStart..digitEnd],
                    System.Globalization.NumberStyles.None,
                    System.Globalization.CultureInfo.InvariantCulture,
                    out var index))
            {
                throw new FormatException($"Invalid indexed UI target parameter {name}.");
            }

            var suffix = name[digitEnd..];
            if (!UiPinningTargetFieldSuffixes.Contains(suffix, StringComparer.Ordinal)
                || !string.Equals(name, $"target{index}{suffix}", StringComparison.Ordinal))
            {
                throw new FormatException($"Invalid indexed UI target parameter {name}.");
            }
            if (index >= targetCount)
            {
                throw new FormatException($"UI target parameter {name} exceeds targetCount {targetCount}.");
            }
        }
    }

    private static bool ReadRequiredExactBoolQuery(string query, string key)
    {
        if (!HasQueryParameter(query, key))
        {
            throw new FormatException($"Missing required UI target parameter {key}.");
        }

        return ReadStringQuery(query, key) switch
        {
            "true" => true,
            "false" => false,
            _ => throw new FormatException($"{key} must be exactly true or false."),
        };
    }

    private static long ReadRequiredPositiveLongQuery(string query, string key)
    {
        var raw = ReadRequiredAsciiDecimalQuery(query, key);
        if (!long.TryParse(
                raw,
                System.Globalization.NumberStyles.None,
                System.Globalization.CultureInfo.InvariantCulture,
                out var value)
            || value <= 0)
        {
            throw new FormatException($"{key} must be a positive 64-bit ASCII decimal integer.");
        }
        return value;
    }

    private static int ReadRequiredNonNegativeIntQuery(string query, string key)
    {
        var raw = ReadRequiredAsciiDecimalQuery(query, key);
        if (!int.TryParse(
                raw,
                System.Globalization.NumberStyles.None,
                System.Globalization.CultureInfo.InvariantCulture,
                out var value))
        {
            throw new FormatException($"{key} must be a non-negative 32-bit ASCII decimal integer.");
        }
        return value;
    }

    private static int ReadRequiredOptionalIdQuery(string query, string key)
    {
        if (!HasQueryParameter(query, key))
        {
            throw new FormatException($"Missing required UI target parameter {key}.");
        }

        var raw = ReadStringQuery(query, key);
        return raw == "-1" ? -1 : ReadRequiredNonNegativeIntQuery(query, key);
    }

    private static string ReadRequiredAsciiDecimalQuery(string query, string key)
    {
        if (!HasQueryParameter(query, key))
        {
            throw new FormatException($"Missing required UI target parameter {key}.");
        }

        var raw = ReadStringQuery(query, key);
        if (raw.Length == 0 || raw.Any(character => character is < '0' or > '9'))
        {
            throw new FormatException($"{key} must contain ASCII decimal digits only.");
        }
        return raw;
    }

    private static int ReadIntQuery(string query, string key, int fallback)
    {
        return int.TryParse(ReadStringQuery(query, key), out var value) ? value : fallback;
    }

    private static string ReadStringQuery(string query, string key)
    {
        if (string.IsNullOrWhiteSpace(query)) return "";
        foreach (var pair in query.Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            var parts = pair.Split('=', 2);
            if (parts.Length == 0) continue;
            var name = Uri.UnescapeDataString(parts[0].Replace("+", " ", StringComparison.Ordinal));
            if (!string.Equals(name, key, StringComparison.OrdinalIgnoreCase)) continue;
            return parts.Length == 1
                ? ""
                : Uri.UnescapeDataString(parts[1].Replace("+", " ", StringComparison.Ordinal));
        }

        return "";
    }

    private static bool HasQueryParameter(string query, string key)
    {
        if (string.IsNullOrWhiteSpace(query)) return false;
        return query.Split('&', StringSplitOptions.RemoveEmptyEntries).Any(pair =>
        {
            var separator = pair.IndexOf('=');
            var encodedName = separator < 0 ? pair : pair[..separator];
            var name = Uri.UnescapeDataString(encodedName.Replace("+", " ", StringComparison.Ordinal));
            return string.Equals(name, key, StringComparison.OrdinalIgnoreCase);
        });
    }

    private static List<int> ReadExactNonNegativeIntListQuery(
        string query,
        string key,
        int maxCount)
    {
        var value = ReadStringQuery(query, key);
        if (value.Length == 0) return new List<int>();

        var parts = value.Split(',', StringSplitOptions.None);
        if (parts.Length > maxCount)
        {
            throw new ArgumentOutOfRangeException(key, $"{key} cannot contain more than {maxCount} values.");
        }

        var result = new List<int>(parts.Length);
        foreach (var part in parts)
        {
            if (part.Length == 0
                || part.Any(character => character is < '0' or > '9')
                || !int.TryParse(
                    part,
                    System.Globalization.NumberStyles.None,
                    System.Globalization.CultureInfo.InvariantCulture,
                    out var id))
            {
                throw new FormatException($"{key} must contain only comma-separated ASCII decimal ids without whitespace or signs.");
            }

            result.Add(id);
        }

        return result;
    }
}
