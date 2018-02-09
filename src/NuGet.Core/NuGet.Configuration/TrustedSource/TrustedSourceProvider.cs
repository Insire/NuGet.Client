// Copyright (c) .NET Foundation. All rights reserved.
// Licensed under the Apache License, Version 2.0. See License.txt in the project root for license information.

using System;
using System.Collections.Generic;
using System.Linq;
using NuGet.Common;

namespace NuGet.Configuration
{
    public class TrustedSourceProvider : ITrustedSourceProvider
    {
        private ISettings _settings;

        public TrustedSourceProvider(ISettings settings)
        {
            _settings = settings;
        }

        public IEnumerable<TrustedSource> LoadTrustedSources()
        {
            var trustedSources = new List<TrustedSource>();
            var trustedSourceNames = _settings.GetAllSubsections(ConfigurationConstants.TrustedSources);

            foreach (var trustedSourceName in trustedSourceNames)
            {
                var trustedSource = LoadTrustedSource(trustedSourceName);

                if (trustedSource != null)
                {
                    trustedSources.Add(trustedSource);
                }
            }

            return trustedSources;
        }

        public TrustedSource LoadTrustedSource(string packageSourceName)
        {
            TrustedSource trustedSource = null;
            var settingValues = _settings.GetNestedSettingValues(ConfigurationConstants.TrustedSources, packageSourceName);

            if (settingValues?.Count > 0)
            {
                trustedSource = new TrustedSource(packageSourceName);
                foreach (var settingValue in settingValues)
                {
                    if (string.Equals(settingValue.Key, ConfigurationConstants.ServiceIndex, StringComparison.OrdinalIgnoreCase))
                    {
                        trustedSource.ServiceIndex = settingValue.Value;
                    }
                    else
                    {
                        var fingerprint = settingValue.Key;
                        var subjectName = settingValue.Value;
                        var algorithm = HashAlgorithmName.SHA256;

                        if (settingValue.AdditionalData.TryGetValue(ConfigurationConstants.FingerprintAlgorithm, out var algorithmString) &&
                            CryptoHashUtility.GetHashAlgorithmName(algorithmString) != HashAlgorithmName.Unknown)
                        {
                            algorithm = CryptoHashUtility.GetHashAlgorithmName(algorithmString);
                        }

                        trustedSource.Certificates.Add(new CertificateTrustEntry(fingerprint, subjectName, algorithm, settingValue.Priority));
                    }
                }
            }

            return trustedSource;
        }

        public void SaveTrustedSources(IEnumerable<TrustedSource> sources)
        {
            foreach (var source in sources)
            {
                SaveTrustedSource(source);
            }
        }

        public void SaveTrustedSource(TrustedSource source)
        {
            var matchingSource = LoadTrustedSources()
                .Where(s => string.Equals(s.SourceName, source.SourceName, StringComparison.OrdinalIgnoreCase))
                .FirstOrDefault();

            var settingValues = new List<SettingValue>();

            foreach(var cert in source.Certificates)
            {
                var settingValue = new SettingValue(cert.Fingerprint, cert.SubjectName, isMachineWide: false, priority: cert.Priority);
                settingValue.AdditionalData.Add(ConfigurationConstants.FingerprintAlgorithm, cert.FingerprintAlgorithm.ToString());

                settingValues.Add(settingValue);
            }

            if (matchingSource != null)
            {
                _settings.UpdateSubsections(ConfigurationConstants.TrustedSources, source.SourceName, settingValues);
            }
            else
            {
                _settings.SetNestedSettingValues(ConfigurationConstants.TrustedSources, source.SourceName, settingValues);
            }
        }
    }
}
