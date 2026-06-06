namespace MystiaSteward.Core;

public static class FoodTags
{
    public static readonly HashSet<string> All = new(StringComparer.Ordinal)
    {
        "肉", "水产", "素", "家常", "高级", "传说", "菌类",
        "实惠", "昂贵", "大份",
        "咸", "鲜", "甜", "辣", "苦", "酸",
        "重油", "清淡", "下酒", "饱腹", "山珍", "海味",
        "招牌", "和风", "西式", "中华",
        "力量涌现", "灼热", "凉爽", "猎奇", "文化底蕴", "不可思议",
        "小巧", "梦幻", "特产", "果味", "汤羹", "烧烤",
        "燃起来了", "毒", "适合拍照", "生",
    };

    public static string? NormalizeName(string value)
    {
        return value switch
        {
            "流行·喜爱" => "流行喜爱",
            "流行·厌恶" => "流行厌恶",
            _ => value,
        };
    }
}
