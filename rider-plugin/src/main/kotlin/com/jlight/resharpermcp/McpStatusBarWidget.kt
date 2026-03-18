package com.jlight.resharpermcp

import com.intellij.openapi.application.ApplicationManager
import com.intellij.openapi.ui.popup.JBPopupFactory
import com.intellij.openapi.ui.popup.PopupStep
import com.intellij.openapi.ui.popup.util.BaseListPopupStep
import com.intellij.openapi.wm.StatusBar
import com.intellij.openapi.wm.StatusBarWidget
import com.intellij.util.concurrency.AppExecutorUtil
import org.jetbrains.annotations.Nls
import java.awt.Component
import java.awt.event.MouseEvent
import java.io.BufferedReader
import java.io.InputStreamReader
import java.net.HttpURLConnection
import java.net.URL
import java.util.concurrent.ScheduledFuture
import java.util.concurrent.TimeUnit
import com.intellij.util.Consumer

class McpStatusBarWidget : StatusBarWidget, StatusBarWidget.TextPresentation {
    companion object {
        const val ID = "ReSharperMcp.StatusBar"
        private const val POLL_INTERVAL_SECONDS = 5L
        private const val DEFAULT_PORT = 23741
    }

    private var statusBar: StatusBar? = null
    private val port: Int = resolvePort()
    private var pollFuture: ScheduledFuture<*>? = null

    // Cached status — written from pooled thread, read from EDT
    @Volatile private var connected: Boolean = false
    @Volatile private var role: String = "unknown"
    @Volatile private var solutions: List<SolutionInfo> = emptyList()

    override fun ID(): String = ID

    override fun install(statusBar: StatusBar) {
        this.statusBar = statusBar
        pollFuture = AppExecutorUtil.getAppScheduledExecutorService()
            .scheduleWithFixedDelay(::poll, 0, POLL_INTERVAL_SECONDS, TimeUnit.SECONDS)
    }

    override fun getPresentation(): StatusBarWidget.WidgetPresentation = this

    // --- TextPresentation ---

    @Nls
    override fun getText(): String {
        return if (connected) "MCP: $port" else "MCP: offline"
    }

    override fun getAlignment(): Float = Component.CENTER_ALIGNMENT

    override fun getTooltipText(): String = "ReSharper MCP Server"

    override fun getClickConsumer(): Consumer<MouseEvent>? = Consumer { event ->
        showPopup(event.component)
    }

    // --- Popup ---

    private fun showPopup(component: Component) {
        val items = mutableListOf<PopupItem>()

        if (connected) {
            items.add(PopupItem("Status: Running (${role.replaceFirstChar { it.uppercase() }})", false))
            items.add(PopupItem("Port: $port", false))
            if (solutions.isNotEmpty()) {
                items.add(PopupItem("───", false))
                items.add(PopupItem("Solutions:", false))
                solutions.forEach { items.add(PopupItem("  ${it.name}", false)) }
            }
            items.add(PopupItem("───", false))
            items.add(PopupItem("Restart Server", true))
        } else {
            items.add(PopupItem("Status: Offline", false))
            items.add(PopupItem("Port: $port", false))
        }

        val popup = JBPopupFactory.getInstance().createListPopup(
            object : BaseListPopupStep<PopupItem>("MCP Server", items) {
                override fun getTextFor(value: PopupItem): String = value.text

                override fun isSelectable(value: PopupItem): Boolean = value.actionable

                override fun onChosen(selectedValue: PopupItem, finalChoice: Boolean): PopupStep<*>? {
                    if (selectedValue.text == "Restart Server") {
                        doRestart()
                    }
                    return FINAL_CHOICE
                }
            }
        )

        popup.showUnderneathOf(component)
    }

    // --- Polling ---

    private fun poll() {
        try {
            val response = httpPost(
                """{"jsonrpc":"2.0","id":1,"method":"internal/status","params":{}}"""
            )
            if (response != null) {
                parseStatus(response)
                connected = true
            } else {
                connected = false
                solutions = emptyList()
            }
        } catch (_: Exception) {
            connected = false
            solutions = emptyList()
        }

        // Update widget text on EDT
        ApplicationManager.getApplication().invokeLater {
            statusBar?.updateWidget(ID)
        }
    }

    private fun parseStatus(json: String) {
        try {
            val resultStart = json.indexOf("\"result\"")
            if (resultStart < 0) return

            // Extract role
            val roleMatch = Regex("\"role\"\\s*:\\s*\"(\\w+)\"").find(json)
            if (roleMatch != null) {
                role = roleMatch.groupValues[1]
            }

            // Extract solution names from the "solutions" array
            val solutionsList = mutableListOf<SolutionInfo>()
            val namePattern = Regex("\"name\"\\s*:\\s*\"([^\"]+)\"")
            val solutionsStart = json.indexOf("\"solutions\"")
            if (solutionsStart >= 0) {
                val solutionsJson = json.substring(solutionsStart)
                namePattern.findAll(solutionsJson).forEach { match ->
                    solutionsList.add(SolutionInfo(match.groupValues[1]))
                }
            }
            solutions = solutionsList
        } catch (_: Exception) {
            // Parse error — keep previous state
        }
    }

    // --- Restart ---

    private fun doRestart() {
        AppExecutorUtil.getAppExecutorService().submit {
            try {
                httpPost("""{"jsonrpc":"2.0","id":1,"method":"internal/restart","params":{}}""")
                connected = false
                ApplicationManager.getApplication().invokeLater {
                    statusBar?.updateWidget(ID)
                }
            } catch (_: Exception) {
                // Will show offline on next poll
            }
        }
    }

    // --- HTTP ---

    private fun httpPost(body: String): String? {
        val url = URL("http://127.0.0.1:$port/")
        val conn = url.openConnection() as HttpURLConnection
        try {
            conn.requestMethod = "POST"
            conn.setRequestProperty("Content-Type", "application/json")
            conn.doOutput = true
            conn.connectTimeout = 2000
            conn.readTimeout = 2000

            conn.outputStream.use { it.write(body.toByteArray(Charsets.UTF_8)) }

            if (conn.responseCode != 200) return null

            return BufferedReader(InputStreamReader(conn.inputStream, Charsets.UTF_8)).use { it.readText() }
        } finally {
            conn.disconnect()
        }
    }

    // --- Lifecycle ---

    override fun dispose() {
        pollFuture?.cancel(false)
    }

    // --- Helpers ---

    private fun resolvePort(): Int {
        val envPort = System.getenv("RESHARPER_MCP_PORT")
        if (!envPort.isNullOrBlank()) {
            envPort.toIntOrNull()?.let { return it }
        }
        return DEFAULT_PORT
    }

    private data class SolutionInfo(val name: String)
    private data class PopupItem(val text: String, val actionable: Boolean)
}
