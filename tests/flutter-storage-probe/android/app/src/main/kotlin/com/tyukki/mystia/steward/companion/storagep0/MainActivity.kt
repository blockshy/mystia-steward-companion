package com.tyukki.mystia.steward.companion.storagep0

import android.os.Process
import io.flutter.embedding.android.FlutterActivity
import io.flutter.embedding.engine.FlutterEngine
import org.json.JSONObject
import java.io.File

class MainActivity : FlutterActivity(), StorageProbeApi {
    private var resumed = false
    private var stopped = false
    private var pendingReport: String? = null
    override fun onResume() { super.onResume(); resumed = true }
    override fun onStop() { super.onStop(); stopped = true }
    private fun runId(): String = intent.getStringExtra("run-id")?.also {
        require(Regex("[A-Za-z0-9][A-Za-z0-9_-]{0,79}").matches(it))
    } ?: error("Explicit isolated run-id required")
    private fun phase(): String = intent.getStringExtra("phase")?.also {
        require(it in listOf("write", "read", "delete", "confirm"))
    } ?: error("Explicit storage phase required")
    override fun configureFlutterEngine(engine: FlutterEngine) {
        super.configureFlutterEngine(engine)
        StorageProbeApi.setUp(engine.dartExecutor.binaryMessenger, this)
    }
    override fun launchConfiguration(): String = JSONObject(mapOf(
        "runId" to runId(), "phase" to phase(), "nativePid" to Process.myPid(),
        "sdk" to android.os.Build.VERSION.SDK_INT,
        "privateRoot" to applicationInfo.dataDir,
        "debuggable" to ((applicationInfo.flags and 2) != 0)
    )).toString()
    override fun publishReport(report: String) {
        require(resumed && !isFinishing && !isChangingConfigurations && pendingReport == null)
        require(report.toByteArray(Charsets.UTF_8).size <= 65536)
        val parsed = JSONObject(report)
        require(parsed.getString("runId") == runId() && parsed.getString("phase") == phase())
        require(parsed.getInt("pid") == Process.myPid())
        pendingReport = report
        finishAndRemoveTask()
    }
    override fun onDestroy() {
        super.onDestroy()
        val report = pendingReport ?: return
        require(stopped && isFinishing && !isChangingConfigurations)
        // Framework onStop processing waits for queued SharedPreferences.apply
        // before this normal destruction. Never use Process.killProcess/exit.
        val directory = File(requireNotNull(getExternalFilesDir(null)), "storage-p0/${runId()}")
        require(directory.mkdirs() || directory.isDirectory)
        val output = File(directory, "${phase()}.json")
        val pending = File(directory, "${phase()}.pending")
        require(!output.exists() && pending.createNewFile()) { "Refuse evidence replay" }
        val parsed = JSONObject(report)
        parsed.put("normalActivityStopBeforeEvidence", true)
        pending.outputStream().use {
            it.write(parsed.toString(2).toByteArray(Charsets.UTF_8))
            it.flush(); it.fd.sync()
        }
        require(!output.exists() && pending.renameTo(output)) { "Evidence publish failed" }
    }
}
