﻿// ------------------------------------------------------------------------------
// <copyright company="Microsoft Corporation">
//     Copyright © Microsoft Corporation. All rights reserved.
// </copyright>
// ------------------------------------------------------------------------------

namespace Microsoft.Extensions.OData.Migration.Formatters.Deserialization
{
    using System;
    using System.Collections.Generic;
    using System.Text;
    using System.Threading.Tasks;
    using Microsoft.AspNet.OData.Extensions;
    using Microsoft.AspNet.OData.Formatter;
    using Microsoft.AspNet.OData.Formatter.Deserialization;
    using Microsoft.AspNet.OData.Routing;
    using Microsoft.AspNetCore.Http;
    using Microsoft.AspNetCore.Http.Headers;
    using Microsoft.AspNetCore.Mvc.Formatters;
    using Microsoft.Extensions.DependencyInjection;
    using Microsoft.Extensions.Logging;
    using Microsoft.Net.Http.Headers;
    using Microsoft.OData;
    using Microsoft.OData.Edm;

    /// <summary>
    /// Input formatter that supports V3 conventions in request bodies.
    /// 
    /// Most of this code was copied from ODataInputFormatter in AspNetCore; the main difference is that instead of using
    /// DefaultODataDeserializerProvider, a customized ODataMigrationDeserializerProvider is used.
    /// </summary>
    public class ODataMigrationInputFormatter : ODataInputFormatter
    {
        /// <summary>
        /// Initialize the ODataMigrationInputFormatter and specify that it only accepts JSON UTF8/Unicode input
        /// </summary>
        /// <param name="payloadKinds">The types of payloads accepted by this input formatter.</param>
        public ODataMigrationInputFormatter(IEnumerable<ODataPayloadKind> payloadKinds)
            : base(payloadKinds)
        {
            SupportedMediaTypes.Add(MediaTypeHeaderValue.Parse("application/json"));
            SupportedEncodings.Add(Encoding.UTF8);
            SupportedEncodings.Add(Encoding.Unicode);
        }

        /// <summary>
        /// Determine if incoming request is specifically OData v3; if not, then use the next InputFormatter
        /// </summary>
        /// <param name="context">InputFormatterContext</param>
        /// <returns>True if the incoming request is OData v3, otherwise false.</returns>
        public override bool CanRead(InputFormatterContext context)
        {
            if (context == null)
            {
                throw new ArgumentNullException(nameof(context));
            }

            if (context.HttpContext.Request.Headers.ContainsV3Headers())
            {
                return base.CanRead(context);
            }
            else
            {
                return false;
            }
        }

        /// <summary>
        /// If the request has OData v3 headers in it, then process using V3 deserializer provider.
        /// Otherwise, process as base class.
        /// </summary>
        /// <param name="context">InputFormatter context</param>
        /// <param name="encoding">Encoding of request body</param>
        /// <returns>InputFormatterResult</returns>
        public override Task<InputFormatterResult> ReadRequestBodyAsync(InputFormatterContext context, Encoding encoding)
        {
            if (context == null)
            {
                throw new ArgumentNullException(nameof(context));
            }

            Type type = context.ModelType;
            if (type == null)
            {
                throw new ArgumentException("Model type for this request body is null", nameof(type));
            }

            HttpRequest request = context.HttpContext.Request;
            if (request == null)
            {
                throw new ArgumentNullException(nameof(request));
            }

            // If content length is 0 then return default value for this type
            RequestHeaders contentHeaders = request.GetTypedHeaders();
            object defaultValue = GetDefaultValueForType(type);
            if (contentHeaders == null || contentHeaders.ContentLength == 0)
            {
                return Task.FromResult(InputFormatterResult.Success(defaultValue));
            }

            try
            {
                Func<ODataDeserializerContext> getODataDeserializerContext = () =>
                {
                    return new ODataDeserializerContext
                    {
                        Request = request,
                    };
                };

                Action<Exception> logErrorAction = (ex) =>
                {
                    ILogger logger = context.HttpContext.RequestServices.GetService<ILogger>();
                    if (logger == null)
                    {
                        throw ex;
                    }

                    logger.LogError(ex, String.Empty);
                };


                List<IDisposable> toDispose = new List<IDisposable>();

                IServiceProvider fakeProvider = (new ServiceCollection()).BuildServiceProvider();
                ODataDeserializerProvider deserializerProvider = new ODataMigrationDeserializerProvider(fakeProvider);

                object result = ReadFromStream(
                    type,
                    defaultValue,
                    request.GetModel(),
                    GetBaseAddressInternal(request),
                    request,
                    () => ODataMigrationMessageWrapper.Create(request.Body, request.Headers, request.GetODataContentIdMapping(), request.GetRequestContainer()),
                    (objectType) => deserializerProvider.GetEdmTypeDeserializer(objectType),
                    (objectType) => deserializerProvider.GetODataDeserializer(objectType, request),
                    getODataDeserializerContext,
                    (disposable) => toDispose.Add(disposable),
                    logErrorAction);

                foreach (IDisposable obj in toDispose)
                {
                    obj.Dispose();
                }

                return Task.FromResult(InputFormatterResult.Success(result));
            }
            catch (Exception ex)
            {
                context.ModelState.AddModelError(context.ModelName, ex, context.Metadata);
                return Task.FromResult(InputFormatterResult.Failure());
            }
        }

        /// <summary>
        /// Read and deserialize incoming object from HTTP request stream.
        /// </summary>
        /// <param name="type">incoming request body object type.</param>
        /// <param name="defaultValue">default value for this type.</param>
        /// <param name="model">Edm model to reference when validating.</param>
        /// <param name="baseAddress">Base address of request.</param>
        /// <param name="internalRequest">HTTP request method that contains ODataPath.</param>
        /// <param name="getODataRequestMessage">Function to obtain customized ODataMessageWrapper.</param>
        /// <param name="getEdmTypeDeserializer">Function to obtain appropriate edm deserializer.</param>
        /// <param name="getODataPayloadDeserializer">Function to obtain appropriate deserialize for function payloads.</param>
        /// <param name="getODataDeserializerContext">Context for Deserializer.</param>
        /// <param name="registerForDisposeAction">Registration function for disposables.</param>
        /// <returns>Deserialized object.</returns>
        internal object ReadFromStream(
            Type type,
            object defaultValue,
            IEdmModel model,
            Uri baseAddress,
            HttpRequest internalRequest,
            Func<IODataRequestMessage> getODataRequestMessage,
            Func<IEdmTypeReference, ODataDeserializer> getEdmTypeDeserializer,
            Func<Type, ODataDeserializer> getODataPayloadDeserializer,
            Func<ODataDeserializerContext> getODataDeserializerContext,
            Action<IDisposable> registerForDisposeAction,
            Action<Exception> logErrorAction)
        {
            object result;

            IEdmTypeReference expectedPayloadType;
            ODataPath path = internalRequest.ODataFeature().Path;
            ODataDeserializer deserializer = GetDeserializer(type, path, model, getEdmTypeDeserializer, getODataPayloadDeserializer, out expectedPayloadType);
            if (deserializer == null)
            {
                throw new ArgumentException("type", "Formatter does not support reading type " + type.FullName);
            }

            try
            {
                ODataMessageReaderSettings oDataReaderSettings = internalRequest.GetReaderSettings();
                oDataReaderSettings.BaseUri = baseAddress;
                oDataReaderSettings.Validations = oDataReaderSettings.Validations & ~ValidationKinds.ThrowOnUndeclaredPropertyForNonOpenType;

                IODataRequestMessage oDataRequestMessage = getODataRequestMessage();
                ODataMessageReader oDataMessageReader = new ODataMessageReader(oDataRequestMessage, oDataReaderSettings, model);
                registerForDisposeAction(oDataMessageReader);

                ODataDeserializerContext readContext = getODataDeserializerContext();
                readContext.Path = path;
                readContext.Model = model;
                readContext.ResourceType = type;
                readContext.ResourceEdmType = expectedPayloadType;

                result = deserializer.Read(oDataMessageReader, type, readContext);
            }
            catch (Exception e)
            {
                logErrorAction(e);
                result = defaultValue;
            }

            return result;
        }

        // Choose deserializer for given type.  If the type is already known to be an edm type at this point,
        // use getEdmTypeDeserializer from the ODataMigrationDeserializerProvider.  If not, use getOdataPayloadDeserializer.
        private static ODataDeserializer GetDeserializer(
            Type type,
            ODataPath path,
            IEdmModel model,
            Func<IEdmTypeReference, ODataDeserializer> getEdmTypeDeserializer,
            Func<Type, ODataDeserializer> getODataPayloadDeserializer,
            out IEdmTypeReference expectedPayloadType)
        {
            expectedPayloadType = EdmExtensions.GetExpectedPayloadType(type, path, model);

            // Get the deserializer using the CLR type first from the deserializer provider.
            ODataDeserializer deserializer = getODataPayloadDeserializer(type);
            if (deserializer == null && expectedPayloadType != null)
            {
                // we are in typeless mode, get the deserializer using the edm type from the path.
                deserializer = getEdmTypeDeserializer(expectedPayloadType);
            }

            return deserializer;
        }

        // Get base address of request
        private Uri GetBaseAddressInternal(HttpRequest request)
        {
            if (BaseAddressFactory != null)
            {
                return BaseAddressFactory(request);
            }
            else
            {
                return GetDefaultBaseAddress(request);
            }
        }
    }
}
