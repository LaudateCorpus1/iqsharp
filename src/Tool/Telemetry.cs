// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

#nullable enable

#if TELEMETRY

using System;
using System.IO;
using System.Linq;
using System.Net.NetworkInformation;
using Microsoft.Applications.Events;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.Jupyter.Core;
using Microsoft.Quantum.QsCompiler.CompilationBuilder;
using Microsoft.Quantum.Simulation.Simulators;
using static Microsoft.Jupyter.Core.BaseEngine;
using Microsoft.Quantum.IQSharp.Kernel;
using Microsoft.Quantum.IQSharp.Jupyter;
using Microsoft.Quantum.IQSharp.AzureClient;
using System.Collections.Generic;

namespace Microsoft.Quantum.IQSharp
{
    public class TelemetryService : ITelemetryService
    {
        private const string TOKEN = "55aee962ee9445f3a86af864fc0fa766-48882422-3439-40de-8030-228042bd9089-7794";

        // To ensure that startup of the telemetry service remains
        // lightweight enough to observe the startup process, we do not
        // inject required services through DI but rather create local
        // variables for each required service and then set them as those
        // services become available through events below.
        internal static IAzureClient? client = null;
        internal static BaseEngine? baseEngine = null;

        private void OnServiceInitialized<T>()
        {
            EventService.OnServiceInitialized<T>().On += (service) =>
            {
                var evt = "ServiceInitialized".AsTelemetryEvent();
                evt.SetProperty("Service", $"{typeof(T).Namespace}.{typeof(T).Name}");
                TelemetryLogger.LogEvent(evt);
            };
        }

        public TelemetryService(
            ILogger<TelemetryService> logger,
            IEventService eventService)
        {
            var config = Program.Configuration;
            Logger = logger;
            EventService = eventService;
            Logger.LogInformation("Starting Telemetry.");
            Logger.LogDebug($"DeviceId: {GetDeviceId()}.");

            TelemetryLogger = CreateLogManager(config);
            InitTelemetryLogger(TelemetryLogger, config);
            TelemetryLogger.LogEvent(
                "TelemetryStarted".AsTelemetryEvent()
            );

            eventService.OnKernelStarted().On += (kernelApp) =>
            {
                TelemetryLogger.LogEvent(
                    "KernelStarted".AsTelemetryEvent()
                );
            };
            eventService.OnKernelStopped().On += (kernelApp) =>
            {
                TelemetryLogger.LogEvent("KernelStopped".AsTelemetryEvent());
                LogManager.UploadNow();
                LogManager.Teardown();
            };

            // Send telemetry that identifies how long after startup each
            // service is ready.
            this.OnServiceInitialized<ISnippets>();
            this.OnServiceInitialized<ISymbolResolver>();
            this.OnServiceInitialized<IMagicSymbolResolver>();
            this.OnServiceInitialized<IWorkspace>();
            eventService.Events<WorkspaceReadyEvent, IWorkspace>().On += (workspace) =>
            {
                var evt = "WorkspaceReady"
                    .AsTelemetryEvent();
                TelemetryLogger.LogEvent(evt);
            };
            this.OnServiceInitialized<IReferences>();
            this.OnServiceInitialized<Microsoft.Quantum.IQSharp.Jupyter.IConfigurationSource>();
            this.OnServiceInitialized<IMetadataController>();
            this.OnServiceInitialized<INugetPackages>();
            this.OnServiceInitialized<IAzureClient>();
            this.OnServiceInitialized<IEntryPointGenerator>();
            this.OnServiceInitialized<IExecutionEngine>();
            this.OnServiceInitialized<ICompilerService>();

            // Subscribe the logger to global events in the event service.
            eventService.Events<ExperimentalFeatureEnabledEvent, ExperimentalFeatureContent>().On += (content) =>
            {
                var evt = "ExperimentalFeatureEnabled".AsTelemetryEvent();
                evt.SetProperty(
                    "FeatureName".WithTelemetryNamespace(),
                    content.FeatureName
                );
                evt.SetProperty(
                    "OptionalDependencies".WithTelemetryNamespace(),
                    string.Join(",", content.OptionalDependencies ?? new List<string>())
                );
                TelemetryLogger.LogEvent(evt);
            };
            eventService.Events<CompletionEvent, CompletionEventArgs>().On += (args) =>
            {
                var evt = "CodeCompletion".AsTelemetryEvent();
                evt.SetProperty("NCompletions".WithTelemetryNamespace(), args.NCompletions);
                evt.SetProperty("Duration".WithTelemetryNamespace(), args.Duration.ToString("G"));
                TelemetryLogger.LogEvent(evt);
            };
            eventService.Events<DeviceCapabilitiesEvent, DeviceCapabilitiesArgs>().On += (args) =>
            {
                var evt = "DeviceCapabilities".AsTelemetryEvent();
                evt.SetProperty("NProcessors".WithTelemetryNamespace(), args.NProcessors?.ToString() ?? "");
                evt.SetProperty("TotalMemoryInGiB".WithTelemetryNamespace(), args.TotalMemoryInGiB?.ToString() ?? "");
                TelemetryLogger.LogEvent(evt);
            };

            // As each different service starts up, we can subscribe to their
            // events as well.
            eventService.OnServiceInitialized<IMetadataController>().On += (metadataController) =>
                metadataController.MetadataChanged += (metadataController, propertyChanged) =>
                    SetSharedContextIfChanged(metadataController, propertyChanged,
                                                nameof(metadataController.ClientId),
                                                nameof(metadataController.UserAgent),
                                                nameof(metadataController.ClientCountry),
                                                nameof(metadataController.ClientLanguage),
                                                nameof(metadataController.ClientHost),
                                                nameof(metadataController.ClientOrigin),
                                                nameof(metadataController.ClientFirstOrigin),
                                                nameof(metadataController.ClientIsNew));
            eventService.OnServiceInitialized<IWorkspace>().On += (workspace) =>
            {
                TelemetryLogger.LogEvent(
                    "WorkspaceInitialized".AsTelemetryEvent()
                );
                workspace.Reloaded += (_, info) => TelemetryLogger.LogEvent(info.AsTelemetryEvent());
                workspace.ProjectLoaded += (_, info) => TelemetryLogger.LogEvent(info.AsTelemetryEvent());
            };
            eventService.OnServiceInitialized<IReferences>().On += (references) =>
                references.PackageLoaded += (_, info) => TelemetryLogger.LogEvent(info.AsTelemetryEvent());
            eventService.OnServiceInitialized<IExecutionEngine>().On += (executionEngine) =>
            {
                TelemetryLogger.LogEvent(
                    "ExecutionEngineInitialized".AsTelemetryEvent()
                );
                if (executionEngine is BaseEngine engine)
                {
                    engine.MagicExecuted += (_, info) => TelemetryLogger.LogEvent(info.AsTelemetryEvent());
                    engine.HelpExecuted += (_, info) => TelemetryLogger.LogEvent(info.AsTelemetryEvent());
                    baseEngine = engine;
                }
            };

            eventService.OnServiceInitialized<IPerformanceMonitor>().On += (performanceMonitor) =>
            {
                performanceMonitor.OnSimulatorPerformanceAvailable += (_, info) => TelemetryLogger.LogEvent(info.AsTelemetryEvent());
                performanceMonitor.OnKernelPerformanceAvailable += (_, info) => TelemetryLogger.LogEvent(info.AsTelemetryEvent());
            };
            eventService.OnServiceInitialized<ISnippets>().On += (snippets) =>
                snippets.SnippetCompiled += (_, info) => TelemetryLogger.LogEvent(info.AsTelemetryEvent());

            eventService.OnServiceInitialized<IAzureClient>().On += (azureClient) =>
            {
                client = azureClient;
                azureClient.ConnectToWorkspace += (_, info) => TelemetryLogger.LogEvent(info.AsTelemetryEvent());
            };
        }

        public Applications.Events.ILogger TelemetryLogger { get; private set; }
        public ILogger<TelemetryService> Logger { get; }
        public IEventService EventService { get; }

        public virtual Applications.Events.ILogger CreateLogManager(IConfiguration config)
        {
            LogManager.Start(new LogConfiguration() {
                //await up to 1 second for the telemetry 
                //to get uploaded before tearing down
                MaxTeardownUploadTime = 1000 
            });
#if REALTIME_TELEMETRY
            //Used for debugging telemetry, performing realtime uploads
            LogManager.SetPowerState(PowerState.Charging);
            LogManager.SetNetCost(NetCost.Low);
            LogManager.SetTransmitProfile("RealTime");
#endif
            return LogManager.GetLogger(TOKEN, out _);
        }

        private void InitTelemetryLogger(Applications.Events.ILogger telemetryLogger, IConfiguration config)
        {
            telemetryLogger.SetContext("AppInfo.Id", "iq#");
            telemetryLogger.SetContext("AppInfo.Version", Kernel.Constants.IQSharpKernelProperties.KernelVersion);
            telemetryLogger.SetContext("CompilerVersion".WithTelemetryNamespace(), typeof(CompilationUnitManager).Assembly.GetName().Version?.ToString());
            telemetryLogger.SetContext("SimulationVersion".WithTelemetryNamespace(), typeof(QuantumSimulator).Assembly.GetName().Version?.ToString());
            telemetryLogger.SetContext("Root".WithTelemetryNamespace(), Path.GetFileName(Directory.GetCurrentDirectory()), PiiKind.GenericData);
            telemetryLogger.SetContext("DeviceId".WithTelemetryNamespace(), GetDeviceId(), PiiKind.GenericData);
            telemetryLogger.SetContext("UserAgent".WithTelemetryNamespace(), config?.GetValue<string>("UserAgent"));
            telemetryLogger.SetContext("HostingEnvironment".WithTelemetryNamespace(), config?.GetValue<string>("HostingEnvironment"));
        }

        /// <summary>
        /// Return an Id for this device, namely, the first non-empty MAC address it can find across all network interfaces (if any).
        /// </summary>
        public static string? GetDeviceId() =>
            NetworkInterface.GetAllNetworkInterfaces()?
                .Select(n => n?.GetPhysicalAddress()?.ToString())
                .Where(address => address != null && !string.IsNullOrWhiteSpace(address) && !address.StartsWith("000000"))
                .FirstOrDefault();

        public void SetSharedContextIfChanged(IMetadataController metadataController, string propertyChanged, params string[] propertyAllowlist)
        {
            if (propertyAllowlist == null
                || !propertyAllowlist.Contains(propertyChanged)) return;
            var property = typeof(IMetadataController)
                            .GetProperties()
                            .Where(p => p.Name == propertyChanged && p.CanRead)
                            .FirstOrDefault();
            if (property != null)
            {
                var value = $"{property.GetValue(metadataController)}";
                Logger.LogInformation($"ClientMetadataChanged: {property.Name}={value}");
                LogManager.SetSharedContext(property.Name, value);
                TelemetryLogger.LogEvent($"ClientMetadataChanged".AsTelemetryEvent());
            }
        }
    }

    public static class TelemetryExtensions
    {
        // Generate a new UUID to use in identifying a particular instance
        // of the kernel process.
        // By making a static property of a non-generic static class, we
        // ensure that there's one session ID per process.
        internal static readonly Guid KernelInstanceId = Guid.NewGuid();

        public static string WithTelemetryNamespace(this string name) =>
            $"Quantum.IQSharp.{name}";

        public static EventProperties AsTelemetryEvent(this string name) =>
            new EventProperties() { Name = name.WithTelemetryNamespace() }
            .SetCommonProperties();

        /// <summary>
        ///      Sets properties common across telemetry events.
        /// </summary>
        /// <returns><paramref name="evt"/> for use in fluent chains.</returns>
        public static EventProperties SetCommonProperties(this EventProperties evt)
        {
            evt.SetProperty("KernelInstanceId".WithTelemetryNamespace(), KernelInstanceId);
            evt.SetProperty("ExecutionCount".WithTelemetryNamespace(), TelemetryService.baseEngine?.ExecutionCount?.ToString());
            evt.SetProperty("CurrentTarget".WithTelemetryNamespace(), TelemetryService.client?.ActiveTargetId);
            evt.SetProperty("CurrentTargetCapability".WithTelemetryNamespace(), TelemetryService.client?.TargetCapability?.ToString());
            evt.SetProperty("CurrentSubscription".WithTelemetryNamespace(), (TelemetryService.client as AzureClient.AzureClient)?.ActiveWorkspace?.SubscriptionId, PiiKind.GenericData);
            evt.SetProperty(
                "TimeSinceStart".WithTelemetryNamespace(),
                // The "c" format converts using the "constant" format, which
                // is stable across .NET cultures and versions.
                (
                    DateTime.UtcNow - System.Diagnostics.Process.GetCurrentProcess().StartTime.ToUniversalTime()
                ).ToString("c")
            );

            return evt;
        }

        public static EventProperties AsTelemetryEvent(this ReloadedEventArgs info)
        {
            var evt = new EventProperties() { Name = "WorkspaceReload".WithTelemetryNamespace() };

            evt.SetProperty("Workspace".WithTelemetryNamespace(), Path.GetFileName(info.Workspace), PiiKind.GenericData);
            evt.SetProperty("Status".WithTelemetryNamespace(), info.Status);
            evt.SetProperty("FileCount".WithTelemetryNamespace(), info.FileCount);
            evt.SetProperty("ProjectCount".WithTelemetryNamespace(), info.ProjectCount);
            evt.SetProperty("Errors".WithTelemetryNamespace(), string.Join(",", info.Errors?.OrderBy(e => e) ?? Enumerable.Empty<string>()));
            evt.SetProperty("Duration".WithTelemetryNamespace(), info.Duration.ToString("G"));
            evt.SetCommonProperties();

            return evt;
        }

        public static EventProperties AsTelemetryEvent(this SnippetCompiledEventArgs info)
        {
            var evt = new EventProperties() { Name = "Compile".WithTelemetryNamespace() };

            evt.SetProperty("Status".WithTelemetryNamespace(), info.Status);
            evt.SetProperty("Errors".WithTelemetryNamespace(), string.Join(",", info.Errors?.OrderBy(e => e) ?? Enumerable.Empty<string>()));
            evt.SetProperty("Namespaces".WithTelemetryNamespace(),
                string.Join(",", info.Namespaces?.Where(n => n.StartsWith("Microsoft.Quantum.")).OrderBy(n => n) ?? Enumerable.Empty<string>()));
            evt.SetProperty("Duration".WithTelemetryNamespace(), info.Duration.ToString("G"));
            evt.SetCommonProperties();

            return evt;
        }

        public static EventProperties AsTelemetryEvent(this PackageLoadedEventArgs info)
        {
            var evt = new EventProperties() { Name = "PackageLoad".WithTelemetryNamespace() };

            evt.SetProperty("PackageId".WithTelemetryNamespace(),
                info.PackageId.StartsWith("Microsoft.Quantum.") ? info.PackageId : "other package");
            evt.SetProperty("PackageVersion".WithTelemetryNamespace(), info.PackageVersion);
            evt.SetProperty("Duration".WithTelemetryNamespace(), info.Duration.ToString("G"));
            evt.SetCommonProperties();

            return evt;
        }

        public static EventProperties AsTelemetryEvent(this ProjectLoadedEventArgs info)
        {
            var evt = new EventProperties() { Name = "ProjectLoad".WithTelemetryNamespace() };

            evt.SetProperty("ProjectUri".WithTelemetryNamespace(), info.ProjectUri?.ToString(), PiiKind.Uri);
            evt.SetProperty("SourceFileCount".WithTelemetryNamespace(), info.SourceFileCount);
            evt.SetProperty("ProjectReferenceCount".WithTelemetryNamespace(), info.ProjectReferenceCount);
            evt.SetProperty("PackageReferenceCount".WithTelemetryNamespace(), info.PackageReferenceCount);
            evt.SetProperty("UserAdded".WithTelemetryNamespace(), info.UserAdded);
            evt.SetProperty("Duration".WithTelemetryNamespace(), info.Duration.ToString("G"));
            evt.SetCommonProperties();

            return evt;
        }

        public static EventProperties AsTelemetryEvent(this ExecutedEventArgs info)
        {
            var evt = new EventProperties() { Name = "Action".WithTelemetryNamespace() };

            evt.SetProperty("Command".WithTelemetryNamespace(), info.Symbol?.Name);
            evt.SetProperty("Kind".WithTelemetryNamespace(), info.Symbol?.Kind.ToString());
            evt.SetProperty("Status".WithTelemetryNamespace(), info.Result.Status.ToString());
            evt.SetProperty("Duration".WithTelemetryNamespace(), info.Duration.ToString("G"));
            evt.SetCommonProperties();

            return evt;
        }

        public static EventProperties AsTelemetryEvent(this SimulatorPerformanceArgs info)
        {
            var evt = new EventProperties() { Name = "SimulatorPerformance".WithTelemetryNamespace() };

            evt.SetProperty("SimulatorName".WithTelemetryNamespace(), info.SimulatorName);
            evt.SetProperty("NQubits".WithTelemetryNamespace(), info.NQubits);
            evt.SetProperty("Duration".WithTelemetryNamespace(), info.Duration.ToString("G"));

            return evt;
        }

        public static EventProperties AsTelemetryEvent(this KernelPerformanceArgs info)
        {
            var evt = new EventProperties() { Name = "KernelPerformance".WithTelemetryNamespace() };

            evt.SetProperty("ManagedRamUsed".WithTelemetryNamespace(), info.ManagedRamUsed);
            evt.SetProperty("TotalRamUsed".WithTelemetryNamespace(), info.TotalRamUsed);

            return evt;
        }

        public static EventProperties AsTelemetryEvent(this ConnectToWorkspaceEventArgs info)
        {
            var evt = new EventProperties() { Name = "ConnectToWorkspace".WithTelemetryNamespace() };

            evt.SetProperty("Status".WithTelemetryNamespace(), info.Status.ToString());
            evt.SetProperty("Error".WithTelemetryNamespace(), info.Error?.ToString());
            evt.SetProperty("Location".WithTelemetryNamespace(), info.Location);
            evt.SetProperty("UseCustomStorage".WithTelemetryNamespace(), info.UseCustomStorage);
            evt.SetProperty("CredentialType".WithTelemetryNamespace(), info.CredentialType.ToString());
            evt.SetProperty("Duration".WithTelemetryNamespace(), info.Duration.ToString("G"));
            evt.SetCommonProperties();

            return evt;
        }
    }
}

#endif
