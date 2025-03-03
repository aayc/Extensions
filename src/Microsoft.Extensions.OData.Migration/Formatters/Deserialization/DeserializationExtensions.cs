﻿// ------------------------------------------------------------------------------
// <copyright company="Microsoft Corporation">
//     Copyright © Microsoft Corporation. All rights reserved.
// </copyright>
// ------------------------------------------------------------------------------

namespace Microsoft.Extensions.OData.Migration.Formatters.Deserialization
{
    using System;
    using System.Collections.Generic;
    using System.IO;
    using System.Linq;
    using System.Reflection;
    using Microsoft.OData;
    using Microsoft.OData.Edm;
    using Newtonsoft.Json.Linq;

    internal static class DeserializationExtensions
    {
        /// <summary>
        /// Replace the inner HTTP request stream with substituteStream using reflection.
        /// 
        /// The stream needs to be substituted because the request body needs to be translated before passed on to the base deserialization classes
        /// to take advantage of OData V4 model validation.  Unfortunately, although it is guaranteed to exist, the stream is marked private
        /// in the ODataMessageReader, so reflection must be used to modify it.
        /// </summary>
        /// <param name="reader">ODataMessageReader which has not read yet.</param>
        /// <param name="substituteStream">Replacement stream.</param>
        public static void SubstituteRequestStream(this ODataMessageReader reader, Stream substituteStream)
        {
            FieldInfo messageField = reader.GetType().GetField("message", BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.FlattenHierarchy);
            object message = messageField.GetValue(reader);
            FieldInfo requestMessageField = message.GetType().GetField("requestMessage", BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.FlattenHierarchy);
            object requestMessage = requestMessageField.GetValue(message);
            FieldInfo streamField = requestMessage.GetType().GetField("_stream", BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.FlattenHierarchy);
            streamField.SetValue(requestMessage, substituteStream);
        }

        /// <summary>
        /// Walk the JSON body and format top level instance annotations (more complex annotations are unsupported)
        /// and change types that would be deserialized incorrectly by OData V4 formatters to types that will be deserialized correctly.
        /// </summary>
        /// <param name="node">JToken root or child of JSON request body</param>
        /// <param name="edmType">Corresponding edm type</param>
        public static void WalkTranslate(this JToken node, IEdmTypeReference edmType)
        {
            if (node == null)
            {
                return;
            }

            if (node.Type == JTokenType.Object)
            {
                JObject obj = (JObject)node;
                IEdmStructuredTypeReference structuredType = edmType.AsStructured();
                foreach (JProperty child in node.Children<JProperty>().ToList())
                {
                    IEdmProperty property = structuredType.FindProperty(child.Name);

                    // Convert instance annotations to V3 format
                    if (child.Name == "odata.type")
                    {
                        obj["@odata.type"] = "#" + obj["odata.type"];
                        obj.Remove("odata.type");
                    }
                    else if (child.Name.Contains("@odata"))
                    {
                        obj[child.Name] = "#" + obj[child.Name];
                    }
                    else if (property != null &&
                        property.Type.TypeKind() == EdmTypeKind.Primitive &&
                        ((IEdmPrimitiveType)property.Type.Definition).PrimitiveKind == EdmPrimitiveTypeKind.Int64)
                    {
                        // Convert long type to unquoted when deserializing
                        obj[child.Name] = Convert.ToInt64(obj[child.Name]);
                    }
                    else if (property != null)
                    {
                        // If type is not IEdmStructuredTypeReference or IEdmCollectionTypeReference, then won't need to convert.
                        if (property.Type.TypeKind() == EdmTypeKind.Collection)
                        {
                            // Translate collection types
                            WalkTranslate(child.Value, property.Type as IEdmCollectionTypeReference);
                        }
                        else
                        {
                            // Continue to translate deeper nested entities
                            WalkTranslate(child.Value, property.Type as IEdmStructuredTypeReference);
                        }
                    }
                }
            }
            else if (node.Type == JTokenType.Array)
            {
                JArray items = (JArray)node;
                IEdmCollectionTypeReference collectionType = (IEdmCollectionTypeReference)edmType;
                IEdmTypeReference elementType = collectionType.Definition.AsElementType().ToEdmTypeReference();

                if (elementType != null)
                {
                    for (int i = 0; i < items.Count; i++)
                    {
                        if (elementType.IsComplex() || elementType.IsEntity() || elementType.IsCollection())
                        {
                            // Continue to translate deeper nested entities
                            items[i].WalkTranslate(elementType);
                        }
                        else
                        {
                            // Do translation of V3 formatted types to V4 formatted types at the collection level
                            if (items[i].Type == JTokenType.String && elementType.IsInt64())
                            {
                                items[i] = new JValue(Convert.ToInt64(items[i].ToString()));
                            }
                        }
                    }
                }
            }
        }
    }
}
