﻿using Data8.PowerPlatform.Dataverse.Client.ADAuthHelpers;
using Microsoft.Xrm.Sdk;
using Microsoft.Xrm.Sdk.Messages;
using Microsoft.Xrm.Sdk.Query;
using NSspi.Contexts;
using System;
using System.Collections.Generic;
using System.Net;
using System.Runtime.Serialization;
using System.ServiceModel.Channels;
using System.Text;
using System.Xml;

namespace Data8.PowerPlatform.Dataverse.Client
{
    /// <summary>
    /// Inner client to set up the SOAP channel using WS-Trust with SSPI auth
    /// </summary>
    class ADAuthClient : IOrganizationService
    {
        private readonly string _url;
        private readonly string _domain;
        private readonly string _username;
        private readonly string _password;
        private readonly string _upn;
        private ClientContext _context;
        private DateTime _tokenExpires;
        private byte[] _proofToken;
        private SecurityContextToken _securityContextToken;

        /// <summary>
        /// Creates a new <see cref="ADAuthClient"/>
        /// </summary>
        /// <param name="url">The URL of the organization service</param>
        /// <param name="username">The username to authenticate as</param>
        /// <param name="password">The password to authenticate as</param>
        /// <param name="upn">The UPN the server process is running under</param>
        public ADAuthClient(string url, string username, string password, string upn)
        {
            _url = url;
            _upn = upn;
            Timeout = TimeSpan.FromSeconds(30);

            if (!String.IsNullOrEmpty(username))
            {
                // Split username into domain + username
                var domain = "";
                var parts = username.Split('\\');

                if (parts.Length == 2)
                {
                    domain = parts[0];
                    username = parts[1];
                }
                else if (parts.Length == 1)
                {
                    parts = username.Split('@');

                    if (parts.Length == 2)
                    {
                        domain = parts[1];
                        username = parts[0];
                    }
                }

                _domain = domain;
                _username = username;
                _password = password;
            }
        }

        /// <summary>
        /// Returns or sets the timeout for executing requests
        /// </summary>
        public TimeSpan Timeout { get; set; }

        /// <summary>
        /// Returns or sets the SDK version that will be reported to the server
        /// </summary>
        public string SdkClientVersion { get; set; }

        /// <summary>
        /// Returns or sets the impersonated user ID
        /// </summary>
        public Guid CallerId { get; set; }

        /// <summary>
        /// Authenticates with the server
        /// </summary>
        private void Authenticate()
        {
            if (_tokenExpires > DateTime.UtcNow.AddSeconds(10))
                return;

            // Set up the SSPI context
            NSspi.Credentials.Credential cred;

            if (String.IsNullOrEmpty(_username))
                cred = new NSspi.Credentials.CurrentCredential(NSspi.PackageNames.Negotiate, NSspi.Credentials.CredentialUse.Outbound);
            else
                cred = new NSspi.Credentials.PasswordCredential(_domain, _username, _password, NSspi.PackageNames.Negotiate, NSspi.Credentials.CredentialUse.Outbound);

            _context = new ClientContext(cred, _upn, ContextAttrib.ReplayDetect | ContextAttrib.SequenceDetect | ContextAttrib.Confidentiality | ContextAttrib.InitIdentify);
            var state = _context.Init(null, out var token);

            if (state != NSspi.SecurityStatus.ContinueNeeded)
                throw new ApplicationException("Error authenticating with the server: " + state);

            // Keep a hash of all the RSTs and RSTRs that have been sent so we can validate the authenticator
            // at the end.
            var auth = new Authenticator();

            var rst = new RequestSecurityToken(token);
            var resp = rst.Execute(_url, auth);

            var finalResponse = resp as RequestSecurityTokenResponseCollection;

            // Keep exchanging tokens until we get a full RSTR
            while (finalResponse == null)
            {
                if (resp is RequestSecurityTokenResponse r)
                {
                    state = _context.Init(r.BinaryExchange.Token, out token);

                    if (state != NSspi.SecurityStatus.OK && state != NSspi.SecurityStatus.ContinueNeeded)
                        throw new ApplicationException("Error authenticating with the server: " + state);

                    resp = new RequestSecurityTokenResponse(r.Context, token).Execute(_url, auth);
                    finalResponse = resp as RequestSecurityTokenResponseCollection;
                }
            }

            if (state != NSspi.SecurityStatus.OK)
                state = _context.Init(finalResponse.Responses[0].BinaryExchange.Token, out _);

            if (state != NSspi.SecurityStatus.OK)
                throw new ApplicationException("Error authenticating with the server: " + state);

            var wrappedToken = finalResponse.Responses[0].RequestedProofToken.CipherValue;
            _tokenExpires = finalResponse.Responses[0].Lifetime.Expires;
            _proofToken = _context.Decrypt(wrappedToken, true);
            _securityContextToken = finalResponse.Responses[0].RequestedSecurityToken;

            // Check the authenticator is valid
            auth.Validate(_proofToken, finalResponse.Responses[1].Authenticator.Token);
        }

        /// <inheritdoc/>
        public void Associate(string entityName, Guid entityId, Relationship relationship, EntityReferenceCollection relatedEntities)
        {
            Execute(new AssociateRequest
            {
                Target = new EntityReference(entityName, entityId),
                Relationship = relationship,
                RelatedEntities = relatedEntities
            });
        }

        /// <inheritdoc/>
        public Guid Create(Entity entity)
        {
            var resp = (CreateResponse) Execute(new CreateRequest { Target = entity });
            return resp.id;
        }

        /// <inheritdoc/>
        public void Delete(string entityName, Guid id)
        {
            Execute(new DeleteRequest { Target = new EntityReference(entityName, id) });
        }

        /// <inheritdoc/>
        public void Disassociate(string entityName, Guid entityId, Relationship relationship, EntityReferenceCollection relatedEntities)
        {
            Execute(new DisassociateRequest
            {
                Target = new EntityReference(entityName, entityId),
                Relationship = relationship,
                RelatedEntities = relatedEntities
            });
        }

        /// <inheritdoc/>
        public OrganizationResponse Execute(OrganizationRequest request)
        {
            Authenticate();

            var message = Message.CreateMessage(MessageVersion.Soap12WSAddressing10, "http://schemas.microsoft.com/xrm/2011/Contracts/Services/IOrganizationService/Execute", new ExecuteRequestWriter(request));
            message.Headers.MessageId = new UniqueId(Guid.NewGuid());
            message.Headers.ReplyTo = new System.ServiceModel.EndpointAddress("http://www.w3.org/2005/08/addressing/anonymous");
            message.Headers.To = new Uri(_url);
            message.Headers.Add(MessageHeader.CreateHeader("SdkClientVersion", Namespaces.Xrm2011Contracts, SdkClientVersion));
            message.Headers.Add(MessageHeader.CreateHeader("UserType", Namespaces.Xrm2011Contracts, "CrmUser"));
            message.Headers.Add(new SecurityHeader(_securityContextToken, _proofToken));

            if (CallerId != Guid.Empty)
                message.Headers.Add(MessageHeader.CreateHeader("CallerId", Namespaces.Xrm2011Contracts, CallerId));

            var req = WebRequest.CreateHttp(_url);
            req.Method = "POST";
            req.ContentType = "application/soap+xml; charset=utf-8";
            req.Timeout = (int) Timeout.TotalMilliseconds;

            using (var reqStream = req.GetRequestStream())
            using (var xmlTextWriter = XmlWriter.Create(reqStream, new XmlWriterSettings
            {
                OmitXmlDeclaration = true,
                Indent = false,
                Encoding = new UTF8Encoding(false),
                CloseOutput = true
            }))
            using (var xmlWriter = XmlDictionaryWriter.CreateDictionaryWriter(xmlTextWriter))
            {
                message.WriteMessage(xmlWriter);
                xmlWriter.WriteEndDocument();
                xmlWriter.Flush();
            }

            try
            {
                using (var resp = req.GetResponse())
                using (var respStream = resp.GetResponseStream())
                {
                    var reader = XmlReader.Create(respStream, new XmlReaderSettings());
                    var responseMessage = Message.CreateMessage(reader, 0x10000, MessageVersion.Soap12WSAddressing10);
                    var action = responseMessage.Headers.Action;

                    using (var bodyReader = responseMessage.GetReaderAtBodyContents())
                    {
                        bodyReader.ReadStartElement("ExecuteResponse", Namespaces.Xrm2011Services);

                        var serializer = new DataContractSerializer(typeof(OrganizationResponse), "ExecuteResult", Namespaces.Xrm2011Services);
                        var response = (OrganizationResponse) serializer.ReadObject(bodyReader, true, new KnownTypesResolver());

                        bodyReader.ReadEndElement(); // ExecuteRepsonse

                        return response;
                    }
                }
            }
            catch (WebException ex)
            {
                using (var errorStream = ex.Response.GetResponseStream())
                {
                    var reader = XmlReader.Create(errorStream, new XmlReaderSettings());
                    var responseMessage = Message.CreateMessage(reader, 0x10000, MessageVersion.Soap12WSAddressing10);
                    var responseAction = responseMessage.Headers.Action;

                    using (var bodyReader = responseMessage.GetReaderAtBodyContents())
                    {
                        if (bodyReader.LocalName == "Fault" && bodyReader.NamespaceURI == Namespaces.Soap)
                            throw FaultReader.ReadFault(bodyReader, responseAction);

                        throw;
                    }
                }
            }
        }

        /// <inheritdoc/>
        public Entity Retrieve(string entityName, Guid id, ColumnSet columnSet)
        {
            var resp = (RetrieveResponse) Execute(new RetrieveRequest { Target = new EntityReference(entityName, id), ColumnSet = columnSet });
            return resp.Entity;
        }

        /// <inheritdoc/>
        public EntityCollection RetrieveMultiple(QueryBase query)
        {
            var resp = (RetrieveMultipleResponse)Execute(new RetrieveMultipleRequest { Query = query });
            return resp.EntityCollection;
        }

        /// <inheritdoc/>
        public void Update(Entity entity)
        {
            Execute(new UpdateRequest { Target = entity });
        }

        private class ExecuteRequestWriter : BodyWriter
        {
            private readonly OrganizationRequest _request;

            public ExecuteRequestWriter(OrganizationRequest request) : base(isBuffered: true)
            {
                _request = request;
            }

            protected override void OnWriteBodyContents(XmlDictionaryWriter writer)
            {
                writer.WriteStartElement("Execute", Namespaces.Xrm2011Services);

                var serializer = new DataContractSerializer(typeof(OrganizationRequest), "request", Namespaces.Xrm2011Services);
                serializer.WriteObject(writer, _request, new KnownTypesResolver());

                writer.WriteEndElement(); // Execute
            }
        }
    }
}
