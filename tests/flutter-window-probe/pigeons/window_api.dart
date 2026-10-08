import 'package:pigeon/pigeon.dart';

enum ProbeOperation {
  initialize,
  inspect,
  armFrame,
  presentFrame,
  setInputMode,
  setUnderlayColor,
  clickSample,
  sendKey,
  focusUnderlay,
  showInteractive,
  hide,
  closeToHide,
  setTopmost,
  sendF10,
  capture,
  finish,
  retireCore,
}

enum ProbeInputMode { interactive, passThrough }

class ProbeCommand {
  ProbeCommand({
    required this.operation,
    required this.revision,
    required this.inputMode,
    required this.value,
    required this.devicePixelRatio,
    required this.text,
  });
  ProbeOperation operation;
  int revision;
  ProbeInputMode inputMode;
  int value;
  double devicePixelRatio;
  String text;
}

class ProbeSnapshot {
  ProbeSnapshot({
    required this.revision,
    required this.frameRevision,
    required this.processId,
    required this.topHwnd,
    required this.childHwnd,
    required this.targetProcessId,
    required this.targetHwnd,
    required this.visible,
    required this.inputMode,
    required this.topmost,
    required this.foregroundHwnd,
    required this.foregroundProcessId,
    required this.focusHwnd,
    required this.flutterMouseDown,
    required this.flutterMouseUp,
    required this.targetMouseDown,
    required this.targetMouseUp,
    required this.flutterKeyDown,
    required this.targetKeyDown,
    required this.hotkeyCount,
    required this.dpi,
    required this.devicePixelRatio,
    required this.topAboveTarget,
    required this.sampleHitHwnd,
    required this.sampleHitProcessId,
    required this.pixels,
    required this.diagnosticsJson,
  });
  int revision;
  int frameRevision;
  int processId;
  int topHwnd;
  int childHwnd;
  int targetProcessId;
  int targetHwnd;
  bool visible;
  ProbeInputMode inputMode;
  bool topmost;
  int foregroundHwnd;
  int foregroundProcessId;
  int focusHwnd;
  int flutterMouseDown;
  int flutterMouseUp;
  int targetMouseDown;
  int targetMouseUp;
  int flutterKeyDown;
  int targetKeyDown;
  int hotkeyCount;
  int dpi;
  double devicePixelRatio;
  bool topAboveTarget;
  int sampleHitHwnd;
  int sampleHitProcessId;
  List<int> pixels;
  String diagnosticsJson;
}

enum LifecycleOperation {
  startPrimary,
  inspect,
  secondaryShow,
  secondaryToggle,
  secondaryExit,
  secondaryInvalid,
  setPassThrough,
  hidePrimary,
  clickTray,
  openTrayMenu,
  trayMenuShow,
  trayMenuPassthrough,
  trayMenuExit,
  clickPrimary,
  sendFocusKey,
  abort,
}

enum LifecycleControlAction { show, toggle, exit }

enum LifecycleTrayAction { activate, show, passthrough, exit }

class LifecycleCommand {
  LifecycleCommand({required this.operation, required this.generation});
  LifecycleOperation operation;
  int generation;
}

class LifecycleUiEvidence {
  LifecycleUiEvidence({
    required this.gitSha,
    required this.ready,
    required this.pointerDown,
    required this.keyDown,
    required this.focused,
    required this.sequence,
  });
  String gitSha;
  bool ready;
  int pointerDown;
  int keyDown;
  bool focused;
  int sequence;
}

class LifecycleSnapshot {
  LifecycleSnapshot({
    required this.generation,
    required this.observationSequence,
    required this.controllerProcessId,
    this.primaryProcessId,
    this.primaryExitCode,
    required this.primaryAlive,
    required this.primaryExecutableMatched,
    required this.primaryWindowCount,
    required this.nativeGitSha,
    required this.primaryGitSha,
    this.topHwnd,
    this.childHwnd,
    this.visible,
    this.inputMode,
    required this.foregroundHwnd,
    required this.foregroundProcessId,
    this.focusHwnd,
    required this.mouseDown,
    required this.mouseUp,
    required this.keyDown,
    required this.uiReady,
    required this.uiPointerDown,
    required this.uiKeyDown,
    required this.uiFocused,
    required this.uiSequence,
    required this.trayAdded,
    required this.trayVersioned,
    required this.trayDeleted,
    required this.trayCallbackCount,
    required this.trayMenuCommandCount,
    this.trayLastAction,
    required this.port,
    required this.controlApplied,
    required this.controlRejected,
    this.controlLastAction,
    required this.receiveCalls,
    required this.pendingBytes,
    required this.connectionUpdates,
    required this.activations,
    required this.gamePid,
    required this.endpointMatched,
    required this.tokenMatched,
    this.secondaryPid,
    this.secondaryAction,
    required this.secondaryRequestId,
    required this.secondaryDone,
    this.secondaryExitCode,
    required this.secondaryBindError,
    required this.secondaryServerPid,
    required this.secondaryBytesSent,
    required this.secondaryWindowCount,
    required this.secondaryError,
    required this.diagnosticsJson,
  });
  int generation;
  int observationSequence;
  int controllerProcessId;
  int? primaryProcessId;
  int? primaryExitCode;
  bool primaryAlive;
  bool primaryExecutableMatched;
  int primaryWindowCount;
  String nativeGitSha;
  String primaryGitSha;
  int? topHwnd;
  int? childHwnd;
  bool? visible;
  ProbeInputMode? inputMode;
  int foregroundHwnd;
  int foregroundProcessId;
  int? focusHwnd;
  int mouseDown;
  int mouseUp;
  int keyDown;
  bool uiReady;
  int uiPointerDown;
  int uiKeyDown;
  bool uiFocused;
  int uiSequence;
  bool trayAdded;
  bool trayVersioned;
  bool trayDeleted;
  int trayCallbackCount;
  int trayMenuCommandCount;
  LifecycleTrayAction? trayLastAction;
  int port;
  int controlApplied;
  int controlRejected;
  LifecycleControlAction? controlLastAction;
  int receiveCalls;
  int pendingBytes;
  int connectionUpdates;
  int activations;
  int gamePid;
  bool endpointMatched;
  bool tokenMatched;
  int? secondaryPid;
  LifecycleControlAction? secondaryAction;
  int secondaryRequestId;
  bool secondaryDone;
  int? secondaryExitCode;
  int secondaryBindError;
  int secondaryServerPid;
  int secondaryBytesSent;
  int secondaryWindowCount;
  int secondaryError;
  String diagnosticsJson;
}

@ConfigurePigeon(
  PigeonOptions(
    dartOut: 'lib/generated/window_api.g.dart',
    cppHeaderOut: 'windows/runner/window_api.g.h',
    cppSourceOut: 'windows/runner/window_api.g.cpp',
    cppOptions: CppOptions(namespace: 'mystia_window_probe'),
  ),
)
@HostApi()
abstract class WindowProbeHostApi {
  // Core observations are typed. JSON is reserved for additional diagnostic
  // context and never authorizes an operation, target, path or success result.
  @async
  ProbeSnapshot execute(ProbeCommand command);
}

@HostApi()
abstract class LifecycleHostApi {
  @async
  LifecycleSnapshot execute(LifecycleCommand command);
}

@HostApi()
abstract class LifecycleFixtureHostApi {
  int publishUi(LifecycleUiEvidence evidence);
}
