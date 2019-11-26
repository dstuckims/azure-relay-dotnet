namespace Microsoft.Azure.Relay.UnitTests
{
    using System.IO;
    using System.Runtime.Serialization;
    using System.Runtime.Serialization.Json;
    using System.Text;

    enum ErrorCode
    {
        Success = 0,
        GatewayTimeout,
        InternalServerError,
        EndpointNotFound,
        TokenMissingOrInvalid,
        Forbidden
    }

    struct ErrorDetail
    {
        [IgnoreDataMember]
        static readonly DataContractJsonSerializer Serializer = new DataContractJsonSerializer(typeof(Wrapper));

        public ErrorDetail(ErrorCode code, string message)
        {
            this.Code = code;
            this.Message = message;
        }

        [DataMember(Name = "code", Order = 0, EmitDefaultValue = true)]
        public ErrorCode Code { get; set; }

        [DataMember(Name = "message", Order = 1, EmitDefaultValue = true, IsRequired = false)]
        public string Message { get; set; }

        [IgnoreDataMember]
        public bool Succeeded { get { return this.Code == ErrorCode.Success; } }

        public string ToJson()
        {
            using (var stream = new MemoryStream())
            {
                Serializer.WriteObject(stream, new Wrapper { Error = this });
                stream.Flush();
                stream.Position = 0;
                return Encoding.UTF8.GetString(stream.ToArray());
            }
        }

        [DataContract]
        struct Wrapper
        {
            [DataMember(Name = "error")]
            public ErrorDetail Error { get; set; }
        }
    }
}