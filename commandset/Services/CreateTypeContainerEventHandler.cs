using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using RevitMCPCommandSet.Models.Common;
using RevitMCPCommandSet.Utils;
using RevitMCPSDK.API.Interfaces;

namespace RevitMCPCommandSet.Services
{
    /// <summary>Options for the type-container command (dimensions in mm).</summary>
    public class TypeContainerOptions
    {
        public double SampleLengthMm { get; set; } = 500.0;    // horizontal run of each sample stub
        public double SampleHeightMm { get; set; } = 1000.0;   // height of each sample, from the container level
        public double GapMm { get; set; } = 500.0;             // clear gap between samples (along X)
        public double LevelDropMm { get; set; } = 10000.0;     // distance below the lowest level
        public string LevelName { get; set; } = "container";
        public string SectionName { get; set; } = "Abaco - Sezione Tipi";  // legacy; sections are now named after each wall type
        public string TagTypeName { get; set; } = null;        // wall tag type/family to use; null = first available
    }

    public class CreateTypeContainerResult
    {
        public string Level { get; set; }
        public double LevelElevationMm { get; set; }
        public int WallTypesFound { get; set; }
        public int SamplesCreated { get; set; }
        public int SectionsCreated { get; set; }
        public int TagsPlaced { get; set; }
        public string Section { get; set; }
        public List<string> Warnings { get; set; } = new List<string>();
    }

    /// <summary>
    /// Creates the "container" level, one sample of each basic wall type, a section across
    /// the row, and a tag on each sample — all in a single transaction.
    /// </summary>
    public class CreateTypeContainerEventHandler : IExternalEventHandler, IWaitableExternalEventHandler
    {
        private UIApplication _uiApp;
        private UIDocument _uiDoc => _uiApp.ActiveUIDocument;
        private Document _doc => _uiDoc.Document;

        private readonly ManualResetEvent _resetEvent = new ManualResetEvent(false);

        public TypeContainerOptions Options { get; private set; }
        public AIResult<CreateTypeContainerResult> Result { get; private set; }

        public void SetParameters(TypeContainerOptions options)
        {
            Options = options;
            _resetEvent.Reset();
        }

        private static double ToFt(double mm) => UnitUtils.ConvertToInternalUnits(mm, UnitTypeId.Millimeters);
        private static double ToMm(double ft) => UnitUtils.ConvertFromInternalUnits(ft, UnitTypeId.Millimeters);

        // Revit view names cannot contain these characters.
        private static string SanitizeViewName(string s)
        {
            if (string.IsNullOrWhiteSpace(s)) return "Tipo";
            foreach (var c in new[] { '\\', ':', '{', '}', '[', ']', '|', ';', '<', '>', '?', '`', '~' })
                s = s.Replace(c, '-');
            return s.Trim();
        }

        public void Execute(UIApplication uiapp)
        {
            _uiApp = uiapp;
            var res = new CreateTypeContainerResult { Level = Options.LevelName, Section = null };

            try
            {
                double sampleLen = ToFt(Options.SampleLengthMm);
                double sampleH = ToFt(Options.SampleHeightMm);
                double gap = ToFt(Options.GapMm);
                double drop = ToFt(Options.LevelDropMm);
                double margin = ToFt(800.0);

                var levels = new FilteredElementCollector(_doc).OfClass(typeof(Level)).Cast<Level>().ToList();
                if (levels.Count == 0)
                {
                    Result = new AIResult<CreateTypeContainerResult>
                    {
                        Success = false,
                        Message = "No levels in the model; cannot place the container level."
                    };
                    return;
                }

                double minElev = levels.Min(l => l.Elevation);
                double containerElev = minElev - drop;

                // Only include wall types actually USED (placed) in this model — not every loaded
                // type. The abaco documents what's really in the project.
                var usedTypeIds = new HashSet<ElementId>(
                    new FilteredElementCollector(_doc)
                        .OfClass(typeof(Wall))
                        .WhereElementIsNotElementType()
                        .Cast<Wall>()
                        .Select(w => w.GetTypeId()));

                var wallTypes = new FilteredElementCollector(_doc).OfClass(typeof(WallType)).Cast<WallType>()
                    .Where(wt => wt.Kind == WallKind.Basic && usedTypeIds.Contains(wt.Id))
                    .OrderBy(wt => wt.Name)
                    .ToList();
                res.WallTypesFound = wallTypes.Count;

                var wallTagSymbols = new FilteredElementCollector(_doc).OfClass(typeof(FamilySymbol))
                    .OfCategory(BuiltInCategory.OST_WallTags).Cast<FamilySymbol>().ToList();
                FamilySymbol wallTag = null;
                if (!string.IsNullOrEmpty(Options.TagTypeName))
                {
                    wallTag = wallTagSymbols.FirstOrDefault(s =>
                        string.Equals(s.Name, Options.TagTypeName, StringComparison.OrdinalIgnoreCase) ||
                        string.Equals(s.FamilyName, Options.TagTypeName, StringComparison.OrdinalIgnoreCase) ||
                        string.Equals($"{s.FamilyName} : {s.Name}", Options.TagTypeName, StringComparison.OrdinalIgnoreCase));
                }
                if (wallTag == null) wallTag = wallTagSymbols.FirstOrDefault();

                var sectionVft = new FilteredElementCollector(_doc).OfClass(typeof(ViewFamilyType)).Cast<ViewFamilyType>()
                    .FirstOrDefault(v => v.ViewFamily == ViewFamily.Section);

                var createdWalls = new List<ElementId>();
                var xs = new List<double>();
                var createdNames = new List<string>();

                using (Transaction tx = new Transaction(_doc, "Create Type Container"))
                {
                    tx.Start();

                    // Auto-resolve Revit failures (e.g. "Can't create wall — delete instance")
                    // silently instead of showing a modal dialog. A blocking dialog here would
                    // freeze the external event and hang every subsequent MCP command.
                    var fho = tx.GetFailureHandlingOptions();
                    fho.SetFailuresPreprocessor(new SilentFailureHandler());
                    fho.SetClearAfterRollback(true);
                    tx.SetFailureHandlingOptions(fho);

                    // 1) container level
                    Level level = Level.Create(_doc, containerElev);
                    try { level.Name = Options.LevelName; }
                    catch
                    {
                        for (int i = 2; i < 100; i++)
                        {
                            try { level.Name = $"{Options.LevelName} {i}"; break; }
                            catch { }
                        }
                    }
                    res.Level = level.Name;
                    res.LevelElevationMm = Math.Round(ToMm(containerElev), 1);

                    // 2) one sample wall per basic type, laid out along +X, each running along +Y
                    double x = 0.0, minX = 0, maxX = 0;
                    bool first = true;
                    foreach (var wt in wallTypes)
                    {
                        try
                        {
                            double thick = (wt.Width > 0) ? wt.Width : ToFt(200.0);
                            double cx = x + thick / 2.0;
                            var line = Line.CreateBound(
                                new XYZ(cx, 0, containerElev),
                                new XYZ(cx, sampleLen, containerElev));
                            var w = Wall.Create(_doc, line, wt.Id, level.Id, sampleH, 0.0, false, false);
                            createdWalls.Add(w.Id);
                            xs.Add(cx);
                            createdNames.Add(wt.Name);

                            double left = cx - thick / 2.0, right = cx + thick / 2.0;
                            if (first) { minX = left; maxX = right; first = false; }
                            else { if (left < minX) minX = left; if (right > maxX) maxX = right; }
                            x = right + gap;
                        }
                        catch (Exception ex)
                        {
                            res.Warnings.Add($"Wall type '{wt.Name}': {ex.Message}");
                        }
                    }
                    _doc.Regenerate();

                    // The failure handler may have auto-deleted samples for wall types Revit
                    // refused to build at this size. Keep only survivors so the section and
                    // tags never reference a deleted element; report which types were skipped.
                    {
                        var keepWalls = new List<ElementId>();
                        var keepXs = new List<double>();
                        var skippedNames = new List<string>();
                        for (int i = 0; i < createdWalls.Count; i++)
                        {
                            if (_doc.GetElement(createdWalls[i]) is Wall)
                            {
                                keepWalls.Add(createdWalls[i]);
                                keepXs.Add(xs[i]);
                            }
                            else
                            {
                                skippedNames.Add(createdNames[i]);
                            }
                        }
                        createdWalls = keepWalls;
                        xs = keepXs;
                        if (skippedNames.Count > 0)
                            res.Warnings.Add($"{skippedNames.Count} wall type(s) could not be built as a sample and were skipped: {string.Join(", ", skippedNames.Take(20))}");
                    }
                    res.SamplesCreated = createdWalls.Count;

                    // 3+4) one section per sample, framed around that single wall and named
                    //      after its wall type; then tag the wall inside its own section.
                    if (sectionVft == null)
                    {
                        res.Warnings.Add("No Section view family type found; walls created but no sections/tags.");
                    }
                    else
                    {
                        if (wallTag != null && !wallTag.IsActive) { wallTag.Activate(); _doc.Regenerate(); }

                        for (int i = 0; i < createdWalls.Count; i++)
                        {
                            try
                            {
                                var w = (Wall)_doc.GetElement(createdWalls[i]);
                                double cx = xs[i];
                                double thick = (w.WallType != null && w.WallType.Width > 0) ? w.WallType.Width : ToFt(200.0);

                                // Transverse section: the viewer looks along the wall's length, and the
                                // CUT PLANE is placed at the wall's mid-length so the wall is actually
                                // sliced — revealing its layer build-up (stratigrafia). The crop is kept
                                // tight to the thickness so neighbouring samples never appear in the view.
                                double widthMargin = ToFt(100.0);   // just past the layers, left/right
                                double heightMargin = ToFt(300.0);  // above/below the stub
                                double depthBack = sampleLen / 2.0 + ToFt(300.0);

                                var t = Transform.Identity;
                                t.Origin = new XYZ(cx, sampleLen / 2.0, containerElev + sampleH / 2.0);
                                t.BasisX = new XYZ(1, 0, 0);   // view right = world X = across the thickness (layers)
                                t.BasisY = new XYZ(0, 0, 1);   // view up    = world Z = height
                                t.BasisZ = new XYZ(0, -1, 0);  // viewer looks down the wall's length (+Y)

                                double halfW = thick / 2.0 + widthMargin;
                                double halfH = sampleH / 2.0 + heightMargin;
                                var bb = new BoundingBoxXYZ { Transform = t };
                                // Max.Z ~ 0 places the section cut plane at the wall's mid-length; the far
                                // clip (Min.Z) reaches only the back half, so nothing behind is pulled in.
                                bb.Min = new XYZ(-halfW, -halfH, -depthBack);
                                bb.Max = new XYZ(halfW, halfH, ToFt(10.0));

                                var sec = ViewSection.CreateSection(_doc, sectionVft.Id, bb);

                                // Name the section after the wall type (sanitize + dedup on clash).
                                string baseName = SanitizeViewName(createdNames[i]);
                                try { sec.Name = baseName; }
                                catch
                                {
                                    for (int k = 2; k < 100; k++)
                                    {
                                        try { sec.Name = $"{baseName} ({k})"; break; }
                                        catch { }
                                    }
                                }
                                // Reveal the layer build-up (stratigrafia): Fine detail draws the
                                // wall's individual layers where the section cuts it, and 1:5 makes
                                // the thin cross-section legible. This is the whole point of the abaco.
                                try { sec.DetailLevel = ViewDetailLevel.Fine; } catch { }
                                try { sec.Scale = 10; } catch { }

                                res.SectionsCreated++;
                                if (res.Section == null) res.Section = sec.Name;
                                _doc.Regenerate();

                                // Tag this wall inside its own section.
                                if (wallTag != null)
                                {
                                    var pt = new XYZ(cx, sampleLen / 2.0, containerElev + sampleH / 2.0);
                                    IndependentTag.Create(_doc, wallTag.Id, sec.Id, new Reference(w),
                                        false, TagOrientation.Horizontal, pt);
                                    res.TagsPlaced++;
                                }
                            }
                            catch (Exception ex)
                            {
                                res.Warnings.Add($"Section/tag for '{createdNames[i]}': {ex.Message}");
                            }
                        }

                        if (wallTag == null)
                            res.Warnings.Add($"Wall tag '{Options.TagTypeName ?? "(any)"}' not found; sections created but not tagged.");
                    }

                    tx.Commit();
                }

                string message = $"Container level '{res.Level}' at {res.LevelElevationMm:F0}mm: " +
                                 $"{res.SamplesCreated}/{res.WallTypesFound} wall-type samples, " +
                                 $"{res.SectionsCreated} per-type section(s), {res.TagsPlaced} tagged.";
                if (res.Warnings.Count > 0)
                    message += "\n\n⚠ " + string.Join("\n  • ", res.Warnings.Take(10));

                Result = new AIResult<CreateTypeContainerResult>
                {
                    Success = true,
                    Message = message,
                    Response = res
                };
            }
            catch (Exception ex)
            {
                Result = new AIResult<CreateTypeContainerResult>
                {
                    Success = false,
                    Message = $"Error creating type container: {ex.Message}"
                };
            }
            finally
            {
                _resetEvent.Set();
            }
        }

        public bool WaitForCompletion(int timeoutMilliseconds = 60000)
        {
            return _resetEvent.WaitOne(timeoutMilliseconds);
        }

        public string GetName() => "Create Type Container";

        /// <summary>
        /// Silently resolves Revit failures raised during the transaction: deletes warnings
        /// and applies the default resolution to resolvable errors (e.g. deletes a wall Revit
        /// could not build at the sample size). Prevents modal dialogs from blocking the
        /// external event. Only failures caused by this transaction are affected.
        /// </summary>
        private class SilentFailureHandler : IFailuresPreprocessor
        {
            public FailureProcessingResult PreprocessFailures(FailuresAccessor a)
            {
                var failures = a.GetFailureMessages();
                if (failures.Count == 0) return FailureProcessingResult.Continue;
                foreach (var f in failures)
                {
                    if (f.GetSeverity() == FailureSeverity.Warning)
                        a.DeleteWarning(f);
                    else if (f.HasResolutions())
                        a.ResolveFailure(f);
                }
                return FailureProcessingResult.ProceedWithCommit;
            }
        }
    }
}
