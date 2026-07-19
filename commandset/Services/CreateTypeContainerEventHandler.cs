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
        public double SampleLengthMm { get; set; } = 2000.0;   // length of each sample wall (runs along Y)
        public double SampleHeightMm { get; set; } = 3000.0;   // height of each sample wall
        public double GapMm { get; set; } = 1500.0;            // clear gap between samples (along X)
        public double LevelDropMm { get; set; } = 10000.0;     // distance below the lowest level
        public string LevelName { get; set; } = "container";
        public string SectionName { get; set; } = "Abaco - Sezione Tipi";
    }

    public class CreateTypeContainerResult
    {
        public string Level { get; set; }
        public double LevelElevationMm { get; set; }
        public int WallTypesFound { get; set; }
        public int SamplesCreated { get; set; }
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

                var wallTypes = new FilteredElementCollector(_doc).OfClass(typeof(WallType)).Cast<WallType>()
                    .Where(wt => wt.Kind == WallKind.Basic)
                    .OrderBy(wt => wt.Name)
                    .ToList();
                res.WallTypesFound = wallTypes.Count;

                var wallTag = new FilteredElementCollector(_doc).OfClass(typeof(FamilySymbol))
                    .OfCategory(BuiltInCategory.OST_WallTags).Cast<FamilySymbol>().FirstOrDefault();

                var sectionVft = new FilteredElementCollector(_doc).OfClass(typeof(ViewFamilyType)).Cast<ViewFamilyType>()
                    .FirstOrDefault(v => v.ViewFamily == ViewFamily.Section);

                var createdWalls = new List<ElementId>();
                var xs = new List<double>();
                View section = null;

                using (Transaction tx = new Transaction(_doc, "Create Type Container"))
                {
                    tx.Start();

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
                    res.SamplesCreated = createdWalls.Count;

                    _doc.Regenerate();

                    // 3) section across the row (looks along Y, cutting each wall's thickness)
                    if (sectionVft != null && createdWalls.Count > 0)
                    {
                        double originX = (minX + maxX) / 2.0;
                        var t = Transform.Identity;
                        t.Origin = new XYZ(originX, sampleLen / 2.0, containerElev + sampleH / 2.0);
                        t.BasisX = new XYZ(1, 0, 0);   // view right  = world X
                        t.BasisY = new XYZ(0, 0, 1);   // view up     = world Z
                        t.BasisZ = new XYZ(0, -1, 0);  // toward viewer (right-handed: X x Z)

                        double halfSpan = (maxX - minX) / 2.0 + margin;
                        var bb = new BoundingBoxXYZ { Transform = t };
                        bb.Min = new XYZ(-halfSpan, -(sampleH / 2.0 + margin), -(sampleLen / 2.0 + margin));
                        bb.Max = new XYZ(halfSpan, (sampleH / 2.0 + margin), (sampleLen / 2.0 + margin));

                        section = ViewSection.CreateSection(_doc, sectionVft.Id, bb);
                        try { section.Name = Options.SectionName; } catch { }
                        res.Section = section.Name;
                        _doc.Regenerate();
                    }
                    else if (sectionVft == null)
                    {
                        res.Warnings.Add("No Section view family type found; walls created but no section/tags.");
                    }

                    // 4) tag every sample wall inside the section
                    if (section != null && wallTag != null)
                    {
                        if (!wallTag.IsActive) { wallTag.Activate(); _doc.Regenerate(); }
                        for (int i = 0; i < createdWalls.Count; i++)
                        {
                            try
                            {
                                var w = _doc.GetElement(createdWalls[i]);
                                var pt = new XYZ(xs[i], sampleLen / 2.0, containerElev + sampleH / 2.0);
                                IndependentTag.Create(_doc, wallTag.Id, section.Id, new Reference(w),
                                    false, TagOrientation.Horizontal, pt);
                                res.TagsPlaced++;
                            }
                            catch (Exception ex)
                            {
                                res.Warnings.Add($"Tag on wall {createdWalls[i].GetIntValue()}: {ex.Message}");
                            }
                        }
                    }
                    else if (wallTag == null)
                    {
                        res.Warnings.Add("No wall tag family loaded; walls created but not tagged.");
                    }

                    tx.Commit();
                }

                string message = $"Container level '{res.Level}' at {res.LevelElevationMm:F0}mm: " +
                                 $"{res.SamplesCreated}/{res.WallTypesFound} wall-type samples, " +
                                 $"{res.TagsPlaced} tagged" +
                                 (res.Section != null ? $", section '{res.Section}'." : ", no section.");
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
    }
}
