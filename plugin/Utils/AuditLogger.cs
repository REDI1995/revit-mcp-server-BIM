using Newtonsoft.Json;
using System;
using System.IO;
using System.Text;

namespace revit_mcp_plugin.Utils
{
    /// <summary>
    /// Writes a JSON Lines audit trail of every MCP command executed against Revit.
    /// File location: <plugin_dir>/Logs/audit.jsonl
    /// Each line is a self-contained JSON object — safe to tail, grep, or import into Excel.
    /// </summary>
    public static class AuditLogger
    {
        private static readonly object _fileLock = new object();

        // Commands that only read data — still logged but flagged as read-only
        // so audit consumers can easily filter for mutations.
        private static readonly System.Collections.Generic.HashSet<string> _readOnlyCommands =
            new System.Collections.Generic.HashSet<string>(StringComparer.OrdinalIgnoreCase)
            {
                "say_hello",
                "get_project_info",
                "get_current_view_info",
                "get_current_view_elements",
                "get_selected_elements",
                "get_available_family_types",
                "get_element_parameters",
                "get_shared_parameters",
                "get_schedule_data",
                "get_material_quantities",
                "get_material_properties",
                "get_materials",
                "get_phases",
                "get_worksets",
                "get_elements_by_workset",
                "get_compound_structure",
                "get_warnings",
                "get_room_openings",
                "get_linked_elements",
                "get_linked_file_instances",
                "get_link_transform",
                "get_selected_linked_elements",
                "get_elements_in_spatial_volume",
                "analyze_model_statistics",
                "check_model_health",
                "ai_element_filter",
                "filter_by_parameter_value",
                "find_undimensioned_elements",
                "find_untagged_elements",
                "list_family_sizes",
                "lines_per_view_count",
                "list_schedulable_fields",
                "measure_between_elements",
                "query_stored_data",
                "store_project_data",
                "store_room_data",
            };

        /// <summary>
        /// Writes one audit entry. Call this after every command execution in SocketService.
        /// </summary>
        /// <param name="command">The MCP method name (e.g. "set_element_parameters")</param>
        /// <param name="parameters">Raw parameters object from the JSON-RPC request</param>
        /// <param name="success">Whether the command succeeded</param>
        /// <param name="durationMs">Execution time in milliseconds</param>
        /// <param name="projectPath">Full path to the active Revit document, or empty string</param>
        /// <param name="errorMessage">Error message if the command failed, otherwise null</param>
        public static void Log(
            string command,
            object parameters,
            bool success,
            long durationMs,
            string projectPath,
            string errorMessage = null)
        {
            try
            {
                bool isWrite = !_readOnlyCommands.Contains(command);

                var entry = new
                {
                    timestamp = DateTime.UtcNow.ToString("yyyy-MM-ddTHH:mm:ss.fffZ"),
                    command,
                    is_write = isWrite,
                    success,
                    duration_ms = durationMs,
                    project = projectPath ?? string.Empty,
                    parameters = parameters,
                    error = errorMessage
                };

                string line = JsonConvert.SerializeObject(entry, Formatting.None) + "\n";

                string logPath = Path.Combine(PathManager.GetLogsDirectoryPath(), "audit.jsonl");

                lock (_fileLock)
                {
                    File.AppendAllText(logPath, line, Encoding.UTF8);
                }
            }
            catch
            {
                // Audit logging must never crash the command being audited.
                // Silently swallow errors here.
            }
        }
    }
}
