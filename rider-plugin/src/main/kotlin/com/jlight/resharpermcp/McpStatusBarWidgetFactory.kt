package com.jlight.resharpermcp

import com.intellij.openapi.project.Project
import com.intellij.openapi.wm.StatusBarWidget
import com.intellij.openapi.wm.StatusBarWidgetFactory

class McpStatusBarWidgetFactory : StatusBarWidgetFactory {
    override fun getId(): String = "ReSharperMcp.StatusBar"
    override fun getDisplayName(): String = "ReSharper MCP Server"
    override fun isAvailable(project: Project): Boolean = true
    override fun createWidget(project: Project): StatusBarWidget = McpStatusBarWidget()
    override fun isEnabledByDefault(): Boolean = true
}
