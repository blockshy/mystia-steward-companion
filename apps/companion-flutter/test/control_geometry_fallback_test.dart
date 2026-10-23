import 'control_geometry_test.dart' as geometry;

/// 本引擎不注册 Noto，保留真实雅黑作为第二候选。其混合中西文字形的自然行高与
/// EditableText 固定 strut 可不同，正是此前框体短 2px/3px 的可重复边界。
/// 复用真实 InputDecorator.containerOf 测量和全部选择/输入断言，不放宽误差。
void main() => geometry.runControlGeometryTests(loadPreferredFont: false);
