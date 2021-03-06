namespace Microsoft.ApplicationInsights.DependencyCollector.Implementation
{
    using System;
    using System.Collections.Concurrent;
    using System.Collections.Generic;
    using System.Diagnostics;
    using System.Globalization;
    using System.Linq;
    using System.Net.Http;
    using System.Net.Http.Headers;
    using System.Reflection;
    using System.Runtime.CompilerServices;
    using System.Threading.Tasks;
    using Microsoft.ApplicationInsights.Common;
    using Microsoft.ApplicationInsights.DataContracts;
    using Microsoft.ApplicationInsights.Extensibility;
    using Microsoft.ApplicationInsights.Extensibility.Implementation;
    using Microsoft.ApplicationInsights.Extensibility.Implementation.Tracing;

    internal class HttpCoreDiagnosticSourceListener : IObserver<KeyValuePair<string, object>>, IDisposable
    {
        private const string DependencyErrorPropertyKey = "Error";
        private const string HttpOutEventName = "System.Net.Http.HttpRequestOut";
        private const string HttpOutStartEventName = "System.Net.Http.HttpRequestOut.Start";
        private const string HttpOutStopEventName = "System.Net.Http.HttpRequestOut.Stop";
        private const string HttpExceptionEventName = "System.Net.Http.Exception";
        private const string DeprecatedRequestEventName = "System.Net.Http.Request";
        private const string DeprecatedResponseEventName = "System.Net.Http.Response";

        private readonly IEnumerable<string> correlationDomainExclusionList;
        private readonly ApplicationInsightsUrlFilter applicationInsightsUrlFilter;
        private readonly bool setComponentCorrelationHttpHeaders;
        private readonly TelemetryClient client;
        private readonly TelemetryConfiguration configuration;
        private readonly HttpCoreDiagnosticSourceSubscriber subscriber;
        #region fetchers

        private readonly PropertyFetcher startRequestFetcher = new PropertyFetcher("Request");
        private readonly PropertyFetcher stopRequestFetcher = new PropertyFetcher("Request");
        private readonly PropertyFetcher exceptionRequestFetcher = new PropertyFetcher("Request");
        private readonly PropertyFetcher exceptionFetcher = new PropertyFetcher("Exception");
        private readonly PropertyFetcher stopResponseFetcher = new PropertyFetcher("Response");
        private readonly PropertyFetcher stopRequestStatusFetcher = new PropertyFetcher("RequestTaskStatus");
        private readonly PropertyFetcher deprecatedRequestFetcher = new PropertyFetcher("Request");
        private readonly PropertyFetcher deprecatedResponseFetcher = new PropertyFetcher("Response");
        private readonly PropertyFetcher deprecatedRequestGuidFetcher = new PropertyFetcher("LoggingRequestId");
        private readonly PropertyFetcher deprecatedResponseGuidFetcher = new PropertyFetcher("LoggingRequestId");

        #endregion

        private readonly ConditionalWeakTable<HttpRequestMessage, IOperationHolder<DependencyTelemetry>> pendingTelemetry = 
            new ConditionalWeakTable<HttpRequestMessage, IOperationHolder<DependencyTelemetry>>();

        private readonly ConcurrentDictionary<string, Exception> pendingExceptions =
            new ConcurrentDictionary<string, Exception>();

        private readonly bool isNetCore20HttpClient;
        private readonly bool injectLegacyHeaders = false;

        public HttpCoreDiagnosticSourceListener(TelemetryConfiguration configuration, bool setComponentCorrelationHttpHeaders, IEnumerable<string> correlationDomainExclusionList, bool injectLegacyHeaders)
        {
            this.client = new TelemetryClient(configuration);
            this.client.Context.GetInternalContext().SdkVersion = SdkVersionUtils.GetSdkVersion("rdd" + RddSource.DiagnosticSourceCore + ":");

            var httpClientVersion = typeof(HttpClient).GetTypeInfo().Assembly.GetName().Version;
            this.isNetCore20HttpClient = httpClientVersion.CompareTo(new Version(4, 2)) >= 0;

            this.configuration = configuration;
            this.applicationInsightsUrlFilter = new ApplicationInsightsUrlFilter(configuration);
            this.setComponentCorrelationHttpHeaders = setComponentCorrelationHttpHeaders;
            this.correlationDomainExclusionList = correlationDomainExclusionList ?? Enumerable.Empty<string>();
            this.injectLegacyHeaders = injectLegacyHeaders;

            this.subscriber = new HttpCoreDiagnosticSourceSubscriber(this, this.applicationInsightsUrlFilter, this.isNetCore20HttpClient);
        }

        /// <summary>
        /// Gets the DependencyTelemetry objects that are still waiting for a response from the dependency. This will most likely only be used for testing purposes.
        /// </summary>
        internal ConditionalWeakTable<HttpRequestMessage, IOperationHolder<DependencyTelemetry>> PendingDependencyTelemetry => this.pendingTelemetry;

        /// <summary>
        /// Notifies the observer that the provider has finished sending push-based notifications.
        /// <seealso cref="IObserver{T}.OnCompleted()"/>
        /// </summary>
        public void OnCompleted()
        {
        }

        /// <summary>
        /// Notifies the observer that the provider has experienced an error condition.
        /// <seealso cref="IObserver{T}.OnError(Exception)"/>
        /// </summary>
        /// <param name="error">An object that provides additional information about the error.</param>
        public void OnError(Exception error)
        {
        }

        /// <summary>
        /// Provides the observer with new data.
        /// <seealso cref="IObserver{T}.OnNext(T)"/>
        /// </summary>
        /// <param name="evnt">The current notification information.</param>
        public void OnNext(KeyValuePair<string, object> evnt)
        {
            const string ErrorTemplateTypeCast = "Event {0}: cannot cast {1} to expected type {2}";
            const string ErrorTemplateValueParse = "Event {0}: cannot parse '{1}' as type {2}";

            try
            {
                switch (evnt.Key)
                {
                    case HttpOutStartEventName:
                        {
                            var request = this.startRequestFetcher.Fetch(evnt.Value) as HttpRequestMessage;

                            if (request == null)
                            {
                                var error = string.Format(CultureInfo.InvariantCulture, ErrorTemplateTypeCast, evnt.Key, "request", "HttpRequestMessage");
                                DependencyCollectorEventSource.Log.HttpCoreDiagnosticSourceListenerOnNextFailed(error);
                            }
                            else
                            {
                                this.OnActivityStart(request);
                            }

                            break;
                        }

                    case HttpOutStopEventName:
                        {
                            var response = this.stopResponseFetcher.Fetch(evnt.Value) as HttpResponseMessage;
                            var request = this.stopRequestFetcher.Fetch(evnt.Value) as HttpRequestMessage;
                            var requestTaskStatusString = this.stopRequestStatusFetcher.Fetch(evnt.Value).ToString();

                            if (request == null)
                            {
                                var error = string.Format(CultureInfo.InvariantCulture, ErrorTemplateTypeCast, evnt.Key, "request", "HttpRequestMessage");
                                DependencyCollectorEventSource.Log.HttpCoreDiagnosticSourceListenerOnNextFailed(error);
                            }
                            else if (!Enum.TryParse(requestTaskStatusString, out TaskStatus requestTaskStatus))
                            {
                                var error = string.Format(CultureInfo.InvariantCulture, ErrorTemplateValueParse, evnt.Key, requestTaskStatusString, "TaskStatus");
                                DependencyCollectorEventSource.Log.HttpCoreDiagnosticSourceListenerOnNextFailed(error);
                            }
                            else
                            {
                                this.OnActivityStop(response, request, requestTaskStatus);
                            }

                            break;
                        }

                    case HttpExceptionEventName:
                        {
                            var exception = this.exceptionFetcher.Fetch(evnt.Value) as Exception;
                            var request = this.exceptionRequestFetcher.Fetch(evnt.Value) as HttpRequestMessage;

                            if (exception == null)
                            {
                                var error = string.Format(CultureInfo.InvariantCulture, ErrorTemplateTypeCast, evnt.Key, "exception", "Exception");
                                DependencyCollectorEventSource.Log.HttpCoreDiagnosticSourceListenerOnNextFailed(error);
                            }
                            else if (request == null)
                            {
                                var error = string.Format(CultureInfo.InvariantCulture, ErrorTemplateTypeCast, evnt.Key, "request", "HttpRequestMessage");
                                DependencyCollectorEventSource.Log.HttpCoreDiagnosticSourceListenerOnNextFailed(error);
                            }
                            else
                            {
                                this.OnException(exception, request);
                            }

                            break;
                        }

                    case DeprecatedRequestEventName:
                        {
                            if (this.isNetCore20HttpClient)
                            {
                                // 2.0 publishes new events, and this should be just ignored to prevent duplicates.
                                break;
                            }

                            var request = this.deprecatedRequestFetcher.Fetch(evnt.Value) as HttpRequestMessage;
                            var loggingRequestIdString = this.deprecatedRequestGuidFetcher.Fetch(evnt.Value).ToString();

                            if (request == null)
                            {
                                var error = string.Format(CultureInfo.InvariantCulture, ErrorTemplateTypeCast, evnt.Key, "request", "HttpRequestMessage");
                                DependencyCollectorEventSource.Log.HttpCoreDiagnosticSourceListenerOnNextFailed(error);
                            }
                            else if (!Guid.TryParse(loggingRequestIdString, out Guid loggingRequestId))
                            {
                                var error = string.Format(CultureInfo.InvariantCulture, ErrorTemplateValueParse, evnt.Key, loggingRequestIdString, "Guid");
                                DependencyCollectorEventSource.Log.HttpCoreDiagnosticSourceListenerOnNextFailed(error);
                            }
                            else
                            {
                                this.OnRequest(request, loggingRequestId);
                            }

                            break;
                        }

                    case DeprecatedResponseEventName:
                        {
                            if (this.isNetCore20HttpClient)
                            {
                                // 2.0 publishes new events, and this should be just ignored to prevent duplicates.
                                break;
                            }

                            var response = this.deprecatedResponseFetcher.Fetch(evnt.Value) as HttpResponseMessage;
                            var loggingRequestIdString = this.deprecatedResponseGuidFetcher.Fetch(evnt.Value).ToString();

                            if (response == null)
                            {
                                var error = string.Format(CultureInfo.InvariantCulture, ErrorTemplateTypeCast, evnt.Key, "response", "HttpResponseMessage");
                                DependencyCollectorEventSource.Log.HttpCoreDiagnosticSourceListenerOnNextFailed(error);
                            }
                            else if (!Guid.TryParse(loggingRequestIdString, out Guid loggingRequestId))
                            {
                                var error = string.Format(CultureInfo.InvariantCulture, ErrorTemplateValueParse, evnt.Key, loggingRequestIdString, "Guid");
                                DependencyCollectorEventSource.Log.HttpCoreDiagnosticSourceListenerOnNextFailed(error);
                            }
                            else
                            {
                                this.OnResponse(response, loggingRequestId);
                            }

                            break;
                        }
                }
            }
            catch (Exception ex)
            {
                DependencyCollectorEventSource.Log.HttpCoreDiagnosticSourceListenerOnNextFailed(ExceptionUtilities.GetExceptionDetailString(ex));
            }
        }

        public void Dispose()
        {
            if (this.subscriber != null)
            {
                this.subscriber.Dispose();
            }
        }

        //// netcoreapp 2.0 event

        /// <summary>
        /// Handler for Exception event, it is sent when request processing cause an exception (e.g. because of DNS or network issues)
        /// Stop event will be sent anyway with null response.
        /// </summary>
        internal void OnException(Exception exception, HttpRequestMessage request)
        {
            // Even though we have the IsEnabled filter, to reject ApplicationInsights URLs before any events are fired,
            // Exceptions are special and fired even if request instrumentation is disabled.
            if (this.applicationInsightsUrlFilter.IsApplicationInsightsUrl(request.RequestUri))
            {
                return;
            }

            Activity currentActivity = Activity.Current;
            if (currentActivity == null)
            {
                DependencyCollectorEventSource.Log.CurrentActivityIsNull(HttpExceptionEventName);
                return;
            }

            DependencyCollectorEventSource.Log.HttpCoreDiagnosticSourceListenerException(currentActivity.Id);

            this.pendingExceptions.TryAdd(currentActivity.Id, exception);
            this.client.TrackException(exception);
        }

        //// netcoreapp 2.0 event

        /// <summary>
        /// Handler for Activity start event (outgoing request is about to be sent).
        /// </summary>
        internal void OnActivityStart(HttpRequestMessage request)
        {
            // Even though we have the IsEnabled filter to reject ApplicationInsights URLs before any events are fired, if there
            // are multiple subscribers and one subscriber returns true to IsEnabled then all subscribers will receive the event.
            if (this.applicationInsightsUrlFilter.IsApplicationInsightsUrl(request.RequestUri))
            {
                return;
            }

            var currentActivity = Activity.Current;
            if (currentActivity == null)
            {
                DependencyCollectorEventSource.Log.CurrentActivityIsNull(HttpOutStartEventName);
                return;
            }

            DependencyCollectorEventSource.Log.HttpCoreDiagnosticSourceListenerStart(currentActivity.Id);

            this.InjectRequestHeaders(request, this.configuration.InstrumentationKey);
        }

        //// netcoreapp 2.0 event

        /// <summary>
        /// Handler for Activity stop event (response is received for the outgoing request).
        /// </summary>
        internal void OnActivityStop(HttpResponseMessage response, HttpRequestMessage request, TaskStatus requestTaskStatus)
        {
            // Even though we have the IsEnabled filter to reject ApplicationInsights URLs before any events are fired, if there
            // are multiple subscribers and one subscriber returns true to IsEnabled then all subscribers will receive the event.
            if (this.applicationInsightsUrlFilter.IsApplicationInsightsUrl(request.RequestUri))
            {
                return;
            }

            Activity currentActivity = Activity.Current;
            if (currentActivity == null)
            {
                DependencyCollectorEventSource.Log.CurrentActivityIsNull(HttpOutStopEventName);
                return;
            }

            DependencyCollectorEventSource.Log.HttpCoreDiagnosticSourceListenerStop(currentActivity.Id);

            Uri requestUri = request.RequestUri;
            var resourceName = request.Method.Method + " " + requestUri.AbsolutePath;

            DependencyTelemetry telemetry = new DependencyTelemetry();

            // properly fill dependency telemetry operation context: OperationCorrelationTelemetryInitializer initializes child telemetry
            telemetry.Context.Operation.Id = currentActivity.RootId;
            telemetry.Context.Operation.ParentId = currentActivity.ParentId;
            telemetry.Id = currentActivity.Id;
            foreach (var item in currentActivity.Baggage)
            {
                if (!telemetry.Context.Properties.ContainsKey(item.Key))
                {
                    telemetry.Context.Properties[item.Key] = item.Value;
                }
            }
            
            this.client.Initialize(telemetry);

            telemetry.Timestamp = currentActivity.StartTimeUtc;
            telemetry.Name = resourceName;
            telemetry.Target = requestUri.Host;
            telemetry.Type = RemoteDependencyConstants.HTTP;
            telemetry.Data = requestUri.OriginalString;
            telemetry.Duration = currentActivity.Duration;
            if (response != null)
            {
                this.ParseResponse(response, telemetry);
            }
            else
            {
                if (this.pendingExceptions.TryRemove(currentActivity.Id, out Exception exception))
                {
                    telemetry.Context.Properties[DependencyErrorPropertyKey] = exception.GetBaseException().Message;
                }

                telemetry.ResultCode = requestTaskStatus.ToString();
                telemetry.Success = false;
            }

            this.client.Track(telemetry);
        }

        //// netcoreapp1.1 and prior event. See https://github.com/dotnet/corefx/blob/release/1.0.0-rc2/src/Common/src/System/Net/Http/HttpHandlerDiagnosticListenerExtensions.cs.

        /// <summary>
        /// Diagnostic event handler method for 'System.Net.Http.Request' event.
        /// </summary>
        internal void OnRequest(HttpRequestMessage request, Guid loggingRequestId)
        {
            if (request != null && request.RequestUri != null &&
                !this.applicationInsightsUrlFilter.IsApplicationInsightsUrl(request.RequestUri))
            {
                DependencyCollectorEventSource.Log.HttpCoreDiagnosticSourceListenerRequest(loggingRequestId);

                Uri requestUri = request.RequestUri;
                var resourceName = request.Method.Method + " " + requestUri.AbsolutePath;

                var dependency = this.client.StartOperation<DependencyTelemetry>(resourceName);
                dependency.Telemetry.Target = requestUri.Host;
                dependency.Telemetry.Type = RemoteDependencyConstants.HTTP;
                dependency.Telemetry.Data = requestUri.OriginalString;
                this.pendingTelemetry.AddIfNotExists(request, dependency);

                this.InjectRequestHeaders(request, dependency.Telemetry.Context.InstrumentationKey, true);
            }
        }

        //// netcoreapp1.1 and prior event. See https://github.com/dotnet/corefx/blob/release/1.0.0-rc2/src/Common/src/System/Net/Http/HttpHandlerDiagnosticListenerExtensions.cs.

        /// <summary>
        /// Diagnostic event handler method for 'System.Net.Http.Response' event.
        /// This event will be fired only if response was received (and not called for faulted or canceled requests).
        /// </summary>
        internal void OnResponse(HttpResponseMessage response, Guid loggingRequestId)
        {
            if (response != null)
            {
                DependencyCollectorEventSource.Log.HttpCoreDiagnosticSourceListenerResponse(loggingRequestId);
                var request = response.RequestMessage;
                if (request != null && this.pendingTelemetry.TryGetValue(request, out IOperationHolder<DependencyTelemetry> dependency))
                {
                    this.ParseResponse(response, dependency.Telemetry);
                    this.client.StopOperation(dependency);
                    this.pendingTelemetry.Remove(request);
                }
            }
        }

        private void InjectRequestHeaders(HttpRequestMessage request, string instrumentationKey, bool isLegacyEvent = false)
        {
            try
            {
                var currentActivity = Activity.Current;

                HttpRequestHeaders requestHeaders = request.Headers;
                if (requestHeaders != null && this.setComponentCorrelationHttpHeaders && !this.correlationDomainExclusionList.Contains(request.RequestUri.Host))
                {
                    try
                    {
                        string sourceApplicationId = null;
                        if (!string.IsNullOrEmpty(instrumentationKey)
                            && !HttpHeadersUtilities.ContainsRequestContextKeyValue(requestHeaders, RequestResponseHeaders.RequestContextCorrelationSourceKey)
                            && (this.configuration.ApplicationIdProvider?.TryGetApplicationId(instrumentationKey, out sourceApplicationId) ?? false))
                        {
                            HttpHeadersUtilities.SetRequestContextKeyValue(requestHeaders, RequestResponseHeaders.RequestContextCorrelationSourceKey, sourceApplicationId);
                        }
                    }
                    catch (Exception e)
                    {
                        AppMapCorrelationEventSource.Log.UnknownError(ExceptionUtilities.GetExceptionDetailString(e));
                    }

                    if (isLegacyEvent)
                    {
                        if (!requestHeaders.Contains(RequestResponseHeaders.RequestIdHeader))
                        {
                            requestHeaders.Add(RequestResponseHeaders.RequestIdHeader, currentActivity.Id);
                        }

                        if (!requestHeaders.Contains(RequestResponseHeaders.CorrelationContextHeader))
                        {
                            // we expect baggage to be empty or contain a few items
                            using (IEnumerator<KeyValuePair<string, string>> e = currentActivity.Baggage.GetEnumerator())
                            {
                                if (e.MoveNext())
                                {
                                    var baggage = new List<string>();
                                    do
                                    {
                                        KeyValuePair<string, string> item = e.Current;
                                        baggage.Add(new NameValueHeaderValue(item.Key, item.Value).ToString());
                                    }
                                    while (e.MoveNext());

                                    requestHeaders.Add(RequestResponseHeaders.CorrelationContextHeader, baggage);
                                }
                            }
                        }
                    }

                    if (this.injectLegacyHeaders)
                    {
                        // Add the root ID
                        string rootId = currentActivity.RootId;
                        if (!string.IsNullOrEmpty(rootId) && !requestHeaders.Contains(RequestResponseHeaders.StandardRootIdHeader))
                        {
                            requestHeaders.Add(RequestResponseHeaders.StandardRootIdHeader, rootId);
                        }

                        // Add the parent ID
                        string parentId = currentActivity.Id;
                        if (!string.IsNullOrEmpty(parentId) && !requestHeaders.Contains(RequestResponseHeaders.StandardParentIdHeader))
                        {
                            requestHeaders.Add(RequestResponseHeaders.StandardParentIdHeader, parentId);
                        }
                    }
                }
            }
            catch (Exception e)
            {
                AppMapCorrelationEventSource.Log.UnknownError(ExceptionUtilities.GetExceptionDetailString(e));
            }
        }

        private void ParseResponse(HttpResponseMessage response, DependencyTelemetry telemetry)
        {
            try
            {
                string targetApplicationId = HttpHeadersUtilities.GetRequestContextKeyValue(response.Headers, RequestResponseHeaders.RequestContextCorrelationTargetKey);
                if (!string.IsNullOrEmpty(targetApplicationId) && !string.IsNullOrEmpty(telemetry.Context.InstrumentationKey))
                {
                    // We only add the cross component correlation key if the key does not represent the current component.
                    string sourceApplicationId = null;
                    if (this.configuration.ApplicationIdProvider?.TryGetApplicationId(telemetry.Context.InstrumentationKey, out sourceApplicationId) ?? false)
                    {
                        if (targetApplicationId != sourceApplicationId)
                        {
                            telemetry.Type = RemoteDependencyConstants.AI;
                            telemetry.Target += " | " + targetApplicationId;
                        }
                    }
                }
            }
            catch (Exception e)
            {
                AppMapCorrelationEventSource.Log.UnknownError(ExceptionUtilities.GetExceptionDetailString(e));
            }

            int statusCode = (int)response.StatusCode;
            telemetry.ResultCode = (statusCode > 0) ? statusCode.ToString(CultureInfo.InvariantCulture) : string.Empty;
            telemetry.Success = (statusCode > 0) && (statusCode < 400);
        }

        /// <summary>
        /// Diagnostic listener implementation that listens for events specific to outgoing dependency requests.
        /// </summary>
        private class HttpCoreDiagnosticSourceSubscriber : IObserver<DiagnosticListener>, IDisposable
        {
            private readonly HttpCoreDiagnosticSourceListener httpDiagnosticListener;
            private readonly IDisposable listenerSubscription;
            private readonly ApplicationInsightsUrlFilter applicationInsightsUrlFilter;
            private readonly bool isNetCore20HttpClient;

            private IDisposable eventSubscription;

            internal HttpCoreDiagnosticSourceSubscriber(
                HttpCoreDiagnosticSourceListener listener,
                ApplicationInsightsUrlFilter applicationInsightsUrlFilter,
                bool isNetCore20HttpClient)
            {
                this.httpDiagnosticListener = listener;
                this.applicationInsightsUrlFilter = applicationInsightsUrlFilter;

                this.isNetCore20HttpClient = isNetCore20HttpClient;

                try
                {
                    this.listenerSubscription = DiagnosticListener.AllListeners.Subscribe(this);
                }
                catch (Exception ex)
                {
                    DependencyCollectorEventSource.Log.HttpCoreDiagnosticSubscriberFailedToSubscribe(ex.ToInvariantString());
                }
            }

            /// <summary>
            /// This method gets called once for each existing DiagnosticListener when this
            /// DiagnosticListener is added to the list of DiagnosticListeners
            /// (<see cref="System.Diagnostics.DiagnosticListener.AllListeners"/>). This method will
            /// also be called for each subsequent DiagnosticListener that is added to the list of
            /// DiagnosticListeners.
            /// <seealso cref="IObserver{T}.OnNext(T)"/>
            /// </summary>
            /// <param name="value">The DiagnosticListener that exists when this listener was added to
            /// the list, or a DiagnosticListener that got added after this listener was added.</param>
            public void OnNext(DiagnosticListener value)
            {
                if (value != null)
                {
                    // Comes from https://github.com/dotnet/corefx/blob/master/src/System.Net.Http/src/System/Net/Http/DiagnosticsHandlerLoggingStrings.cs#L12
                    if (value.Name == "HttpHandlerDiagnosticListener")
                    {
                        this.eventSubscription = value.Subscribe(
                            this.httpDiagnosticListener,
                            (evnt, r, _) =>
                            {
                                if (this.isNetCore20HttpClient)
                                {
                                    if (evnt == HttpExceptionEventName)
                                    {
                                        return true;
                                    }

                                    if (!evnt.StartsWith(HttpOutEventName, StringComparison.Ordinal))
                                    {
                                        return false;
                                    }

                                    if (evnt == HttpOutEventName && r != null)
                                    {
                                        var request = (HttpRequestMessage)r;
                                        return !this.applicationInsightsUrlFilter.IsApplicationInsightsUrl(request.RequestUri);
                                    }
                                }

                                return true;
                            });
                    }
                }
            }

            /// <summary>
            /// Notifies the observer that the provider has finished sending push-based notifications.
            /// <seealso cref="IObserver{T}.OnCompleted()"/>
            /// </summary>
            public void OnCompleted()
            {
            }

            /// <summary>
            /// Notifies the observer that the provider has experienced an error condition.
            /// <seealso cref="IObserver{T}.OnError(Exception)"/>
            /// </summary>
            /// <param name="error">An object that provides additional information about the error.</param>
            public void OnError(Exception error)
            {
            }

            public void Dispose()
            {
                if (this.eventSubscription != null)
                {
                    this.eventSubscription.Dispose();
                }

                if (this.listenerSubscription != null)
                {
                    this.listenerSubscription.Dispose();
                }
            }
        }
    }
}