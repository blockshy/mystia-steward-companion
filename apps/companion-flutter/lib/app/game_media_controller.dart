import 'dart:async';

import 'package:companion_client/companion_client.dart';
import 'package:crypto/crypto.dart';
import 'package:flutter/foundation.dart';
import 'package:flutter/painting.dart';

import 'workbench_model.dart';

/// 一次连接内的游戏图像仓库。宿主负责提取和耐久缓存，客户端只读取清单及可见图片。
/// 请求并发、清单容量、PNG 尺寸及 SHA256 均受限；不把凭据写入图片 URL 或磁盘。
/// Unity 对象不进入本层，跨设备只传明确的命名空间、整数编号和 PNG 字节。
class GameMediaController extends ChangeNotifier {
  CompanionApi? _api;
  bool _disposed = false, _reloading = false;
  int _epoch = 0, _imageEpoch = 0, _revision = 0, _downloads = 0;
  Future<bool>? _reading;
  String? _generation;
  DataMap _status = const {
    'phase': 'verifying',
    'completed': 0,
    'total': 0,
    'message': '正在确认游戏图片缓存',
  };
  Map<String, DataMap> _items = const {}, _recipes = const {};
  final _images = <String, MemoryImage>{};
  final _queue = <String>[];
  final _requested = <String>{}, _failed = <String>{};

  DataMap get status => Map.unmodifiable({
    ..._status,
    'loadedImages': _images.length,
    'failedImages': _failed.length,
  });
  List<DataMap> get items => List.unmodifiable(_items.values);
  Map<String, DataMap> get recipes => _recipes;
  int get revision => _revision;
  bool get ready => _status['phase'] == 'ready' && _generation != null;

  /// 换连接、失连或进程会话改变时立即撤回全部映射。正在读取的旧请求可以结束，
  /// 但 epoch 不一致时不能提交结果，也不能占用新一代的并发计数。
  void bind(CompanionApi? api) {
    _epoch++;
    _api = api;
    _reading = null;
    _reloading = false;
    _clearImages();
    _status = const {
      'phase': 'verifying',
      'completed': 0,
      'total': 0,
      'message': '正在确认游戏图片缓存',
    };
    _changed();
  }

  void _clearImages() {
    // 同一宿主代次也可能经历暂时失败再恢复；独立图片作业代次阻止迟到旧图提交。
    // _downloads 保留真实在途数，换代后仍最多四个 HTTP；旧请求由超时正常结束。
    _imageEpoch++;
    _generation = null;
    _items = const {};
    _recipes = const {};
    // 撤回原始 Provider 缓存；组件派生的 ResizeImage 解码键仍由 Flutter 的有界
    // ImageCache 按 LRU 回收。旧 Provider 不再公开，因此不会在新代次重新显示。
    for (final provider in _images.values) {
      unawaited(provider.evict());
    }
    _images.clear();
    _queue.clear();
    _requested.clear();
    _failed.clear();
  }

  /// 会话每轮只调用一次；同一时刻其它资源页重试共享结果，避免重复拉取清单。
  Future<bool> synchronize() {
    if (_disposed || _api == null || _reloading) return Future.value(false);
    final existing = _reading;
    if (existing != null) return existing;
    late final Future<bool> operation;
    operation = _synchronize(_api!, _epoch).whenComplete(() {
      if (identical(_reading, operation)) _reading = null;
    });
    _reading = operation;
    return operation;
  }

  bool _current(int epoch, CompanionApi api) =>
      !_disposed && epoch == _epoch && identical(api, _api);

  Future<bool> _synchronize(CompanionApi api, int epoch) async {
    try {
      final status = await api.read('/game-media/status');
      if (!_current(epoch, api)) return false;
      final phase = status['phase'];
      if (!const {
            'waiting',
            'verifying',
            'extracting',
            'ready',
            'failed',
          }.contains(phase) ||
          status['completed'] is! int ||
          status['total'] is! int ||
          (status['completed'] as int) < 0 ||
          (status['total'] as int) < 0 ||
          (status['completed'] as int) > (status['total'] as int)) {
        throw const FormatException('游戏资源进度格式无效。');
      }
      final generation = status['generation'];
      if (phase != 'ready') {
        if (_generation != null) {
          _epoch++;
          _clearImages();
        }
        if (!mapEquals(_status, status)) {
          _status = Map.unmodifiable(status);
          _changed();
        }
        return false;
      }
      if (generation is! String ||
          generation.isEmpty ||
          generation.length > 96) {
        throw const FormatException('游戏资源代次无效。');
      }
      if (generation != _generation) {
        // 先撤回旧图再读取新清单，绝不把新游戏编号套用到旧图片。
        _clearImages();
        // 宿主已经就绪时这里只同步并校验清单，未执行任何纹理提取。
        // 保留独立核验状态，在统一初始化面板说明当前仅校验目录而非重新提取。
        // ready 仍要等全部清单通过校验，不能提前放行业务页面或自动化租约。
        _status = {...status, 'phase': 'verifying', 'message': '正在验证游戏资源目录'};
        _changed();
        final manifest = await api.read('/game-media/manifest');
        if (!_current(epoch, api)) return false;
        final parsed = _parseManifest(manifest, generation);
        _items = parsed.$1;
        _recipes = parsed.$2;
        _generation = generation;
      }
      if (!mapEquals(_status, status)) {
        _status = Map.unmodifiable(status);
        _changed();
      }
      return ready;
    } catch (cause) {
      if (_current(epoch, api)) {
        _clearImages();
        _status = {
          'phase': 'failed',
          'completed': 0,
          'total': 0,
          'message': '游戏资源读取失败',
          'error': cause.toString().replaceAll(api.connection.token, '[已隐藏]'),
        };
        _changed();
      }
      return false;
    }
  }

  /// 严格验证全部目录再一次发布；重复身份、超限或损坏不能局部提交成 ready。
  /// 配方索引独立于显示名称，保留原材料顺序和重复项。
  (Map<String, DataMap>, Map<String, DataMap>) _parseManifest(
    DataMap data,
    String generation,
  ) {
    if (data['version'] != 1 ||
        data['generation'] != generation ||
        data['items'] is! List ||
        data['recipes'] is! List) {
      throw const FormatException('游戏资源目录版本或代次不匹配。');
    }
    final entries = values(data['items']);
    if (entries.length > 2048 || values(data['recipes']).length > 4096) {
      throw const FormatException('游戏资源目录超出数量限制。');
    }
    const kinds = {
      'food',
      'beverage',
      'ingredient',
      'cooker',
      'rare-guest',
      'normal-group',
    };
    final items = <String, DataMap>{}, recipes = <String, DataMap>{};
    var bytes = 0;
    for (final raw in entries) {
      final item = object(raw);
      final id = item['id'], kind = item['kind'], length = item['byteLength'];
      final width = item['width'], height = item['height'];
      if (id is! int ||
          id < 0 ||
          !kinds.contains(kind) ||
          item['key'] != '$kind:$id' ||
          length is! int ||
          length <= 0 ||
          length > 256 * 1024 ||
          width is! int ||
          height is! int ||
          width < 1 ||
          height < 1 ||
          width > 512 ||
          height > 512 ||
          item['sha256'] is! String ||
          !RegExp(r'^[a-f0-9]{64}$').hasMatch(item['sha256'] as String) ||
          item['name'] is! String ||
          items.containsKey(item['key'])) {
        throw const FormatException('游戏图片身份、大小或摘要无效。');
      }
      bytes += length;
      if (bytes > 32 * 1024 * 1024)
        throw const FormatException('游戏图片总量超过 32 MiB。');
      items[item['key'] as String] = Map.unmodifiable(item);
    }
    for (final raw in values(data['recipes'])) {
      final recipe = object(raw);
      final food = recipe['foodId'],
          id = recipe['recipeId'],
          cooker = recipe['cookerType'];
      final ingredients = recipe['ingredientIds'];
      if (food is! int ||
          food < 0 ||
          id is! int ||
          id < 0 ||
          ingredients is! List ||
          ingredients.length > 32 ||
          ingredients.any((entry) => entry is! int || entry < 0) ||
          cooker is! int ||
          cooker < 1 ||
          cooker > 5 ||
          recipes.containsKey('$food:$id')) {
        throw const FormatException('游戏配方图像索引无效。');
      }
      recipes['$food:$id'] = Map.unmodifiable({
        ...recipe,
        'ingredientIds': List<int>.unmodifiable(ingredients.cast<int>()),
      });
    }
    return (Map.unmodifiable(items), Map.unmodifiable(recipes));
  }

  /// 构建期间只排队，不同步通知组件；最多四个可见图片请求并发。
  /// 失败的同代图片不在每次 rebuild 重试，用户可从资源管理明确重新读取。
  ImageProvider<Object>? imageFor(String namespace, int id) {
    if (!ready || id < 0) return null;
    final key = '$namespace:$id';
    if (!_items.containsKey(key)) return null;
    final image = _images[key];
    if (image != null) return image;
    if (_requested.add(key)) {
      _queue.add(key);
      scheduleMicrotask(_drain);
    }
    return null;
  }

  void _drain() {
    final api = _api, generation = _generation;
    if (_disposed || !ready || api == null || generation == null) return;
    while (_downloads < 4 && _queue.isNotEmpty) {
      final key = _queue.removeAt(0);
      _downloads++;
      unawaited(
        _download(api, _epoch, _imageEpoch, generation, key, _items[key]!),
      );
    }
  }

  Future<void> _download(
    CompanionApi api,
    int epoch,
    int imageEpoch,
    String generation,
    String key,
    DataMap item,
  ) async {
    bool current() =>
        _current(epoch, api) &&
        imageEpoch == _imageEpoch &&
        generation == _generation;
    try {
      final bytes = await api.gameImage(generation: generation, key: key);
      if (!current()) return;
      const signature = [137, 80, 78, 71, 13, 10, 26, 10];
      final header = bytes.length >= 24 ? ByteData.sublistView(bytes) : null;
      if (bytes.length != item['byteLength'] ||
          header == null ||
          !listEquals(bytes.sublist(0, 8), signature) ||
          header.getUint32(12) != 0x49484452 ||
          header.getUint32(16) != item['width'] ||
          header.getUint32(20) != item['height'] ||
          sha256.convert(bytes).toString() != item['sha256']) {
        throw const FormatException('游戏图片校验失败。');
      }
      _images[key] = MemoryImage(bytes);
    } catch (_) {
      if (current()) _failed.add(key);
    } finally {
      _downloads--;
      if (current()) _changed();
      _drain();
    }
  }

  /// 重新读取由主设备明确发起一次，不自动重复 POST。请求期间撤回当前映射，
  /// 超时仍按未知结果处理，随后 GET 确认宿主实际进度。
  Future<void> reload(
    int authorityRevision, {
    Future<void> Function()? beforeReload,
  }) async {
    final api = _api;
    if (api == null || _disposed || _reloading) return;
    _epoch++;
    final epoch = _epoch;
    _reading = null;
    _reloading = true;
    _clearImages();
    _status = const {
      'phase': 'extracting',
      'completed': 0,
      'total': 0,
      'message': '正在重新读取游戏资源',
    };
    _changed();
    try {
      // 必须先将本控制器切入 extracting 并阻止 prepareResources，再等待旧租约
      // 释放；否则释放 HTTP 等待期间的新一轮轮询可能读到旧 ready 并重新续租。
      await beforeReload?.call();
      if (!_current(epoch, api)) return;
      await api.write(
        '/game-media/reload',
        authorityRevision: authorityRevision,
      );
    } finally {
      if (_current(epoch, api)) {
        _reloading = false;
        await synchronize();
      }
    }
  }

  void _changed() {
    if (!_disposed) {
      _revision++;
      notifyListeners();
    }
  }

  @override
  void dispose() {
    _disposed = true;
    _epoch++;
    _clearImages();
    super.dispose();
  }
}
