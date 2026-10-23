using System.Text.RegularExpressions;

namespace MystiaStewardCompanion.Core;

/// <summary>
/// 游戏本地化说明转为客户端纯文字。只处理已知Unity富文本样式及换行，保留正文、
/// 普通大小比较符和未知尖括号内容；不以通配的&lt;.*&gt;规则吞掉用户需要阅读的信息。
/// 此方法是纯托管处理，可由任务条件、厨具效果等展示投影共享，不读取任何游戏对象。
/// </summary>
internal static class GameDisplayText
{
    internal static string Normalize(string? text)
    {
        if (string.IsNullOrWhiteSpace(text) || text.Length > 16384) return "";
        var plain = Regex.Replace(text, @"<br\s*/?>", "\n",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        return Regex.Replace(plain,
            @"</?(?:b|i|u|s|color|size|font|mark|align|indent|line-height|voffset)(?:=[^<>]*)?>",
            "", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant).Replace("\r\n", "\n").Trim();
    }
}
