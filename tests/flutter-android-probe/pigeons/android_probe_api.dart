import 'package:pigeon/pigeon.dart';

@ConfigurePigeon(
  PigeonOptions(
    dartOut: 'lib/generated/android_probe_api.g.dart',
    kotlinOut: 'android/app/src/main/kotlin/com/tyukki/mystia/steward/companion/p0probe/AndroidProbeApi.g.kt',
    kotlinOptions: KotlinOptions(
      package: 'com.tyukki.mystia.steward.companion.p0probe',
    ),
  ),
)
@HostApi()
abstract class AndroidProbeApi {
  String launchConfiguration();
  String runtimeFacts();
  @async
  String nativeHttp(String endpoint, String nonce);
  String publishReport(String report);
}
