﻿using StagWare.FanControl.Configurations;
using System;
using System.IO;
using System.Reflection;
using System.ServiceModel;
using System.Linq;
using StagWare.FanControl.Service.Settings;
using System.Collections.Generic;

namespace StagWare.FanControl.Service
{
    [ServiceBehavior(InstanceContextMode = InstanceContextMode.Single, ConcurrencyMode = ConcurrencyMode.Single)]
    public class FanControlService : IFanControlService, IDisposable
    {
        #region Constants

        private const string ConfigsDirectoryName = "Configs";
        private const int AutoControlFanSpeedPercentage = 101;

        #endregion

        #region Private Fields

        private FanControl fanControl;
        private bool initialized;
        private string selectedConfig;
        private int[] fanSpeedSteps;
        private string executingAssemblyDirName;

        #endregion

        #region Constructors

        public FanControlService()
        {
            executingAssemblyDirName = Assembly.GetExecutingAssembly().Location;
            executingAssemblyDirName = Path.GetDirectoryName(executingAssemblyDirName);

            using (var settings = ServiceSettings.Load())
            {
                initialized = TryInitializeFanControl(settings);
            }
        }

        #endregion

        #region IFanControlService implementation

        public void SetTargetFanSpeed(double value, int fanIndex)
        {
            if (fanControl != null)
            {
                fanControl.SetTargetFanSpeed(value, fanIndex);
            }
        }

        public FanControlInfo GetFanControlInfo()
        {
            var info = new FanControlInfo()
            {
                IsInitialized = initialized,
                SelectedConfig = selectedConfig,
            };

            if (fanControl != null)
            {
                info.CpuTemperature = fanControl.CpuTemperature;

                IList<FanInformation> fanInfo = this.fanControl.FanInformation;
                info.FanStatus = new FanStatus[fanInfo.Count];

                for (int i = 0; i < fanInfo.Count; i++)
                {
                    info.FanStatus[i] = new FanStatus()
                    {
                        AutoControlEnabled = fanInfo[i].AutoFanControlEnabled,
                        CriticalModeEnabled = fanInfo[i].CriticalModeEnabled,
                        CurrentFanSpeed = fanInfo[i].CurrentFanSpeed,
                        TargetFanSpeed = fanInfo[i].TargetFanSpeed,
                        FanDisplayName = fanInfo[i].FanDisplayName,
                        FanSpeedSteps = this.fanSpeedSteps[i]
                    };
                }
            }

            return info;
        }

        public bool Restart()
        {
            using (var settings = ServiceSettings.Load())
            {
                return Restart(settings);
            }
        }

        public void Stop()
        {
            if (this.initialized)
            {
                initialized = false;
                Dispose();

                using (var settings = ServiceSettings.Load())
                {
                    settings.AutoStart = false;
                    settings.Save();
                }
            }
        }

        public bool Start()
        {
            if (!this.initialized)
            {
                using (var settings = ServiceSettings.Load())
                {
                    this.initialized = TryInitializeFanControl(settings);

                    settings.AutoStart = true;
                    settings.Save();
                }
            }

            return this.initialized;
        }

        public void SetConfig(string configUniqueId)
        {
            using (var settings = ServiceSettings.Load())
            {
                settings.SelectedConfigId = configUniqueId;
                settings.Save();

                Restart(settings);
            }
        }

        #endregion

        #region IDisposable implementation

        public void Dispose()
        {
            if (fanControl != null)
            {
                try
                {
                    using (var settings = ServiceSettings.Load())
                    {
                        settings.AutoStart = this.initialized;
                        settings.TargetFanSpeeds = fanControl.FanInformation
                            .Select(x => x.AutoFanControlEnabled ? AutoControlFanSpeedPercentage : x.TargetFanSpeed).ToArray();

                        settings.Save();
                    }
                }
                catch
                {
                }

                fanControl.Dispose();
                fanControl = null;
            }
        }

        #endregion

        #region Private Methods

        private bool TryInitializeFanControl(ServiceSettings settings)
        {
            bool success = false;

            try
            {
                string path = Path.Combine(executingAssemblyDirName, ConfigsDirectoryName);
                var configManager = new FanControlConfigManager(path);

                if (!string.IsNullOrWhiteSpace(settings.SelectedConfigId))
                {
                    if (!configManager.SelectConfig(settings.SelectedConfigId))
                    {
                        return false;
                    }
                    else
                    {
                        var config = configManager.SelectedConfig;
                        selectedConfig = configManager.SelectedConfigName;
                        fanSpeedSteps = new int[config.FanConfigurations.Count];

                        for (int i = 0; i < fanSpeedSteps.Length; i++)
                        {
                            var fanConfig = config.FanConfigurations[i];

                            // Add 1 extra step for "auto control"
                            fanSpeedSteps[i] = 1 + (Math.Max(fanConfig.MinSpeedValue, fanConfig.MaxSpeedValue)
                                - Math.Min(fanConfig.MinSpeedValue, fanConfig.MaxSpeedValue));
                        }
                    }
                }

                if (configManager.SelectedConfig == null)
                {
                    return false;
                }
                else
                {
                    fanControl = new FanControl(configManager.SelectedConfig);

                    for (int i = 0; i < fanControl.FanInformation.Count; i++)
                    {
                        if (settings.TargetFanSpeeds == null || i >= settings.TargetFanSpeeds.Length)
                        {
                            fanControl.SetTargetFanSpeed(101, i);
                        }
                        else
                        {
                            fanControl.SetTargetFanSpeed(settings.TargetFanSpeeds[i], i);
                        }
                    }

                    fanControl.Start();
                    success = true;
                }
            }
            catch
            { 
            }
            finally
            {
                if (!success && this.fanControl != null)
                {
                    this.fanControl.Dispose();
                    this.fanControl = null;
                }
            }

            return success;
        }

        private bool Restart(ServiceSettings settings)
        {
            this.initialized = false;
            Dispose();
            this.initialized = TryInitializeFanControl(settings);

            return this.initialized;
        }

        #endregion
    }
}