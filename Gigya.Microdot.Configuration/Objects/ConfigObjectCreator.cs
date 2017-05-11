﻿using System;
using System.Collections.Generic;
using System.ComponentModel.DataAnnotations;
using System.Linq;
using System.Linq.Expressions;
using System.Reflection;
using System.Threading.Tasks.Dataflow;
using Gigya.Common.Contracts.Exceptions;
using Gigya.Microdot.Interfaces.Configuration;
using Gigya.Microdot.Interfaces.Logging;
using Gigya.Microdot.SharedLogic.Exceptions;
using Gigya.Microdot.SharedLogic.Monitor;

using Metrics;

using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace Gigya.Microdot.Configuration.Objects
{
    public class ConfigObjectCreator
    {

        /// <summary>
        /// Gets an ISourceBlock the provides notifications of config changes. This value is cached for quick retrieval.
        /// </summary>
        public object ChangeNotifications { get; private set; }

        private object Latest { get; set; }
        private UsageTracking UsageTracking { get; }
        private ILog Log { get; }
        private Type ObjectType { get; }
        private ConfigCache ConfigCache { get; }
        private string ConfigPath { get; }
        private Action<object> SendChangeNotification { get; set; }
        private string ValidationErrors { get; set; }
        private JObject LatestNode { get; set; }
        private JObject Empty { get; } = new JObject();
        private DataAnnotationsValidator.DataAnnotationsValidator Validator { get; }

        public ConfigObjectCreator(Type objectType, ConfigCache configCache, UsageTracking usageTracking, ILog log, IHealthMonitor healthMonitor)
        {
            UsageTracking = usageTracking;
            Log = log;
            ObjectType = objectType;
            ConfigCache = configCache;
            ConfigPath = GetConfigPath();
            Validator = new DataAnnotationsValidator.DataAnnotationsValidator();

            Create();
            ConfigCache.ConfigChanged.LinkTo(new ActionBlock<ConfigItemsCollection>(c => Create()));
            InitializeBroadcast();

            healthMonitor.SetHealthFunction($"{ObjectType.Name} Configuration", HealthCheck);
        }


        private HealthCheckResult HealthCheck()
        {
            if (ValidationErrors != null)
            {
                return HealthCheckResult.Unhealthy("The config object failed validation.\r\n" +
                                                   $"ConfigObjectType={ObjectType.FullName}\r\n" +
                                                   $"ConfigObjectPath={ConfigPath}\r\n" +
                                                   $"ValidationErrors={ValidationErrors}");
            }

            return HealthCheckResult.Healthy();
        }


        /// <summary>
        /// Gets the latest version of the configuration. This value is cached for quick retrieval. If the config object
        /// fails validation, an exception will be thrown.
        /// </summary>
        /// <exception cref="ConfigurationException">When the configuration fails validation, this exception will be
        /// thrown, with details regarding that has failed.</exception>
        public object GetLatest()
        {
            if (Latest == null)
            {
                throw new ConfigurationException("The config object failed validation.", unencrypted: new Tags
                {
                    { "ConfigObjectType", ObjectType.FullName },
                    { "ConfigObjectPath", ConfigPath },
                    { "ValidationErrors", ValidationErrors }
                });
            }

            return Latest;
        }


        private string GetConfigPath()
        {
            var configPath = ObjectType.Name;

            var rootAttribute = ObjectType.GetCustomAttribute<ConfigurationRootAttribute>();
            if (rootAttribute != null)
            {
                configPath = rootAttribute.Path;
                if (rootAttribute.BuildingStrategy == RootStrategy.AppendClassNameToPath)
                {
                    configPath = configPath + "." + ObjectType.Name;
                }
            }

            return configPath;
        }


        private void InitializeBroadcast()
        {
            var broadcastBlockType = typeof(BroadcastBlock<>).MakeGenericType(ObjectType);

            ChangeNotifications = Activator.CreateInstance(broadcastBlockType, new object[] { null });

            var broadcastBlockConst = Expression.Constant(ChangeNotifications);
            var convertedBlock = Expression.Convert(broadcastBlockConst, broadcastBlockType);

            var configParam = Expression.Parameter(typeof(object), "updatedConfig");
            var convertedConfig = Expression.Convert(configParam, ObjectType);

            var postMethod = typeof(DataflowBlock).GetMethod("Post").MakeGenericMethod(ObjectType);
            var postCall = Expression.Call(postMethod, convertedBlock, convertedConfig);
            var lambda = Expression.Lambda<Action<object>>(postCall, configParam);

            SendChangeNotification = lambda.Compile();
        }


        private void Create()
        {
            var errors = new List<ValidationResult>();
            JObject config = null;
            object updatedConfig = null;

            try
            {
                config = ConfigCache.CreateJsonConfig(ConfigPath) ?? Empty;

                if (JToken.DeepEquals(LatestNode, config))
                    return;
            }
            catch (Exception ex)
            {
                errors.Add(new ValidationResult("Failed to acquire config JObject: " + HealthMonitor.GetMessages(ex)));
            }

            if (config != null && errors.Any() == false)
            {
                LatestNode = config;

                try
                {
                    updatedConfig = LatestNode.ToObject(ObjectType);
                }
                catch (JsonException ex)
                {
                    errors.Add(new ValidationResult("Failed to deserialize config object: " + HealthMonitor.GetMessages(ex)));
                }

                if (updatedConfig != null)
                    Validator.TryValidateObjectRecursive(updatedConfig, errors);
            }

            if (errors.Any() == false)
            {
                Latest = updatedConfig;
                ValidationErrors = null;
                UsageTracking.AddConfigObject(Latest, ConfigPath);

                Log.Info(_ => _("A config object has been updated", unencryptedTags: new
                {
                    ConfigObjectType = ObjectType.FullName,
                    ConfigObjectPath = ConfigPath
                }));

                SendChangeNotification?.Invoke(Latest);
            }
            else
            {
                ValidationErrors = string.Join(" \n", errors.Select(a => a.ErrorMessage));

                Log.Error(_ => _("A config object has been updated but failed validation", unencryptedTags: new
                {
                    ConfigObjectType = ObjectType.FullName,
                    ConfigObjectPath = ConfigPath,
                    ValidationErrors
                }));
            }
        }
    }
}