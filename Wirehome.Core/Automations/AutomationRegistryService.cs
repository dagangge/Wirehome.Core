﻿using System;
using System.Collections.Generic;
using Microsoft.Extensions.Logging;
using Wirehome.Core.Automations.Configuration;
using Wirehome.Core.Automations.Exceptions;
using Wirehome.Core.MessageBus;
using Wirehome.Core.Model;
using Wirehome.Core.Python;
using Wirehome.Core.Python.Proxies;
using Wirehome.Core.Repository;
using Wirehome.Core.Storage;

namespace Wirehome.Core.Automations
{
    public class AutomationRegistryService
    {
        private readonly Dictionary<string, Automation> _automations = new Dictionary<string, Automation>();

        private readonly RepositoryService _repositoryService;
        private readonly PythonEngineService _pythonEngineService;
        private readonly StorageService _storageService;
        private readonly MessageBusService _messageBusService;
        private readonly ILogger _logger;

        public AutomationRegistryService(
            RepositoryService repositoryService,
            PythonEngineService pythonEngineService,
            StorageService storageService,
            MessageBusService messageBusService,
            ILoggerFactory loggerFactory)
        {
            _repositoryService = repositoryService ?? throw new ArgumentNullException(nameof(repositoryService));
            _pythonEngineService = pythonEngineService ?? throw new ArgumentNullException(nameof(pythonEngineService));
            _storageService = storageService ?? throw new ArgumentNullException(nameof(storageService));
            _messageBusService = messageBusService ?? throw new ArgumentNullException(nameof(messageBusService));

            if (loggerFactory == null) throw new ArgumentNullException(nameof(loggerFactory));
            _logger = loggerFactory.CreateLogger<AutomationRegistryService>();
        }

        public void Start()
        {
            var automationDirectories = _storageService.EnumeratureDirectories("*", "Automations");
            foreach (var automationUid in automationDirectories)
            {
                TryInitializeAutomation(automationUid);
            }
        }

        public AutomationConfiguration ReadAutomationConfiguration(string uid)
        {
            if (uid == null) throw new ArgumentNullException(nameof(uid));

            if (!_storageService.TryRead(out AutomationConfiguration configuration, "Automations", uid, DefaultFilenames.Configuration))
            {
                throw new AutomationNotFoundException(uid);
            }

            return configuration;
        }

        public void WriteAutomationConfiguration(string uid, AutomationConfiguration configuration)
        {
            if (uid == null) throw new ArgumentNullException(nameof(uid));
            if (configuration == null) throw new ArgumentNullException(nameof(configuration));

            _storageService.Write(configuration, "Automations", uid, DefaultFilenames.Configuration);
        }

        public List<Automation> GetAutomations()
        {
            lock (_automations)
            {
                return new List<Automation>(_automations.Values);
            }
        }

        public Automation GetAutomation(string uid)
        {
            if (uid == null) throw new ArgumentNullException(nameof(uid));

            lock (_automations)
            {
                if (!_automations.TryGetValue(uid, out var automation))
                {
                    throw new AutomationNotFoundException(uid);
                }

                return automation;
            }
        }

        public void ActivateAutomation(string uid)
        {
            if (uid == null) throw new ArgumentNullException(nameof(uid));

            GetAutomation(uid).Activate();
        }

        public void DeactivateAutomation(string uid)
        {
            if (uid == null) throw new ArgumentNullException(nameof(uid));

            GetAutomation(uid).Deactivate();
        }

        public object GetAutomationSetting(string automationUid, string settingUid, object defaultValue = null)
        {
            if (automationUid == null) throw new ArgumentNullException(nameof(automationUid));
            if (settingUid == null) throw new ArgumentNullException(nameof(settingUid));

            var automation = GetAutomation(automationUid);
            return automation.Settings.GetValueOrDefault(settingUid, defaultValue);
        }

        public void SetAutomationSetting(string automationUid, string settingUid, object value)
        {
            if (automationUid == null) throw new ArgumentNullException(nameof(automationUid));
            if (settingUid == null) throw new ArgumentNullException(nameof(settingUid));

            var automation = GetAutomation(automationUid);
            automation.Settings.TryGetValue(settingUid, out var oldValue);

            if (Equals(oldValue, value))
            {
                return;
            }

            automation.Settings[settingUid] = value;

            _storageService.Write(automation.Settings, "Automations", automation.Uid, DefaultFilenames.Settings);
            _messageBusService.Publish(new WirehomeDictionary
            {
                ["type"] = "automation_registry.event.setting_changed",
                ["automation_uid"] = automationUid,
                ["setting_uid"] = settingUid,
                ["old_value"] = oldValue,
                ["new_value"] = value,
                ["timestamp"] = DateTimeOffset.UtcNow
            });
        }

        private void TryInitializeAutomation(string uid)
        {
            try
            {
                if (!_storageService.TryRead(out AutomationConfiguration configuration, "Automations", uid, DefaultFilenames.Configuration))
                {
                    return;
                }

                if (!configuration.IsEnabled)
                {
                    _logger.LogInformation($"Automation '{uid}' not initialized because it is disabled.");
                    return;
                }

                if (!_storageService.TryRead(out WirehomeDictionary settings, "Automations", uid, DefaultFilenames.Settings))
                {
                    settings = new WirehomeDictionary();
                }

                _logger.LogInformation($"Initializing automation '{uid}'.");
                var automation = CreateAutomation(uid, configuration, settings);
                automation.Initialize();
                _logger.LogInformation($"Automation '{uid}' initialized.");

                lock (_automations)
                {
                    if (_automations.TryGetValue(uid, out var existingAutomation))
                    {
                        _logger.LogInformation($"Deactivating automation '{uid}'.");
                        existingAutomation.Deactivate();
                        _logger.LogInformation($"Automation '{uid}' deactivated.");
                    }

                    _automations[uid] = automation;

                    _logger.LogInformation($"Activating automation '{uid}'.");
                    automation.Activate();
                    _logger.LogInformation($"Automation '{uid}' activated.");
                }
            }
            catch (Exception exception)
            {
                _logger.LogError(exception, $"Error while initializing automation '{uid}'.");
            }
        }

        private Automation CreateAutomation(string uid, AutomationConfiguration configuration, WirehomeDictionary settings)
        {
            var repositoryEntitySource = _repositoryService.LoadEntity(configuration.Logic.Uid);
            var scriptHost = _pythonEngineService.CreateScriptHost(_logger, new AutomationPythonProxy(uid, this));

            scriptHost.Initialize(repositoryEntitySource.Script);

            var context = new WirehomeDictionary
            {
                ["automation_uid"] = uid,
                ["logic_id"] = configuration.Logic.Uid.Id,
                ["logic_version"] = configuration.Logic.Uid.Version
            };

            // TODO: Remove scope as soon as all automations are migrated.
            scriptHost.SetVariable("scope", context);
            scriptHost.SetVariable("context", context);

            foreach (var variable in configuration.Logic.Variables)
            {
                scriptHost.SetVariable(variable.Key, variable.Value);
            }

            var automation = new Automation(uid, scriptHost);
            foreach (var setting in settings)
            {
                automation.Settings[setting.Key] = setting.Value;
            }

            return automation;
        }
    }
}
