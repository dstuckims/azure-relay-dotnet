namespace Microsoft.Azure.Relay.UnitTests
{
    using System;
    using System.Collections;
    using System.Collections.Generic;
    using System.Diagnostics.CodeAnalysis;
    using System.Linq;
    using System.Net;
    using System.Net.Sockets;
    using System.Runtime.Serialization;

    /// <summary>Represents a collection of <see cref="AuthorizationRule" />.</summary>
    public class AuthorizationRules : ICollection<AuthorizationRule>
    {
        readonly ICollection<AuthorizationRule> innerCollection;
        readonly IDictionary<string, SharedAccessAuthorizationRule> nameToSharedAccessAuthorizationRuleMap;

        /// <summary>Initializes a new instance of the <see cref="AuthorizationRules" /> class.</summary>
        public AuthorizationRules()
        {
            this.nameToSharedAccessAuthorizationRuleMap = new Dictionary<string, SharedAccessAuthorizationRule>(StringComparer.OrdinalIgnoreCase);
            this.innerCollection = new List<AuthorizationRule>();
        }

        /// <summary>Initializes a new instance of the 
        /// <see cref="AuthorizationRules" /> class with a list of 
        /// <see cref="AuthorizationRule" />.</summary> 
        /// <param name="enumerable">The list of <see cref="AuthorizationRule" />.</param>
        public AuthorizationRules(IEnumerable<AuthorizationRule> enumerable)
        {
            enumerable = enumerable ?? throw new ArgumentNullException(nameof(enumerable));

            this.nameToSharedAccessAuthorizationRuleMap = new Dictionary<string, SharedAccessAuthorizationRule>(StringComparer.OrdinalIgnoreCase);
            this.innerCollection = new List<AuthorizationRule>();

            foreach (AuthorizationRule rule in enumerable)
            {
                this.Add(rule);
            }
        }

        /// <summary>Gets the enumerator that iterates through the collection.</summary>
        /// <returns>The enumerator that can be used to iterate through the collection.</returns>
        public IEnumerator<AuthorizationRule> GetEnumerator()
        {
            return this.innerCollection.GetEnumerator();
        }

        /// <summary>Gets the enumerator that iterates through the collection.</summary>
        /// <returns>The enumerator that can be used to iterate through the collection.</returns>
        IEnumerator IEnumerable.GetEnumerator()
        {
            return ((IEnumerable)this.innerCollection).GetEnumerator();
        }

        /// <summary>Adds the specified <see cref="AuthorizationRule" /> into the collection.</summary>
        /// <param name="item">The <see cref="AuthorizationRule" /> to be added.</param>
        public void Add(AuthorizationRule item)
        {
            if (item is SharedAccessAuthorizationRule sasRule)
            {
                SharedAccessAuthorizationRule existingRule;

                if (this.nameToSharedAccessAuthorizationRuleMap.TryGetValue(sasRule.KeyName, out existingRule))
                {
                    this.nameToSharedAccessAuthorizationRuleMap.Remove(sasRule.KeyName);
                    this.innerCollection.Remove(existingRule);
                }

                this.nameToSharedAccessAuthorizationRuleMap.Add(sasRule.KeyName, sasRule);
            }

            this.innerCollection.Add(item);
        }

        /// <summary>Clears all elements in the collection.</summary>
        public void Clear()
        {
            this.nameToSharedAccessAuthorizationRuleMap.Clear();
            this.innerCollection.Clear();
        }

        /// <summary>Determines whether the specified item exists in the collection.</summary>
        /// <param name="item">The item to search in the collection.</param>
        /// <returns>true if the specified item is found; otherwise, false.</returns>
        public bool Contains(AuthorizationRule item)
        {
            return this.innerCollection.Contains(item);
        }

        /// <summary>Copies the elements into an array starting at the specified array index.</summary>
        /// <param name="array">The array to hold the copied elements.</param>
        /// <param name="arrayIndex">The zero-based index at which copying starts.</param>
        public void CopyTo(AuthorizationRule[] array, int arrayIndex)
        {
            this.innerCollection.CopyTo(array, arrayIndex);
        }

        /// <summary>Removes the specified <see cref="AuthorizationRule" /> from the collection.</summary>
        /// <param name="item">The item to remove.</param>
        /// <returns>true if the operation succeeded; otherwise, false.</returns>
        public bool Remove(AuthorizationRule item)
        {
            return this.innerCollection.Remove(item);
        }

        /// <summary>Gets or sets the number of <see cref="AuthorizationRule" /> contained in the collection.</summary>
        /// <value>The number of <see cref="AuthorizationRule" /> contained in the collection.</value>
        public int Count
        {
            get { return this.innerCollection.Count; }
        }

        /// <summary>Gets or sets whether the <see cref="AuthorizationRules" /> is read only.</summary>
        /// <value>true if the <see cref="AuthorizationRules" /> is read only; otherwise, false.</value>
        public bool IsReadOnly
        {
            get { return this.innerCollection.IsReadOnly; }
        }

    }
}