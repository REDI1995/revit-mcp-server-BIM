import { McpServer } from "@modelcontextprotocol/sdk/server/mcp.js";
import { StdioServerTransport } from "@modelcontextprotocol/sdk/server/stdio.js";
import { registerTools } from "./tools/register.js";
import { getDatabase } from "./database/db.js";
import { createRequire } from "module";

const require = createRequire(import.meta.url);
const { version } = require("../package.json");

// Create server instance
const server = new McpServer({
  name: "revit-mcp-bim",
  version,
});

// Start server
async function main() {
  // Initialize database (better-sqlite3 is synchronous)
  getDatabase();

  // Register tools
  await registerTools(server);

  // Connect to transport layer
  const transport = new StdioServerTransport();
  await server.connect(transport);
  console.error("Revit MCP Server started successfully");
}

main().catch((error) => {
  console.error("Error starting Revit MCP Server:", error);
  process.exit(1);
});
