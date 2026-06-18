using System;
using System.IO;
using Autodesk.Revit.UI;
using System.Reflection;
using System.Windows.Media.Imaging;
using revit_mcp_plugin.Helpers;
using revit_mcp_plugin.UI;
using revit_mcp_plugin.Utils;



namespace revit_mcp_plugin.Core
{
    public class Application : IExternalApplication
    {
        public Result OnStartup(UIControlledApplication application)
        {
            var pluginDir = Path.GetDirectoryName(typeof(Application).Assembly.Location);
            McpLogger.Initialize(pluginDir);
            McpLogger.Info("Application", "Plugin starting");

            // Auto-configure Claude Desktop on first run (silent, never crashes)
            ClaudeDesktopConfigurator.EnsureConfigured();

            // Register Dockable Panel
            try
            {
                application.RegisterDockablePane(
                    MCPDockablePaneProvider.PaneId,
                    "MCP Server",
                    new MCPDockablePaneProvider());
            }
            catch (Exception ex)
            {
                System.Diagnostics.Trace.WriteLine($"[RevitMCP] Panel registration skipped: {ex.Message}");
            }

            var asm = Assembly.GetExecutingAssembly();
            var v = asm.GetName().Version;
            string ver = $"v{v.Major}.{v.Minor}.{v.Build}";

            RibbonPanel mcpPanel = application.CreateRibbonPanel("revit-mcp-bim");

            PushButtonData pushButtonData = new PushButtonData("ID_EXCMD_TOGGLE_REVIT_MCP", "BIM MCP\r\n Switch",
                asm.Location, "revit_mcp_plugin.Core.MCPServiceConnection");
            pushButtonData.ToolTip = $"revit-mcp-bim {ver} — Start / Stop the MCP server";
            pushButtonData.Image = new BitmapImage(new Uri("/RevitMCPPlugin;component/Core/Ressources/icon-16.png", UriKind.RelativeOrAbsolute));
            pushButtonData.LargeImage = new BitmapImage(new Uri("/RevitMCPPlugin;component/Core/Ressources/icon-32.png", UriKind.RelativeOrAbsolute));
            mcpPanel.AddItem(pushButtonData);

            PushButtonData panelButtonData = new PushButtonData("ID_EXCMD_TOGGLE_MCP_PANEL", "MCP\r\n Panel",
                asm.Location, "revit_mcp_plugin.Core.ToggleMCPPanel");
            panelButtonData.ToolTip = $"revit-mcp-bim {ver} — Show / Hide MCP monitoring panel";
            panelButtonData.Image = new BitmapImage(new Uri("/RevitMCPPlugin;component/Core/Ressources/panel-16.png", UriKind.RelativeOrAbsolute));
            panelButtonData.LargeImage = new BitmapImage(new Uri("/RevitMCPPlugin;component/Core/Ressources/panel-32.png", UriKind.RelativeOrAbsolute));
            mcpPanel.AddItem(panelButtonData);

            PushButtonData mcp_settings_pushButtonData = new PushButtonData("ID_EXCMD_MCP_SETTINGS", "Settings",
                asm.Location, "revit_mcp_plugin.Core.Settings");
            mcp_settings_pushButtonData.ToolTip = $"revit-mcp-bim {ver} — Settings";
            mcp_settings_pushButtonData.Image = new BitmapImage(new Uri("/RevitMCPPlugin;component/Core/Ressources/settings-16.png", UriKind.RelativeOrAbsolute));
            mcp_settings_pushButtonData.LargeImage = new BitmapImage(new Uri("/RevitMCPPlugin;component/Core/Ressources/settings-32.png", UriKind.RelativeOrAbsolute));
            mcpPanel.AddItem(mcp_settings_pushButtonData);

            return Result.Succeeded;
        }

        public Result OnShutdown(UIControlledApplication application)
        {
            try
            {
                if (SocketService.Instance.IsRunning)
                {
                    SocketService.Instance.Stop();
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Trace.WriteLine($"[RevitMCP] Error during shutdown: {ex.Message}");
            }

            return Result.Succeeded;
        }
    }
}
