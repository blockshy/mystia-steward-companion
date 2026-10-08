package com.tyukki.mystia.steward.companion.p0probe

import android.Manifest
import android.content.pm.ApplicationInfo
import android.content.pm.PackageManager
import android.os.Build
import android.os.Process
import android.security.NetworkSecurityPolicy
import android.system.Os
import android.system.OsConstants
import io.flutter.embedding.android.FlutterActivity
import io.flutter.embedding.engine.FlutterEngine
import java.net.HttpURLConnection
import java.net.Proxy
import java.net.URI
import java.io.File
import java.util.concurrent.Executors
import org.json.JSONObject
import kotlin.coroutines.resume
import kotlin.coroutines.suspendCoroutine

class MainActivity : FlutterActivity(), AndroidProbeApi {
    private val executor = Executors.newSingleThreadExecutor()
    override fun configureFlutterEngine(flutterEngine: FlutterEngine) {
        super.configureFlutterEngine(flutterEngine)
        AndroidProbeApi.setUp(flutterEngine.dartExecutor.binaryMessenger, this)
    }
    override fun onDestroy() {
        flutterEngine?.let { AndroidProbeApi.setUp(it.dartExecutor.binaryMessenger, null) }
        executor.shutdownNow()
        super.onDestroy()
    }
    override fun launchConfiguration(): String = JSONObject().apply {
        put("runId", intent.getStringExtra("run_id") ?: "")
        put("nonce", intent.getStringExtra("nonce") ?: "")
        put("endpoint", intent.getStringExtra("endpoint") ?: "")
        put("expectDenied", intent.getBooleanExtra("expect_denied", false))
    }.toString()

    @Suppress("DEPRECATION")
    override fun runtimeFacts(): String = JSONObject().apply {
        put("sdk", Build.VERSION.SDK_INT)
        put("release", Build.VERSION.RELEASE)
        put("fingerprint", Build.FINGERPRINT)
        put("supportedAbis", org.json.JSONArray(Build.SUPPORTED_ABIS.toList()))
        put("process64Bit", Process.is64Bit())
        put("processAbi", if (Process.is64Bit()) Build.SUPPORTED_64_BIT_ABIS.first() else Build.SUPPORTED_32_BIT_ABIS.first())
        put("pageSize", Os.sysconf(OsConstants._SC_PAGESIZE))
        put("targetSdk", applicationInfo.targetSdkVersion)
        put("debuggable", applicationInfo.flags and ApplicationInfo.FLAG_DEBUGGABLE != 0)
        put("cleartextPermitted", NetworkSecurityPolicy.getInstance().isCleartextTrafficPermitted)
        put("nearbyWifiGranted", Build.VERSION.SDK_INT >= 33 && checkSelfPermission(Manifest.permission.NEARBY_WIFI_DEVICES) == PackageManager.PERMISSION_GRANTED)
        // PackageManager includes OS split permissions, not just APK declarations.
        val effective = packageManager.getPackageInfo(packageName, PackageManager.GET_PERMISSIONS).requestedPermissions ?: emptyArray()
        put("accessLocalNetworkEffective", effective.contains("android.permission.ACCESS_LOCAL_NETWORK"))
        put("accessLocalNetworkGranted", Build.VERSION.SDK_INT >= 37 && checkSelfPermission("android.permission.ACCESS_LOCAL_NETWORK") == PackageManager.PERMISSION_GRANTED)
        put("packageName", packageName)
        put("pid", Process.myPid())
    }.toString()

    // Numeric local IPv4 only. Never invoke DNS, a proxy, or automatic redirects.
    private fun localUri(endpoint: String): URI? {
        if (endpoint.any { it.code <= 32 || it.code == 127 }) return null
        val uri = try { URI(endpoint) } catch (_: Exception) { return null }
        if (uri.scheme != "http" || uri.userInfo != null || uri.fragment != null || uri.port !in 1..65535) return null
        val host = uri.host ?: return null
        val parts = host.split('.')
        if (parts.size != 4 || parts.any { !Regex("0|[1-9][0-9]{0,2}").matches(it) }) return null
        val octets = parts.map { it.toInt() }
        if (octets.any { it !in 0..255 }) return null
        val local = octets[0] == 127 || octets[0] == 10 ||
            (octets[0] == 172 && octets[1] in 16..31) ||
            (octets[0] == 192 && octets[1] == 168) || (octets[0] == 169 && octets[1] == 254)
        return if (local) uri else null
    }
    override suspend fun nativeHttp(endpoint: String, nonce: String): String = suspendCoroutine { continuation ->
        val uri = localUri(endpoint)
        if (uri == null || !Regex("[a-f0-9]{32}").matches(nonce)) {
            continuation.resume(JSONObject().put("outcome", "invalidEndpoint").toString())
            return@suspendCoroutine
        }
        executor.execute {
            val report = JSONObject()
            var connection: HttpURLConnection? = null
            try {
                connection = uri.toURL().openConnection(Proxy.NO_PROXY) as HttpURLConnection
                connection.apply {
                    instanceFollowRedirects = false
                    connectTimeout = 2500
                    readTimeout = 2500
                    requestMethod = "GET"
                    useCaches = false
                    setRequestProperty("X-Mystia-Steward-Companion-Token", nonce)
                }
                val status = connection.responseCode
                report.put("outcome", "response").put("status", status)
                if (status == 200) {
                    val bytes = connection.inputStream.use { stream ->
                        val output = java.io.ByteArrayOutputStream()
                        val chunk = ByteArray(1024)
                        while (true) {
                            val count = stream.read(chunk)
                            if (count < 0) break
                            require(output.size() + count <= 16384) { "responseTooLarge" }
                            output.write(chunk, 0, count)
                        }
                        output.toByteArray()
                    }
                    report.put("body", String(bytes, Charsets.UTF_8))
                }
            } catch (error: Exception) {
                report.put("outcome", "ioFailure").put("errorType", error.javaClass.simpleName)
            } finally { connection?.disconnect() }
            runOnUiThread { continuation.resume(report.toString()) }
        }
    }
    override fun publishReport(report: String): String {
        require(report.toByteArray().size <= 32768)
        val parsed = JSONObject(report)
        val runId = intent.getStringExtra("run_id") ?: ""
        val nonce = intent.getStringExtra("nonce") ?: ""
        require(Regex("[a-z0-9][a-z0-9-]{7,63}").matches(runId))
        require(parsed.getString("runId") == runId && parsed.getString("nonce") == nonce)
        val directory = requireNotNull(getExternalFilesDir(null))
        val target = File(directory, "$runId.json")
        val pending = File(directory, "$runId.pending")
        require(!target.exists() && pending.createNewFile()) { "Report already exists" }
        pending.outputStream().use { it.write(report.toByteArray(Charsets.UTF_8)) }
        require(pending.renameTo(target)) { "Report publication failed" }
        return target.name
    }
}
