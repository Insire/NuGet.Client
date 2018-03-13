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
    public sealed class GetSourceAgnosticOperationClaimsRequest
    {

        [JsonConstructor]
        public GetSourceAgnosticOperationClaimsRequest()
        {
        }
    }
}