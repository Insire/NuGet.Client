// Copyright (c) .NET Foundation. All rights reserved.
// Licensed under the Apache License, Version 2.0. See License.txt in the project root for license information.
using System.Collections.Generic;
using System.Linq;
using Newtonsoft.Json;

namespace NuGet.Protocol.Plugins.Messages
{
    public class GetAuthenticationCredentialsResponse
    {
        public string Username { get; set; }

        public string Password { get; set; }

        public string Message { get; set; }

        /// <summary>
        /// Gets or sets the list of authentication types this credential is applicable to. Useful values include
        /// <c>basic</c>, <c>digest</c>, <c>negotiate</c>, and <c>ntlm</c>
        /// </summary>
        public IList<string> AuthTypes { get; set; }

        [JsonConstructor]
        public GetAuthenticationCredentialsResponse(string username, string password, string message, IList<string> authTypes)
        {
            Username = username;
            Password = password;
            Message = message;
            AuthTypes = authTypes;
        }

        /// <summary>
        /// Gets a value indicating whether the provider returnd a valid response.
        /// </summary>
        /// <remarks>
        /// Either Username or Password (or both) must be set, and AuthTypes must either be null or contain at least
        /// one element
        /// </remarks>
        public bool IsValid => (!string.IsNullOrWhiteSpace(Username) || !string.IsNullOrWhiteSpace(Password))
                               && (AuthTypes == null || AuthTypes.Any());
    }
}
