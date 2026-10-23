/// 扩展工作区的本机模块目录。Tab 始终可访问，开关控制附加读取与人工操作，
/// 不进入共享业务 profile，也不改变任务料理优先、自动化或其他设备的配置。
class ExtensionModule {
  const ExtensionModule(
    this.id,
    this.preferenceKey,
    this.title,
    this.tabLabel,
    this.description,
  );
  final String id, preferenceKey, title, tabLabel, description;

  /// 只有明确持久化的 true 才启用；首次启动、缺字段和错误类型均保持关闭。
  bool enabled(Map<String, Object?> preferences) =>
      preferences[preferenceKey] == true;
}

const extensionModules = <ExtensionModule>[
  ExtensionModule(
    'missions',
    'missionListModuleEnabled',
    '任务列表',
    '任务',
    '读取追踪任务与可接取任务。关闭不影响任务料理优先。',
  ),
  ExtensionModule(
    'invitations',
    'rareGuestInvitationModuleEnabled',
    '稀客邀请',
    '稀客邀请',
    '读取日间候选，并按明确选择单独或批量邀请。',
  ),
  ExtensionModule(
    'inventory',
    'inventoryEditorModuleEnabled',
    '库存修改',
    '库存修改',
    '查看材料与酒水数量，确认后修改选中条目的库存。',
  ),
];
