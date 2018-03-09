// Copyright (c) .NET Foundation. All rights reserved.
// Licensed under the Apache License, Version 2.0. See License.txt in the project root for license information.

using System;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace NuGet.Protocol.Plugins
{
    /// <summary>
    /// A query to a plugin about operation claims
    /// </summary>
    public sealed class GetSourceOperationClaimsRequest
    {
        /// <summary>
        /// The package source repository location
        /// </summary>
        [JsonRequired]
        public string PackageSourceRepository { get; }

        /// <param name="packageSourceRepository"></param>
        /// <exception cref="ArgumentException">Thrown if <paramref name="packageSourceRepository" /> is null or empty
        [JsonConstructor]
        public GetSourceOperationClaimsRequest(string packageSourceRepository)
        {
            if (string.IsNullOrEmpty(packageSourceRepository))
            {
                throw new ArgumentNullException(nameof(packageSourceRepository));
            }
            PackageSourceRepository = packageSourceRepository;
        }
    }
}