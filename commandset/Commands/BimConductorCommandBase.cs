using Autodesk.Revit.UI;
using RevitMCPSDK.API.Interfaces;
using System;
using System.Threading;

namespace RevitMCPSDK.API.Base
{
    /// <summary>
    /// Replaces ExternalEventCommandBase.RaiseAndWaitForCompletion with a version that checks
    /// the Raise() return value. On Revit 2024 (net48), Raise() can silently return Denied,
    /// causing WaitForCompletion to block for the full timeout. This class retries on Denied
    /// with short delays and throws immediately rather than waiting 15s for nothing.
    /// </summary>
    public abstract class BimConductorCommandBase : ExternalEventCommandBase
    {
        protected BimConductorCommandBase(IWaitableExternalEventHandler handler, UIApplication uiApp)
            : base(handler, uiApp) { }

        protected new bool RaiseAndWaitForCompletion(int timeoutMs = 10000)
        {
            const int maxAttempts = 4;
            ExternalEventRequest request = ExternalEventRequest.Denied;

            for (int i = 0; i < maxAttempts; i++)
            {
                request = Event.Raise();

                if (request == ExternalEventRequest.Accepted || request == ExternalEventRequest.Pending)
                    return Handler.WaitForCompletion(timeoutMs);

                // Denied: Revit not idle yet (common on Revit 2024 under net48).
                // Back off and retry rather than immediately blocking for the full timeout.
                if (i < maxAttempts - 1)
                    Thread.Sleep(150 * (i + 1));
            }

            throw new InvalidOperationException(
                $"ExternalEvent.Raise() was {request} after {maxAttempts} attempts. " +
                "Ensure no modal dialog is open and the Revit model is fully loaded.");
        }
    }
}
