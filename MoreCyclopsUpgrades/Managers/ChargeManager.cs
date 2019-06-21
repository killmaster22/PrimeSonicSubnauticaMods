﻿namespace MoreCyclopsUpgrades.Managers
{
    using System.Collections.Generic;
    using Common;
    using MoreCyclopsUpgrades.API;
    using MoreCyclopsUpgrades.API.Charging;
    using MoreCyclopsUpgrades.API.General;
    using MoreCyclopsUpgrades.Config;
    using UnityEngine;

    internal class ChargeManager : IAuxCyclopsManager
    {
        internal static bool Initialized { get; private set; }
        internal const string ManagerName = "McuChargeMgr";
        internal const float BatteryDrainRate = 0.01f;
        internal const float MinimalPowerValue = MCUServices.MinimalPowerValue;
        internal const float Mk2ChargeRateModifier = 1.10f; // The MK2 charging modules get a 15% bonus to their charge rate.

        private static readonly ICollection<CreateCyclopsCharger> CyclopsChargers = new List<CreateCyclopsCharger>();

        /// <summary>
        /// Registers a <see cref="CreateCyclopsCharger"/> method that creates returns a new <see cref="ICyclopsCharger"/> on demand and is only used once.
        /// </summary>
        /// <param name="createEvent">A method that takes no parameters a returns a new instance of an <see cref="CreateCyclopsCharger"/>.</param>
        internal static void RegisterChargerCreator(CreateCyclopsCharger createEvent, string assemblyName)
        {
            if (CyclopsChargers.Contains(createEvent))
            {
                QuickLogger.Warning($"Duplicate ChargerCreator blocked from {assemblyName}");
                return;
            }

            QuickLogger.Info($"Received ChargerCreator from {assemblyName}");
            CyclopsChargers.Add(createEvent);
        }

        internal readonly SubRoot Cyclops;

        private CyclopsHUDManager cyclopsHUDManager;
        private CyclopsHUDManager HUDManager => cyclopsHUDManager ?? (cyclopsHUDManager = MCUServices.Find.AuxCyclopsManager<CyclopsHUDManager>(Cyclops, CyclopsHUDManager.ManagerName));

        internal int PowerChargersCount => RenewablePowerChargers.Count + NonRenewablePowerChargers.Count;
        internal IEnumerable<ICyclopsCharger> PowerChargers
        {
            get
            {
                foreach (ICyclopsCharger charger in RenewablePowerChargers)
                    yield return charger;

                foreach (ICyclopsCharger charger in NonRenewablePowerChargers)
                    yield return charger;
            }
        }

        public string Name { get; } = ManagerName;

        private readonly IDictionary<string, ICyclopsCharger> KnownChargers = new Dictionary<string, ICyclopsCharger>();
        private readonly ICollection<ICyclopsCharger> RenewablePowerChargers = new List<ICyclopsCharger>();
        private readonly ICollection<ICyclopsCharger> NonRenewablePowerChargers = new List<ICyclopsCharger>();
        private readonly float rechargePenalty;

        internal T GetCharger<T>(string chargeHandlerName) where T : class, ICyclopsCharger
        {
            if (KnownChargers.TryGetValue(chargeHandlerName, out ICyclopsCharger cyclopsCharger))
            {
                return (T)cyclopsCharger;
            }

            return null;
        }

        public ChargeManager(SubRoot cyclops)
        {
            Cyclops = cyclops;
            rechargePenalty = ModConfig.Main.RechargePenalty;
        }

        public bool Initialize(SubRoot cyclops)
        {
            QuickLogger.Debug("PowerManager InitializeChargingHandlers");

            foreach (CreateCyclopsCharger method in CyclopsChargers)
            {
                ICyclopsCharger charger = method.Invoke(Cyclops);

                ICollection<ICyclopsCharger> powerChargers = charger.IsRenewable ? RenewablePowerChargers : NonRenewablePowerChargers;

                if (!powerChargers.Contains(charger))
                {
                    powerChargers.Add(charger);
                    KnownChargers.Add(charger.Name, charger);
                }
                else
                {
                    QuickLogger.Warning($"Duplicate Reusable ICyclopsCharger '{charger.GetType()?.Name}' was blocked");
                }
            }

            return Initialized = true;
        }

        /// <summary>
        /// Gets the total available reserve power across all equipment upgrade modules.
        /// </summary>
        /// <returns>The <see cref="int"/> value of the total available reserve power.</returns>
        internal int GetTotalReservePower()
        {
            float availableReservePower = 0f;

            foreach (ICyclopsCharger charger in KnownChargers.Values)
                availableReservePower += charger.TotalReservePower();

            return Mathf.FloorToInt(availableReservePower);
        }

        /// <summary>
        /// Recharges the cyclops' power cells using all charging modules across all upgrade consoles.
        /// </summary>
        internal void RechargeCyclops()
        {
            if (Time.timeScale == 0f) // Is the game paused?
                return;

            // When in Creative mode or using the NoPower cheat, inform the chargers that there is no power deficit.
            // This is so that each charger can decide what to do individually rather than skip the entire charging cycle all together.
            float powerDeficit = GameModeUtils.RequiresPower()
                                 ? Cyclops.powerRelay.GetMaxPower() - Cyclops.powerRelay.GetPower()
                                 : 0f;

            this.HUDManager.UpdateTextVisibility();

            float producedPower = 0f;

            // Produce power from renewable energy first
            foreach (ICyclopsCharger charger in RenewablePowerChargers)
                producedPower += charger.ProducePower(powerDeficit);

            if (NonRenewablePowerChargers.Count > 0 && // Do we have non-renewable energy sources?
                powerDeficit - producedPower > MinimalPowerValue && // Did the renewable energy sources produce enough power to cover the deficit?
                powerDeficit > ModConfig.Main.MinimumEnergyDeficit) // Is the power deficit over the threshhold to start consuming non-renewable energy?
            {
                // Start producing power from non-renewable energy
                foreach (ICyclopsCharger charger in NonRenewablePowerChargers)
                    producedPower += charger.ProducePower(powerDeficit);
            }

            ChargeCyclops(producedPower, ref powerDeficit);
        }

        private void ChargeCyclops(float availablePower, ref float powerDeficit)
        {
            if (powerDeficit < MinimalPowerValue)
                return; // No need to charge

            if (availablePower < MinimalPowerValue)
                return; // No power available

            availablePower *= rechargePenalty;

            Cyclops.powerRelay.AddEnergy(availablePower, out float amtStored);
            powerDeficit = Mathf.Max(0f, powerDeficit - availablePower);
        }
    }
}
