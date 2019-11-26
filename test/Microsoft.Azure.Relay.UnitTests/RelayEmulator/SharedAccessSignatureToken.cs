using System;
using System.Collections.Generic;
using System.Globalization;
using System.Net;
using System.Security.Cryptography;
using System.Text;

namespace Microsoft.Azure.Relay.UnitTests
{
    class SharedAccessSignatureToken : SecurityToken
    {
        public static readonly DateTime EpochTime = new DateTime(1970, 1, 1, 0, 0, 0, 0, DateTimeKind.Utc);
        public const int MaxKeyNameLength = 256;
        public const int MaxKeyLength = 256;
        public const string SharedAccessSignature = "SharedAccessSignature";
        public const string SignedResource = "sr";
        public const string SignatureName = "sig";
        public const string SignedKeyName = "skn";
        public const string SignedExpiry = "se";
        public const string SignedResourceFullFieldName = SharedAccessSignature + " " + SignedResource;
        public const string SasKeyValueSeparator = "=";
        public const string SasPairSeparator = "&";
        readonly string keyName;
        readonly string signature;
        readonly string rawExpiry;

        public SharedAccessSignatureToken(string tokenString)
            : base(
                  tokenString,
                  audienceFieldName: SignedResourceFullFieldName,
                  expiresOnFieldName: SignedExpiry,
                  keyValueSeparator: SasKeyValueSeparator,
                  pairSeparator: SasPairSeparator)
        {
            IDictionary<string, string> parsedFields = ExtractFieldValues(tokenString);
            if (!parsedFields.TryGetValue(SignedKeyName, out this.keyName))
            {
                throw Log.ArgumentNull(SignedKeyName);
            }
            else if (!parsedFields.TryGetValue(SignatureName, out this.signature))
            {
                throw new ArgumentNullException(Signature);
            }
            else if (!parsedFields.TryGetValue(SignedExpiry, out this.rawExpiry))
            {
                throw new ArgumentNullException(SignedExpiry);
            }
        }

        public string KeyName
        {
            get => this.keyName;
        }

        public string Signature
        {
            get => this.signature;
        }

        /// <summary>
        /// Gets the expiration time of this token in wire format
        /// </summary>
        public string RawExpiry => this.rawExpiry;

        static IDictionary<string, string> ExtractFieldValues(string sharedAccessSignature)
        {
            string[] tokenLines = sharedAccessSignature.Split();

            if (!string.Equals(tokenLines[0].Trim(), SharedAccessSignature, StringComparison.OrdinalIgnoreCase) || tokenLines.Length != 2)
            {
                throw Log.ArgumentNull(nameof(sharedAccessSignature));
            }

            IDictionary<string, string> parsedFields = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            string[] tokenFields = tokenLines[1].Trim().Split(new string[] { SasPairSeparator }, StringSplitOptions.None);

            foreach (string tokenField in tokenFields)
            {
                if (tokenField != string.Empty)
                {
                    string[] fieldParts = tokenField.Split(new string[] { SasKeyValueSeparator }, StringSplitOptions.None);
                    if (string.Equals(fieldParts[0], SignedResource, StringComparison.OrdinalIgnoreCase))
                    {
                        // We need to preserve the casing of the escape characters in the audience,
                        // so defer decoding the URL until later.
                        parsedFields.Add(fieldParts[0], fieldParts[1]);
                    }
                    else
                    {
                        parsedFields.Add(fieldParts[0], WebUtility.UrlDecode(fieldParts[1]));
                    }
                }
            }

            return parsedFields;
        }

        public static string ComputeSignature(string expiresOn, string audience, string sasKey)
        {
            return ComputeSignature(expiresOn, audience, Encoding.UTF8.GetBytes(sasKey));
        }

        public static string ComputeSignature(string expiresOn, string audience, byte[] sasKey)
        {
            audience = WebUtility.UrlEncode(audience);
            var fields = new List<string> { audience, expiresOn };

            // Example string to be signed:
            // http://mynamespace.servicebus.windows.net/a/b/c?myvalue1=a
            // <Value for ExpiresOn>
            string signature = Sign(string.Join("\n", fields), sasKey);
            return signature;
        }

        static string BuildExpiresOn(DateTime expiresOn)
        {
            TimeSpan secondsFromBaseTime = expiresOn.Subtract(EpochTime);
            long seconds = Convert.ToInt64(secondsFromBaseTime.TotalSeconds, CultureInfo.InvariantCulture);
            return Convert.ToString(seconds, CultureInfo.InvariantCulture);
        }

        static string Sign(string requestString, byte[] encodedSharedAccessKey)
        {
            using (var hmac = new HMACSHA256(encodedSharedAccessKey))
            {
                return Convert.ToBase64String(hmac.ComputeHash(Encoding.UTF8.GetBytes(requestString)));
            }
        }
    }
}