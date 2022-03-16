using System;
using Microsoft.AspNetCore.Components;

namespace Hearty.Server.WebUi.Infrastructure;

/// <summary>
/// Defines custom event handlers for Blazor.
/// </summary>
[EventHandler("onclosecolumnoptions", typeof(EventArgs), enableStopPropagation: true, enablePreventDefault: true)]
internal static class EventHandlers
{
}
