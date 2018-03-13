using System;
using System.Collections.Generic;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using NuGet.Protocol.Core.Types;
using NuGet.Protocol.Plugins;

namespace NuGet.Credentials
{
    public class SecureCredentialProviderBuilder
    {
        private Common.ILogger Logger;
        public SecureCredentialProviderBuilder(Common.ILogger logger)
        {
            Logger = logger;
        }
        public async Task<IEnumerable<ICredentialProvider>> BuildAll()
        {
           var availablePlugins =  await PluginManager.Instance.FindAvailablePlugins(CancellationToken.None);

            var plugins = new List<ICredentialProvider>();
            foreach(var pluginDiscoveryResult in availablePlugins)
            {
                plugins.Add(new SecurePluginCredentialProvider(pluginDiscoveryResult, Logger));
            }

            return plugins;
        }

    }
}
