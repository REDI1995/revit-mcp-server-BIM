import { errorMessage } from "../utils/errorUtils.js";
import { z } from "zod";
import { McpServer } from "@modelcontextprotocol/sdk/server/mcp.js";
import { withRevitConnection } from "../utils/ConnectionManager.js";
import { rawToolResponse, rawToolError } from "../utils/compactTool.js";

// "Type container": create a Level far below the model, place one sample of each basic
// wall type on it, cut a Section across the row, and tag every sample. A documentation
// aid that pairs with generate_type_schedules ("Abaco"). All dimensions in millimetres.
export function registerCreateTypeContainerTool(server: McpServer) {
  server.tool(
    "create_type_container",
    "Create a dedicated level ('container') far below the model, place one sample of each basic wall type on it, cut a section across the row, and tag every sample. A documentation aid that pairs with generate_type_schedules. Dimensions in mm.",
    {
      sampleLengthMm: z.number().optional().default(2000).describe("Length of each sample wall (runs along Y). Default 2000."),
      sampleHeightMm: z.number().optional().default(3000).describe("Height of each sample wall. Default 3000."),
      gapMm: z.number().optional().default(1500).describe("Clear gap between consecutive samples (along X). Default 1500."),
      levelDropMm: z.number().optional().default(10000).describe("How far below the lowest existing level to place the container level. Default 10000."),
      levelName: z.string().optional().default("container").describe("Name of the container level. Default 'container'."),
      sectionName: z.string().optional().default("Abaco - Sezione Tipi").describe("Name of the created section view."),
    },
    async (args) => {
      const params = {
        sampleLengthMm: args.sampleLengthMm ?? 2000,
        sampleHeightMm: args.sampleHeightMm ?? 3000,
        gapMm: args.gapMm ?? 1500,
        levelDropMm: args.levelDropMm ?? 10000,
        levelName: args.levelName ?? "container",
        sectionName: args.sectionName ?? "Abaco - Sezione Tipi",
      };
      try {
        const response = await withRevitConnection(
          async (revitClient) => revitClient.sendCommand("create_type_container", params),
          120000
        );
        return rawToolResponse("create_type_container", response);
      } catch (error) {
        return rawToolError("create_type_container", `create_type_container failed: ${errorMessage(error)}`);
      }
    }
  );
}
