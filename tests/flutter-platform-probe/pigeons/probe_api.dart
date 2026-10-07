import 'package:pigeon/pigeon.dart';

@ConfigurePigeon(
  PigeonOptions(
    dartOut: 'lib/generated/probe_api.g.dart',
    cppHeaderOut: 'windows/runner/probe_api.g.h',
    cppSourceOut: 'windows/runner/probe_api.g.cpp',
    cppOptions: CppOptions(namespace: 'mystia_probe'),
  ),
)
@HostApi()
abstract class ProbeHostApi {
  // JSON is the separately versioned bootstrap wire format. Pigeon only moves
  // this bounded frame across the in-process platform boundary.
  @async
  String exchange(String command);
}
