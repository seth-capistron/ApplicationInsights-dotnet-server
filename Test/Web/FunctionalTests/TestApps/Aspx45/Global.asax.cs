using System;
using System.Collections.Generic;
using System.Linq;
using System.Web;
using System.Web.Routing;
using System.Web.Security;
using Aspx45;

namespace Aspx45
{
    public class Global : HttpApplication
    {
        void Application_Start(object sender, EventArgs e)
        {
            // Code that runs on application startup
            AuthConfig.RegisterOpenAuth();
            RouteConfig.RegisterRoutes(RouteTable.Routes);

            foreach (var module in Microsoft.ApplicationInsights.Extensibility.Implementation.TelemetryModules.Instance.Modules)
            {
                module.Initialize(Microsoft.ApplicationInsights.Extensibility.TelemetryConfiguration.Active);
            }
        }

        void Application_End(object sender, EventArgs e)
        {
            //  Code that runs on application shutdown

        }

        void Application_Error(object sender, EventArgs e)
        {
            // Code that runs when an unhandled error occurs

        }
    }
}
