using System;
using System.Collections.Generic;
using System.Linq;

namespace Microsoft.Azure.Relay.UnitTests
{
    public abstract class AuthorizationRule
    {
        /// <summary>The name identifier claim rule.</summary>
        public const string NameIdentifierClaimType = "http://schemas.xmlsoap.org/ws/2005/05/identity/claims/nameidentifier";
        /// <summary>The short name identifier claim rule.</summary>
        public const string ShortNameIdentifierClaimType = "nameidentifier";
        /// <summary>The UPN claim rule.</summary>
        public const string UpnClaimType = "http://schemas.xmlsoap.org/ws/2005/05/identity/claims/upn";
        /// <summary>The short UPN claim rule.</summary>
        public const string ShortUpnClaimType = "upn";
        /// <summary>The role role claim rule.</summary>
        public const string RoleClaimType = "http://schemas.microsoft.com/ws/2008/06/identity/claims/role";
        /// <summary>The role role claim rule.</summary>
        public const string RoleRoleClaimType = "role";
        /// <summary>The shared access key claim rule.</summary>
        public const string SharedAccessKeyClaimType = "sharedaccesskey";

        const int SupportedClaimsCount = 3;
        IEnumerable<AccessRights> rights;

        internal AuthorizationRule()
        {
            // TODO: Add property keyname with default value for server
            CreatedTime = DateTime.UtcNow;
            ModifiedTime = DateTime.UtcNow;
            Revision = 0;
        }

        /// <summary>Gets or sets the name identifier of the issuer.</summary>
        /// <value>The name identifier of the issuer.</value>
        public string IssuerName
        {
            get; set;
        }

        /// <summary>Gets or sets the claim type.</summary>
        /// <value>The claim type.</value>
        public string ClaimType
        {
            get; set;
        }

        /// <summary>Gets or sets the claim value which is either ‘Send’, ‘Listen’, or ‘Manage’.</summary>
        /// <value>The claim value which is either ‘Send’, ‘Listen’, or ‘Manage’.</value>
        public string ClaimValue
        {
            get; set;
        }

        /// <summary>Gets or sets the list of rights.</summary>
        /// <value>The list of rights.</value>
        public IEnumerable<AccessRights> Rights
        {
            get { return this.rights; }
            set
            {
                this.ValidateRights(value);
                this.rights = value;
            }
        }

        /// <summary>Gets or sets the authorization rule key name.</summary>
        /// <value>The authorization rule key name.</value>
        public abstract string KeyName { get; }

        /// <summary>Gets or sets the date and time when the authorization rule was created.</summary>
        /// <value>The date and time when the authorization rule was created.</value>
        public DateTime CreatedTime { get; private set; }

        /// <summary>Gets or sets the date and time when the authorization rule was modified.</summary>
        /// <value>The date and time when the authorization rule was modified.</value>
        public DateTime ModifiedTime { get; private set; }

        /// <summary>Gets or sets the modification revision number.</summary>
        /// <value>The modification revision number.</value>
        public long Revision { get; set; }

        /// <summary>Enables derived classes to provide custom handling when validating the authorization rule.</summary>
        protected virtual void OnValidate()
        {
        }

        /// <summary>Checks the validity of the specified access rights.</summary>
        /// <param name="value">The access rights to check.</param>
        protected virtual void ValidateRights(IEnumerable<AccessRights> value)
        {
            if (value == null || !value.Any() || value.Count() > SupportedClaimsCount)
            {
                throw new ArgumentException($"SR.NullEmptyRights({SupportedClaimsCount})");
            }
            else if (!AreAccessRightsUnique(value))
            {
                throw new ArgumentException("SR.CannotHaveDuplicateAccessRights");
            }
        }

        /// <summary>Returns the hash code for this instance.</summary>
        /// <returns>The hash code for this instance.</returns>
        public override int GetHashCode()
        {
            int result = 0;
            foreach (string value in new string[] { this.IssuerName, this.ClaimValue, this.ClaimType })
            {
                if (!string.IsNullOrEmpty(value))
                {
                    result ^= value.GetHashCode();
                }
            }

            return result;
        }

        /// <summary>Creates a copy of <see cref="AuthorizationRule" />.</summary>
        /// <returns>A created copy of <see cref="AuthorizationRule" />.</returns>
        public virtual AuthorizationRule Clone()
        {
            return (AuthorizationRule)this.MemberwiseClone();
        }

        /// <summary>Determines whether the specified object is equal to the current object.</summary>
        /// <param name="obj">The object to compare with the current object.</param>
        /// <returns>true if the specified object is equal to the current object; otherwise, false.</returns>
        public override bool Equals(object obj)
        {
            if (!(this.GetType() == obj.GetType()))
            {
                return false;
            }

            var comparand = (AuthorizationRule)obj;
            if (!string.Equals(this.IssuerName, comparand.IssuerName, StringComparison.OrdinalIgnoreCase) ||
                !string.Equals(this.ClaimType, comparand.ClaimType, StringComparison.OrdinalIgnoreCase) ||
                !string.Equals(this.ClaimValue, comparand.ClaimValue, StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }

            if ((this.Rights != null && comparand.Rights == null) ||
                (this.Rights == null && comparand.Rights != null))
            {
                return false;
            }

            if (this.Rights != null && comparand.Rights != null)
            {
                var thisRights = new HashSet<AccessRights>(this.Rights);
                var comparandRights = new HashSet<AccessRights>(comparand.Rights);

                if (comparandRights.Count != thisRights.Count)
                {
                    return false;
                }

                return thisRights.All(comparandRights.Contains);
            }

            return true;
        }

        internal void MarkModified()
        {
            ModifiedTime = DateTime.UtcNow;
            Revision++;
        }

        static bool AreAccessRightsUnique(IEnumerable<AccessRights> rights)
        {
            var dedupedAccessRights = new HashSet<AccessRights>(rights);
            return rights.Count() == dedupedAccessRights.Count;
        }
    }
}