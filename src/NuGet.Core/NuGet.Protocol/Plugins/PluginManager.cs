// Copyright (c) .NET Foundation. All rights reserved.
// Licensed under the Apache License, Version 2.0. See License.txt in the project root for license information.

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json.Linq;
using NuGet.Common;
using NuGet.Configuration;
using NuGet.Packaging;
using NuGet.Protocol.Plugins;
using NuGet.Shared;

namespace NuGet.Protocol.Core.Types
{
    /// <summary>
    /// A plugin manager. This manages all the live plugins and their operation claims.
    /// Invoked in by both the credential provider and the PluginResourceProvider
    /// </summary>
    /// <remarks>This is unsealed only to facilitate testing.</remarks>
    public class PluginManager : IDisposable
    {
        private const string _idleTimeoutEnvironmentVariable = "NUGET_PLUGIN_IDLE_TIMEOUT_IN_SECONDS";
        private const string _pluginPathsEnvironmentVariable = "NUGET_PLUGIN_PATHS";

        private ConnectionOptions _connectionOptions;
        private Lazy<IPluginDiscoverer> _discoverer;
        private bool _isDisposed;
        private IPluginFactory _pluginFactory;
        private ConcurrentDictionary<PluginPackageSourceKey, Lazy<Task<IReadOnlyList<OperationClaim>>>> _pluginOperationClaims;
        private ConcurrentDictionary<string, Lazy<IPluginMulticlientUtilities>> _pluginUtilities;
        private string _rawPluginPaths;

        private static Lazy<int> _currentProcessId = new Lazy<int>(GetCurrentProcessId);

        /// <summary>
        /// Gets an environment variable reader.
        /// </summary>
        /// <remarks>This is non-private only to facilitate testing.</remarks>
        public IEnvironmentVariableReader EnvironmentVariableReader { get; private set; }

        /// <summary>
        /// Initializes a new <see cref="PluginManager" /> class.
        /// </summary>
        public PluginManager()
        {
            Reinitialize(
                new EnvironmentVariableWrapper(),
                new Lazy<IPluginDiscoverer>(InitializeDiscoverer),
                (TimeSpan idleTimeout) => new PluginFactory(idleTimeout));
        }

        /// <summary>
        /// Disposes of this instance.
        /// </summary>
        public void Dispose()
        {
            if (!_isDisposed)
            {
                if (_discoverer.IsValueCreated)
                {
                    _discoverer.Value.Dispose();
                }

                _pluginFactory.Dispose();

                GC.SuppressFinalize(this);

                _isDisposed = true;
            }
        }

        public async Task<IEnumerable<PluginCreationResult>> TryCreate(
            SourceRepository source,
            CancellationToken cancellationToken)
        {
            if (source == null)
            {
                throw new ArgumentNullException(nameof(source));
            }

            cancellationToken.ThrowIfCancellationRequested();

            // Fast path
            if (source.PackageSource.IsHttp && IsPluginPossiblyAvailable())
            {
                var serviceIndex = await source.GetResourceAsync<ServiceIndexResourceV3>(cancellationToken);

                if (serviceIndex != null)
                {
                    var serviceIndexJson = JObject.Parse(serviceIndex.Json);
                    var results = await _discoverer.Value.DiscoverAsync(cancellationToken);

                    var pluginCreationResults = await TryCreatePlugin(
                        source.PackageSource,
                        serviceIndexJson,
                        cancellationToken);

                    return pluginCreationResults;
                }
            }

            return null;
        }

        public async Task<IEnumerable<PluginCreationResult>> TryCreatePlugin(
            PackageSource source,
            JObject serviceIndexJson,
            CancellationToken cancellationToken)
        {
            if (source == null)
            {
                throw new ArgumentNullException(nameof(source));
            }

            cancellationToken.ThrowIfCancellationRequested();
            var pluginCreationResults = new List<PluginCreationResult>();

            // Fast path
            if (source.IsHttp && IsPluginPossiblyAvailable())
            {
                var results = await _discoverer.Value.DiscoverAsync(cancellationToken);

                foreach (var result in results)
                {
                    PluginCreationResult pluginCreationResult = null;

                    if (result.PluginFile.State == PluginFileState.Valid)
                    {
                        var plugin = await _pluginFactory.GetOrCreateAsync(
                            result.PluginFile.Path,
                            PluginConstants.PluginArguments,
                            new RequestHandlers(),
                            _connectionOptions,
                            cancellationToken);

                        var utilities = _pluginUtilities.GetOrAdd(
                            plugin.Id,
                            path => new Lazy<IPluginMulticlientUtilities>(
                                () => new PluginMulticlientUtilities()));

                        await utilities.Value.DoOncePerPluginLifetimeAsync(
                            MessageMethod.MonitorNuGetProcessExit.ToString(),
                            () => plugin.Connection.SendRequestAndReceiveResponseAsync<MonitorNuGetProcessExitRequest, MonitorNuGetProcessExitResponse>(
                                MessageMethod.MonitorNuGetProcessExit,
                                new MonitorNuGetProcessExitRequest(_currentProcessId.Value),
                                cancellationToken),
                            cancellationToken);

                        await utilities.Value.DoOncePerPluginLifetimeAsync(
                            MessageMethod.Initialize.ToString(),
                            () => InitializePluginAsync(plugin, _connectionOptions.RequestTimeout, cancellationToken),
                            cancellationToken);

                        var lazyOperationClaims = _pluginOperationClaims.GetOrAdd(
                                new PluginPackageSourceKey(result.PluginFile.Path, source.Source),
                                key => new Lazy<Task<IReadOnlyList<OperationClaim>>>(() =>
                                (
                                plugin.Connection.ProtocolVersion.Equals(Plugins.ProtocolConstants.CurrentVersion) ?
                                GetPluginGetSourceOperationClaims(
                                    plugin,
                                    source.Source,
                                    cancellationToken)
                                    :
                                GetPluginOperationClaimsAsync(
                                    plugin,
                                    source.Source,
                                    serviceIndexJson,
                                    cancellationToken))));

                        await lazyOperationClaims.Value;

                        pluginCreationResult = new PluginCreationResult(
                            plugin,
                            utilities.Value,
                            lazyOperationClaims.Value.Result);
                    }
                    else
                    {
                        pluginCreationResult = new PluginCreationResult(result.Message);
                    }

                    pluginCreationResults.Add(pluginCreationResult);
                }
            }

            return pluginCreationResults;
        }

        /// <summary>
        /// Reinitializes static state.
        /// </summary>
        /// <remarks>This is non-private only to facilitate unit testing.
        /// This should not be called by product code.</remarks>
        /// <param name="reader">An environment variable reader.</param>
        /// <param name="pluginDiscoverer">A lazy plugin discoverer.</param>
        /// <param name="pluginFactoryCreator">A plugin factory creator.</param>
        /// <exception cref="ArgumentNullException">Thrown if <paramref name="reader" /> is <c>null</c>.</exception>
        /// <exception cref="ArgumentNullException">Thrown if <paramref name="pluginDiscoverer" />
        /// is <c>null</c>.</exception>
        /// <exception cref="ArgumentNullException">Thrown if <paramref name="pluginFactoryCreator" />
        /// is <c>null</c>.</exception>
        public void Reinitialize(IEnvironmentVariableReader reader,
            Lazy<IPluginDiscoverer> pluginDiscoverer,
            Func<TimeSpan, IPluginFactory> pluginFactoryCreator)
        {
            if (reader == null)
            {
                throw new ArgumentNullException(nameof(reader));
            }

            if (pluginDiscoverer == null)
            {
                throw new ArgumentNullException(nameof(pluginDiscoverer));
            }

            if (pluginFactoryCreator == null)
            {
                throw new ArgumentNullException(nameof(pluginFactoryCreator));
            }

            EnvironmentVariableReader = reader;
            _rawPluginPaths = reader.GetEnvironmentVariable(_pluginPathsEnvironmentVariable);

            _connectionOptions = ConnectionOptions.CreateDefault(reader);

            var idleTimeoutInSeconds = EnvironmentVariableReader.GetEnvironmentVariable(_idleTimeoutEnvironmentVariable);
            var idleTimeout = TimeoutUtilities.GetTimeout(idleTimeoutInSeconds, PluginConstants.IdleTimeout);

            _discoverer = pluginDiscoverer;
            _pluginFactory = pluginFactoryCreator(idleTimeout);
            _pluginOperationClaims = new ConcurrentDictionary<PluginPackageSourceKey, Lazy<Task<IReadOnlyList<OperationClaim>>>>();
            _pluginUtilities = new ConcurrentDictionary<string, Lazy<IPluginMulticlientUtilities>>(
                StringComparer.OrdinalIgnoreCase);
        }

        

        private async Task<IReadOnlyList<OperationClaim>> GetPluginGetSourceOperationClaims(
            IPlugin plugin,
            string packageSourceRepository,
            CancellationToken cancellationToken)
        {
                var payload = new GetSourceOperationClaimsRequest(packageSourceRepository);

                var response = await plugin.Connection.SendRequestAndReceiveResponseAsync<GetSourceOperationClaimsRequest, GetSourceOperationClaimsResponse>(
                    MessageMethod.GetOperationClaims,
                    payload,
                    cancellationToken);
                if (response == null)
                {
                    return new List<OperationClaim>();
                }

                return response.Claims;
        }

        private async Task<IReadOnlyList<OperationClaim>> GetPluginOperationClaimsAsync(
            IPlugin plugin,
            string packageSourceRepository,
            JObject serviceIndex,
            CancellationToken cancellationToken)
        {
            var payload = new GetOperationClaimsRequest(packageSourceRepository, serviceIndex);

            var response = await plugin.Connection.SendRequestAndReceiveResponseAsync<GetOperationClaimsRequest, GetOperationClaimsResponse>(
                MessageMethod.GetOperationClaims,
                payload,
                cancellationToken);
            if (response == null)
            {
                return new List<OperationClaim>();
            }

            return response.Claims;
        }

        private PluginDiscoverer InitializeDiscoverer()
        {
            var verifier = EmbeddedSignatureVerifier.Create();

            return new PluginDiscoverer(_rawPluginPaths, verifier);
        }

        private bool IsPluginPossiblyAvailable()
        {
            return !string.IsNullOrEmpty(_rawPluginPaths);
        }

        private static int GetCurrentProcessId()
        {
            using (var process = Process.GetCurrentProcess())
            {
                return process.Id;
            }
        }

        private static async Task InitializePluginAsync(
            IPlugin plugin,
            TimeSpan requestTimeout,
            CancellationToken cancellationToken)
        {
            var clientVersion = MinClientVersionUtility.GetNuGetClientVersion().ToNormalizedString();
            var culture = CultureInfo.CurrentCulture.Name;
            var payload = new InitializeRequest(
                clientVersion,
                culture,
                requestTimeout);

            var response = await plugin.Connection.SendRequestAndReceiveResponseAsync<InitializeRequest, InitializeResponse>(
                MessageMethod.Initialize,
                payload,
                cancellationToken);

            if (response != null && response.ResponseCode != MessageResponseCode.Success)
            {
                throw new PluginException(Strings.Plugin_InitializationFailed);
            }

            plugin.Connection.Options.SetRequestTimeout(requestTimeout);
        }

        private sealed class PluginPackageSourceKey : IEquatable<PluginPackageSourceKey>
        {
            internal string PluginFilePath { get; }
            internal string PackageSourceRepository { get; }

            internal PluginPackageSourceKey(string pluginFilePath, string packageSourceRepository)
            {
                PluginFilePath = pluginFilePath;
                PackageSourceRepository = packageSourceRepository;
            }

            public override bool Equals(object obj)
            {
                return Equals(obj as PluginPackageSourceKey);
            }

            public override int GetHashCode()
            {
                return HashCodeCombiner.GetHashCode(PluginFilePath, PackageSourceRepository);
            }

            public bool Equals(PluginPackageSourceKey other)
            {
                if (ReferenceEquals(this, other))
                {
                    return true;
                }

                if (ReferenceEquals(null, other))
                {
                    return false;
                }

                return string.Equals(
                        PluginFilePath,
                        other.PluginFilePath,
                        StringComparison.OrdinalIgnoreCase)
                    && string.Equals(
                        PackageSourceRepository,
                        other.PackageSourceRepository,
                        StringComparison.OrdinalIgnoreCase);
            }
        }
    }
}