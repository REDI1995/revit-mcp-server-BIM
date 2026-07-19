using System;
using Autodesk.Revit.UI;
using Newtonsoft.Json.Linq;
using RevitMCPSDK.API.Base;
using RevitMCPCommandSet.Services;

namespace RevitMCPCommandSet.Commands
{
    /// <summary>
    /// "Type container": creates a Level far below the model, places one sample of each
    /// basic wall type on it, cuts a Section across the row, and tags every sample.
    /// A documentation aid that pairs with the type schedules ("Abaco").
    /// </summary>
    public class CreateTypeContainerCommand : BimConductorCommandBase
    {
        private CreateTypeContainerEventHandler _handler => (CreateTypeContainerEventHandler)Handler;

        /// <summary>Command name — must match the MCP tool name.</summary>
        public override string CommandName => "create_type_container";

        public CreateTypeContainerCommand(UIApplication uiApp)
            : base(new CreateTypeContainerEventHandler(), uiApp)
        {
        }

        public override object Execute(JObject parameters, string requestId)
        {
            try
            {
                var opts = new TypeContainerOptions();
                if (parameters != null)
                {
                    opts.SampleLengthMm = parameters["sampleLengthMm"]?.ToObject<double?>() ?? opts.SampleLengthMm;
                    opts.SampleHeightMm = parameters["sampleHeightMm"]?.ToObject<double?>() ?? opts.SampleHeightMm;
                    opts.GapMm          = parameters["gapMm"]?.ToObject<double?>() ?? opts.GapMm;
                    opts.LevelDropMm    = parameters["levelDropMm"]?.ToObject<double?>() ?? opts.LevelDropMm;

                    var levelName = parameters["levelName"]?.ToObject<string>();
                    if (!string.IsNullOrEmpty(levelName)) opts.LevelName = levelName;

                    var sectionName = parameters["sectionName"]?.ToObject<string>();
                    if (!string.IsNullOrEmpty(sectionName)) opts.SectionName = sectionName;
                }

                _handler.SetParameters(opts);

                if (RaiseAndWaitForCompletion(60000)) // 60 second timeout (can place many walls)
                {
                    return _handler.Result;
                }

                throw new TimeoutException("Create type container operation timed out");
            }
            catch (Exception ex)
            {
                throw new Exception($"Failed to create type container: {ex.Message}");
            }
        }
    }
}
