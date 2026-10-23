import 'package:flutter/widgets.dart';

import 'game_media_scope.dart';
import 'steward_opacity.dart';
import 'steward_style.dart';

/// 图片身份使用独立命名空间；料理编号、游戏配方编号和现场客人编号不能互换。
/// 普客图片仅表示客群的确定性代表形象，不声称是当前随机皮肤或某一位现场客人。
enum EntityImageKind {
  food('food'),
  beverage('beverage'),
  ingredient('ingredient'),
  cooker('cooker'),
  rareGuest('rare-guest'),
  normalGroup('normal-group');

  const EntityImageKind(this.namespace);
  final String namespace;
}

/// 仅接受协议明确给出的非负整数身份，不把名称、数值字符串或浮点数猜成实体 ID。
int? entityImageId(Object? value) => value is int && value >= 0 ? value : null;

/// 统一实体小图。图像始终占据当前字号对应的同一方框，加载过程、缺图及解码错误不会让文字
/// 或控件跳动。旁边文字已经提供名称，图片不重复进入语义树，也不新增点击行为。
/// 图片只应用内容不透明度一次；原图标继承已处理过的前景色，不再次叠乘透明度。
class EntityImage extends StatelessWidget {
  const EntityImage({
    required this.kind,
    required this.id,
    required this.fallback,
    this.size = StewardImageSize.tag,
    this.color,
    super.key,
  });

  final EntityImageKind kind;
  final int? id;
  final IconData fallback;

  /// 100%字号下的设计尺寸；只在build中换算一次，父级不得再用固定小框压缩。
  final double size;
  final Color? color;

  @override
  Widget build(BuildContext context) {
    final extent = MediaQuery.textScalerOf(context).scale(size);
    Widget oldIcon() => Icon(fallback, size: extent, color: color);
    // 认证下载、来源隔离、摘要验证和代次失效都由资源控制器负责；展示层只接收
    // 当前有效代次的图片 Provider，不自行拼 URL、读取文件或按名称推测身份。
    final provider = id == null || id! < 0
        ? null
        : GameMediaScope.imageFor(context, kind.namespace, id!);
    return ExcludeSemantics(
      child: SizedBox.square(
        dimension: extent,
        child: provider == null
            ? oldIcon()
            : Image(
                // 人物原图可能远大于显示槽位，按当前字号与像素密度解码，避免列表滚动时
                // 把整张高分辨率立绘留在图像缓存；实际控件几何仍使用逻辑像素。
                image: ResizeImage.resizeIfNeeded(
                  (extent * MediaQuery.devicePixelRatioOf(context)).ceil(),
                  null,
                  provider,
                ),
                width: extent,
                height: extent,
                fit: BoxFit.contain,
                excludeFromSemantics: true,
                opacity: AlwaysStoppedAnimation(
                  context.stewardPaint.contentOpacity,
                ),
                frameBuilder: (context, child, frame, synchronous) =>
                    frame == null ? oldIcon() : child,
                errorBuilder: (context, error, stackTrace) => oldIcon(),
              ),
      ),
    );
  }
}

/// 名称行复用既有文字级别和首行基线，图片仅替换装饰图标，不固定整行高度。
/// 下拉项目与分组标题可使用同一实现；长中文仍在右侧自然换行。
class EntityName extends StatelessWidget {
  const EntityName(
    this.name, {
    required this.kind,
    required this.id,
    required this.fallback,
    this.style,
    super.key,
  });
  final String name;
  final EntityImageKind kind;
  final int? id;
  final IconData fallback;
  final TextStyle? style;

  @override
  Widget build(BuildContext context) => StewardBaselineRow(
    children: [
      StewardFirstLineCenter(
        textStyle: style,
        referenceText: name,
        child: EntityImage(
          kind: kind,
          id: id,
          fallback: fallback,
          size: StewardImageSize.name,
        ),
      ),
      const SizedBox(width: 6),
      Flexible(child: Text(name, style: style)),
    ],
  );
}
