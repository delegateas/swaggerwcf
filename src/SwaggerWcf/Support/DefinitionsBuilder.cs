﻿using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using System.Reflection;
using System.Runtime.Serialization;
using SwaggerWcf.Attributes;
using SwaggerWcf.Models;

namespace SwaggerWcf.Support
{
    internal sealed class DefinitionsBuilder
    {
        public static List<Definition> Process(IList<string> hiddenTags, List<Type> definitionsTypes)
        {
            if (definitionsTypes == null || !definitionsTypes.Any())
                return new List<Definition>(0);

            List<Definition> definitions = new List<Definition>();
            List<Type> processedTypes = new List<Type>();
            Stack<Type> typesStack =
                new Stack<Type>(definitionsTypes.GroupBy(t => t.FullName).Select(grp => grp.First()));

            while (typesStack.Any())
            {
                Type t = typesStack.Pop();
                if (IsHidden(t, hiddenTags) || processedTypes.Contains(t))
                    continue;

                processedTypes.Add(t);
                definitions.Add(ConvertTypeToDefinition(t, hiddenTags, typesStack));
            }

            return definitions;
        }

        private static bool IsHidden(Type type, IList<string> hiddenTags)
        {
            if (hiddenTags.Contains(type.FullName))
                return true;

            if (type.GetCustomAttribute<SwaggerWcfHiddenAttribute>() != null)
                return true;

            if (type.GetCustomAttributes<SwaggerWcfTagAttribute>().Select(t => t.TagName).Any(hiddenTags.Contains))
                return true;

            return false;
        }

        private static Definition ConvertTypeToDefinition(Type definitionType, IList<string> hiddenTags,
                                                          Stack<Type> typesStack)
        {
            DefinitionSchema schema = new DefinitionSchema
            {
                Name = definitionType.FullName
            };

            ProcessTypeAttributes(definitionType, schema);

            // process
            schema.TypeFormat = Helpers.MapSwaggerType(definitionType, null);
            if (schema.TypeFormat.Type == ParameterType.String && schema.TypeFormat.Format == "enum")
            {
                schema.Enum = new List<string>();
                List<string> listOfEnumNames = definitionType.GetEnumNames().ToList();
                foreach (string enumName in listOfEnumNames)
                {
                    schema.Enum.Add(GetEnumMemberValue(definitionType, enumName));
                }
            }
            else if (schema.TypeFormat.Type == ParameterType.Array)
            {
                Type t = GetEnumerableType(definitionType);

                if (t != null)
                {
                    schema.Ref = t.FullName;
                    typesStack.Push(t);
                }
            }
            else
            {
                ProcessProperties(definitionType, schema, hiddenTags, typesStack);
            }

            return new Definition
            {
                Schema = schema
            };
        }

        private static void ProcessTypeAttributes(Type definitionType, DefinitionSchema schema)
        {
            DescriptionAttribute descAttr = definitionType.GetCustomAttribute<DescriptionAttribute>();
            if (descAttr != null)
                schema.Description = descAttr.Description;

            SwaggerWcfDefinitionAttribute definitionAttr =
                definitionType.GetCustomAttribute<SwaggerWcfDefinitionAttribute>();
            if (definitionAttr != null)
            {
                if (!string.IsNullOrWhiteSpace(definitionAttr.ExternalDocsDescription) ||
                    !string.IsNullOrWhiteSpace(definitionAttr.ExternalDocsUrl))
                {
                    schema.ExternalDocumentation = new ExternalDocumentation
                    {
                        Description = definitionAttr.ExternalDocsDescription,
                        Url = definitionAttr.ExternalDocsUrl
                    };
                }
            }
        }

        private static void ProcessProperties(Type definitionType, DefinitionSchema schema, IList<string> hiddenTags,
                                              Stack<Type> typesStack)
        {
            PropertyInfo[] properties = definitionType.GetProperties();
            schema.Properties = new List<DefinitionProperty>();

            foreach (PropertyInfo propertyInfo in properties)
            {
                DefinitionProperty prop = ProcessProperty(propertyInfo, hiddenTags, typesStack);

                if (prop == null)
                    continue;

                if (prop.TypeFormat.Type == ParameterType.Array)
                {
                    Type propType = propertyInfo.PropertyType;

                    Type t = propType.GetElementType() ?? GetEnumerableType(propType);

                    if (t != null)
                    {
                        //prop.TypeFormat = new TypeFormat(prop.TypeFormat.Type, HttpUtility.HtmlEncode(t.FullName));
                        prop.TypeFormat = new TypeFormat(prop.TypeFormat.Type, null);

                        prop.Items.TypeFormat = new TypeFormat(ParameterType.Unknown, null);
                        prop.Items.Ref = t.FullName;
                    }
                }

                if (prop.Required)
                {
                    if (schema.Required == null)
                        schema.Required = new List<string>();

                    schema.Required.Add(prop.Title);
                }
                schema.Properties.Add(prop);
            }
        }

        public static Type GetEnumerableType(Type type)
        {
            if (type == null)
                return null;

            if (type.IsGenericType && type.GetGenericTypeDefinition() == typeof(IEnumerable<>))
                return type.GetGenericArguments()[0];

            Type iface = (from i in type.GetInterfaces()
                          where i.IsGenericType && i.GetGenericTypeDefinition() == typeof(IEnumerable<>)
                          select i).FirstOrDefault();

            return iface == null ? null : GetEnumerableType(iface);
        }

        private static DefinitionProperty ProcessProperty(PropertyInfo propertyInfo, IList<string> hiddenTags,
                                                          Stack<Type> typesStack)
        {
            if (propertyInfo.GetCustomAttribute<DataMemberAttribute>() == null
                || propertyInfo.GetCustomAttribute<SwaggerWcfHiddenAttribute>() != null
                || propertyInfo.GetCustomAttributes<SwaggerWcfTagAttribute>()
                               .Select(t => t.TagName)
                               .Any(hiddenTags.Contains))
                return null;

            TypeFormat typeFormat = Helpers.MapSwaggerType(propertyInfo.PropertyType, null);

            DefinitionProperty prop = new DefinitionProperty {Title = propertyInfo.Name};

            DataMemberAttribute dataMemberAttribute = propertyInfo.GetCustomAttribute<DataMemberAttribute>();
            if (dataMemberAttribute != null)
            {
                if (!string.IsNullOrEmpty(dataMemberAttribute.Name))
                    prop.Title = dataMemberAttribute.Name;

                prop.Required = dataMemberAttribute.IsRequired;
            }

            DescriptionAttribute descriptionAttribute = propertyInfo.GetCustomAttribute<DescriptionAttribute>();
            if (descriptionAttribute != null)
                prop.Description = descriptionAttribute.Description;

            prop.TypeFormat = typeFormat;

            if (prop.TypeFormat.Type == ParameterType.Object)
            {
                typesStack.Push(propertyInfo.PropertyType);

                prop.Ref = propertyInfo.PropertyType.FullName;

                return prop;
            }

            if (prop.TypeFormat.Type == ParameterType.Array)
            {
                Type subType = propertyInfo.PropertyType.GetEnumerableType();
                if (subType != null)
                {
                    TypeFormat subTypeFormat = Helpers.MapSwaggerType(subType, null);

                    if (subTypeFormat.Type == ParameterType.Object)
                        typesStack.Push(subType);

                    prop.Items = new ParameterItems
                    {
                        TypeFormat = subTypeFormat
                    };
                }
            }

            if (prop.TypeFormat.Type == ParameterType.String && prop.TypeFormat.Format == "enum")
            {
                prop.Enum = new List<string>();
                List<string> listOfEnumNames = propertyInfo.PropertyType.GetEnumNames().ToList();
                foreach (string enumName in listOfEnumNames)
                {
                    prop.Enum.Add(GetEnumMemberValue(propertyInfo.PropertyType, enumName));
                }
            }

            return prop;
        }

        private static string GetEnumMemberValue(Type enumType, string enumName)
        {
            return string.IsNullOrWhiteSpace(enumName) ? null : (Enum.Parse(enumType, enumName, true)).ToString();
        }
    }
}
