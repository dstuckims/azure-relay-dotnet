namespace Microsoft.Azure.Relay.UnitTests
{
    using System;
    using System.Diagnostics.CodeAnalysis;
    using System.Linq;
    using System.Collections.Generic;
    using System.Runtime.Serialization;
    using System.Security.Cryptography;
    using System.Text;
    using System.Web;

    /// <summary>Defines the authorization rule for shared access operation.</summary>
    public class SharedAccessAuthorizationRule : AuthorizationRule
    {
        const int SupportedSASKeyLength = 44;
        const string FixedClaimType = "SharedAccessKey";
        const string FixedClaimValue = "None";
        readonly string keyName;
        string primaryKey;
        string secondaryKey;

        /// <summary>Initializes a new instance of the <see cref="SharedAccessAuthorizationRule" /> class.</summary>
        /// <param name="keyName">The authorization rule key name.</param>
        /// <param name="rights">The list of rights.</param>
        public SharedAccessAuthorizationRule(string keyName, IEnumerable<AccessRights> rights)
            : this(keyName, GenerateRandomKey(), GenerateRandomKey(), rights)
        {
        }

        /// <summary>Initializes a new instance of the <see cref="SharedAccessAuthorizationRule" /> class.</summary>
        /// <param name="keyName">The authorization rule key name.</param>
        /// <param name="primaryKey">The primary key for the authorization rule.</param>
        /// <param name="rights">The list of rights.</param>
        public SharedAccessAuthorizationRule(string keyName, string primaryKey, IEnumerable<AccessRights> rights)
            : this(keyName, primaryKey, GenerateRandomKey(), rights)
        {
        }

        /// <summary>Initializes a new instance of the <see cref="SharedAccessAuthorizationRule" /> class.</summary>
        /// <param name="keyName">The authorization rule key name.</param>
        /// <param name="primaryKey">The primary key for the authorization rule.</param>
        /// <param name="secondaryKey">The secondary key for the authorization rule.</param>
        /// <param name="rights">The list of rights.</param>
        public SharedAccessAuthorizationRule(string keyName, string primaryKey, string secondaryKey, IEnumerable<AccessRights> rights)
        {
            this.keyName = keyName ?? throw new ArgumentNullException(nameof(keyName));
            this.PrimaryKey = primaryKey ?? throw new ArgumentNullException(nameof(primaryKey));
            this.SecondaryKey = secondaryKey ?? throw new ArgumentNullException(nameof(secondaryKey));
            this.Rights = rights;
            this.ClaimType = FixedClaimType;
            this.ClaimValue = FixedClaimValue;

            if (!CheckBase64(primaryKey) || !CheckBase64(secondaryKey))
            {
                throw new ArgumentException("Key must be non-null and valid Base64", !CheckBase64(primaryKey) ? nameof(primaryKey) : nameof(secondaryKey));
            }
            else if (primaryKey.Length != SupportedSASKeyLength || secondaryKey.Length != SupportedSASKeyLength)
            {
                throw new ArgumentException($"SR.SharedAccessRuleAllowsFixedLengthKeys({SupportedSASKeyLength})", primaryKey.Length != SupportedSASKeyLength ? nameof(primaryKey) : nameof(secondaryKey));
            }
        }

        static bool CheckBase64(string base64EncodedString)
        {
            try
            {
                Convert.FromBase64String(base64EncodedString);
                return true;
            }
            catch (Exception)
            {
                return false;
            }
        }

        /// <summary>Gets or sets the authorization rule key name.</summary>
        /// <value>The authorization rule key name.</value>
        public override sealed string KeyName
        {
            get { return this.keyName; }
        }

        /// <summary>Gets or sets the primary key for the authorization rule.</summary>
        /// <value>The primary key for the authorization rule.</value>
        public string PrimaryKey
        {
            get { return this.primaryKey; }
            set
            {
                if (string.IsNullOrWhiteSpace(value))
                {
                    throw new ArgumentNullException(nameof(PrimaryKey));
                }
                else if (value.Length > SharedAccessSignatureToken.MaxKeyLength)
                {
                    throw new ArgumentOutOfRangeException(nameof(PrimaryKey), value, $"SRCore.ArgumentStringTooBig({SharedAccessSignatureToken.MaxKeyLength}");
                }

                this.primaryKey = value;
            }
        }

        /// <summary>Gets or sets the secondary key for the authorization rule.</summary>
        /// <value>The secondary key for the authorization rule.</value>
        public string SecondaryKey
        {
            get { return this.secondaryKey; }
            set
            {
                if (string.IsNullOrWhiteSpace(value))
                {
                    throw new ArgumentNullException(nameof(SecondaryKey));
                }
                else if (value.Length > SharedAccessSignatureToken.MaxKeyLength)
                {
                    throw new ArgumentOutOfRangeException(nameof(SecondaryKey), value, $"SRCore.ArgumentStringTooBig({SharedAccessSignatureToken.MaxKeyLength}");
                }

                this.secondaryKey = value;
            }
        }

        /// <summary>Checks the validity of the specified access rights.</summary>
        /// <param name="value">The access rights to check.</param>
        protected override void ValidateRights(IEnumerable<AccessRights> value)
        {
            base.ValidateRights(value);

            if (!IsValidCombinationOfRights(value))
            {
                throw new ArgumentException("Manage permission should also include Send and Listen.");
            }
        }

        static bool IsValidCombinationOfRights(IEnumerable<AccessRights> rights)
        {
            return !rights.Contains(AccessRights.Manage) || rights.Count() == 3;
        }

        /// <summary>Returns the hash code for this instance.</summary>
        /// <returns>The hash code for this instance.</returns>
        public override int GetHashCode()
        {
            int result = base.GetHashCode();

            foreach (string value in new string[] { this.KeyName, this.PrimaryKey, this.SecondaryKey })
            {
                if (!string.IsNullOrEmpty(value))
                {
                    result ^= value.GetHashCode();
                }
            }

            return result;
        }

        /// <summary>Determines whether the specified object is equal to the current object.</summary>
        /// <param name="obj">The object to compare with the current object.</param>
        /// <returns>true if the specified object is equal to the current object; otherwise, false.</returns>
        public override bool Equals(object obj)
        {
            if (!base.Equals(obj))
            {
                return false;
            }

            var comparand = (SharedAccessAuthorizationRule)obj;
            return string.Equals(this.KeyName, comparand.KeyName, StringComparison.OrdinalIgnoreCase) &&
                   string.Equals(this.PrimaryKey, comparand.PrimaryKey, StringComparison.OrdinalIgnoreCase) &&
                   string.Equals(this.SecondaryKey, comparand.SecondaryKey, StringComparison.OrdinalIgnoreCase);
        }


        /// <summary>Generates the random key for the authorization rule.</summary>
        /// <returns>The random key for the authorization rule.</returns>
        public static string GenerateRandomKey()
        {
            byte[] key256 = new byte[32];
            using (var rngCryptoServiceProvider = new RNGCryptoServiceProvider())
            {
                rngCryptoServiceProvider.GetBytes(key256);
            }

            return Convert.ToBase64String(key256);
        }
    }
}