import { errorMessage } from "../utils/errorUtils.js";
import { z } from "zod";
import { McpServer } from "@modelcontextprotocol/sdk/server/mcp.js";
import { withRevitConnection } from "../utils/ConnectionManager.js";
import { rawToolResponse, rawToolError } from "../utils/compactTool.js";

// "Abaco" — generate a type-summary schedule per category, listing every unique
// wall/floor/ceiling TYPE actually used (Revit schedules only include placed
// types) with its key parameters and an instance count.
//
// Language-safety: create_schedule matches fields by their LOCALIZED display name
// and silently drops names it does not recognise. This Revit runs in Italian, so
// we pass Italian AND English candidate names for each column; the command keeps
// whichever exist, in order. Because the type-identity candidates are listed
// first, the surviving type field is always field index 0 — so we sort/group by
// fieldIndex 0 rather than by a (localized) name.

// Friendly, Italian-first labels for the schedule name.
const CATEGORY_LABELS: Record<string, string> = {
  OST_Walls: "Muri",
  OST_Floors: "Pavimenti",
  OST_Ceilings: "Controsoffitti",
  OST_Roofs: "Coperture",
  OST_StructuralColumns: "Pilastri strutturali",
  OST_Columns: "Pilastri",
  OST_StructuralFraming: "Travi",
};

// Column candidates, most-preferred first. Italian names first for this setup,
// English kept as a fallback so the tool also works on English models.
// The type-identity block MUST stay first so the surviving field is index 0.
const FIELD_CANDIDATES: string[] = [
  // type identity (index 0 after resolution)
  "Famiglia e tipo",
  "Family and Type",
  // thickness / width
  "Spessore",
  "Width",
  "Thickness",
  "Default Thickness",
  // quantities
  "Area",
  "Volume",
  // wall function (skipped for floors/ceilings)
  "Funzione",
  "Function",
  // instance count
  "Conteggio",
  "Count",
];

function labelFor(category: string): string {
  return CATEGORY_LABELS[category] ?? category.replace(/^OST_/, "");
}

export function registerGenerateTypeSchedulesTool(server: McpServer) {
  server.tool(
    "generate_type_schedules",
    "Abaco: generate a type-summary schedule for each given category (default walls, floors, ceilings), listing every unique type actually used with its key parameters and an instance count. Language-safe: works on Italian or English Revit. One call produces one schedule per category.",
    {
      categories: z
        .array(z.string())
        .optional()
        .describe(
          "BuiltInCategory names to schedule. Default: OST_Walls, OST_Floors, OST_Ceilings."
        ),
      namePrefix: z
        .string()
        .optional()
        .default("Abaco")
        .describe("Prefix for each schedule name, e.g. 'Abaco - Muri'."),
      itemized: z
        .boolean()
        .optional()
        .default(false)
        .describe(
          "If false (default), rows collapse so each unique type appears once. If true, every instance is a row."
        ),
    },
    async (args) => {
      const categories =
        args.categories && args.categories.length > 0
          ? args.categories
          : ["OST_Walls", "OST_Floors", "OST_Ceilings"];
      const namePrefix = args.namePrefix ?? "Abaco";
      const itemized = args.itemized ?? false;

      const fields = FIELD_CANDIDATES.map((parameterName) => ({ parameterName }));

      try {
        const results = await withRevitConnection(async (revitClient) => {
          const created: any[] = [];
          const failed: any[] = [];

          for (const category of categories) {
            const label = labelFor(category);
            const name = `${namePrefix} - ${label}`;
            const payload = {
              type: "Regular",
              categoryName: category,
              name,
              fields,
              isItemized: itemized,
              // group + sort by the type-identity field (index 0), language-agnostic
              sortFields: [{ fieldIndex: 0, sortOrder: "Ascending" }],
              // NOTE: grand-total flags are intentionally omitted. The create_schedule
              // handler throws "Display of grand total title is not enabled" when
              // showGrandTotal/showGrandTotalCount are set (verified live 2026-07). The
              // per-type instance count comes from the "Count"/"Conteggio" column instead.
            };

            try {
              const r: any = await revitClient.sendCommand("create_schedule", payload);
              const ok = r?.success ?? r?.Success ?? true;
              const info = r?.response ?? r?.Response ?? {};
              if (ok) {
                created.push({
                  category,
                  label,
                  // Report the REQUESTED name, not info.name: the server can echo a
                  // stale/previous response name under rapid calls (verified live 2026-07).
                  // The schedule is created correctly regardless; only the echo is unreliable.
                  name,
                  scheduleId: info.scheduleId ?? null,
                });
              } else {
                failed.push({ category, message: r?.message ?? r?.Message ?? "unknown error" });
              }
            } catch (err) {
              failed.push({ category, message: errorMessage(err) });
            }
          }

          return { created, failed };
        });

        const summary =
          `Created ${results.created.length} of ${categories.length} type schedule(s)` +
          (results.failed.length ? `, ${results.failed.length} failed.` : ".");

        return rawToolResponse("generate_type_schedules", {
          summary,
          note:
            "Columns matched by localized name; any category with no matching fields will have a near-empty schedule. Verify in Revit and tell me which columns to adjust.",
          ...results,
        });
      } catch (error) {
        return rawToolError(
          "generate_type_schedules",
          `generate_type_schedules failed: ${errorMessage(error)}`
        );
      }
    }
  );
}
