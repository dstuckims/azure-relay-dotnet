using System;
using System.Collections.Generic;
using System.Text;

namespace Microsoft.Azure.Relay.UnitTests
{
    public class HybridConnectionDescription
    {
        /// <summary>Initializes a new instance of the <see cref="HybridConnectionDescription" /> class.</summary>
        /// <param name="path">The relative path of the HybridConnection.</param>
        public HybridConnectionDescription(string path)
        {
            this.Path = path;
        }

        /// <summary>Gets the relative path of the HybridConnection.</summary>
        /// <value>The relative path of the HybridConnection.</value>
        public string Path { get; internal set; }

        /// <summary>Gets or sets whether client authorization is needed for this HybridConnection.</summary>
        /// <value>true if client authorization is needed for this HybridConnection; otherwise, false.</value>
        public bool RequiresClientAuthorization
        {
            get; set;
        }

        /// <summary>Gets the <see cref="AuthorizationRules" />.</summary>
        /// <value>The <see cref="AuthorizationRules" />.</value>
        public AuthorizationRules Authorization
        {
            get => this.authorizationRules ?? (this.authorizationRules = new AuthorizationRules());
        }

        /// <summary>Gets or sets the user metadata associated with this instance.</summary>
        /// <value>The user metadata associated with this instance.</value>
        /// <exception cref="ArgumentOutOfRangeException">Thrown if the length of value is greater than 1024 characters.</exception>
        public string UserMetadata
        {
            get; set;
        }

        AuthorizationRules authorizationRules;
    }
}
