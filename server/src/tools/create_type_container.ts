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
      sampleLengthMm: z.number().optional().default(500).describe("Horizontal run of each sample stub (along Y). Default 500."),
      sampleHeightMm: z.number().optional().default(1000).describe("Height of each sample, from the container level. Default 1000."),
      gapMm: z.number().optional().default(500).describe("Clear gap between consecutive samples (along X). Default 500."),
      levelDropMm: z.number().optional().default(10000).describe("How far below the lowest existing level to place the container level. Default 10000."),
      levelName: z.string().optional().default("container").describe("Name of the container level. Default 'container'."),
      tagTypeName: z.string().optional().describe("Wall tag type/family to use (matched by type name, family name, or 'Family : Type'). Default: first available wall tag."),
      sectionName: z.string().optional().default("Abaco - Sezione Tipi").describe("Legacy — one section per wall type is now created, named after the type. Kept for back-compat."),
    },
    async (args) => {
      const params = {
        sampleLengthMm: args.sampleLengthMm ?? 500,
        sampleHeightMm: args.sampleHeightMm ?? 1000,
        gapMm: args.gapMm ?? 500,
        levelDropMm: args.levelDropMm ?? 10000,
        levelName: args.levelName ?? "container",
        tagTypeName: args.tagTypeName,
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
